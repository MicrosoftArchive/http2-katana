﻿// Copyright © Microsoft Open Technologies, Inc.
// All Rights Reserved       
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0

// THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.

// See the Apache 2 License for the specific language governing permissions and limitations under the License.

using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Http2.Protocol.Compression.HeadersDeltaCompression;
using Microsoft.Http2.Protocol.EventArgs;
using Microsoft.Http2.Protocol.Exceptions;
using OpenSSL;
using Microsoft.Http2.Protocol.Compression;
using Microsoft.Http2.Protocol.Framing;
using Microsoft.Http2.Protocol.IO;
using Microsoft.Http2.Protocol.FlowControl;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Http2.Protocol.Utils;
using OpenSSL.SSL;

namespace Microsoft.Http2.Protocol.Session
{
    /// <summary>
    /// This class creates and closes session, pumps incoming and outcoming frames and dispatches them.
    /// It defines events for request handling by subscriber. Also it is responsible for sending some frames.
    /// </summary>
    public partial class Http2Session : IDisposable
    {
        private bool _goAwayReceived;
        private FrameReader _frameReader;
        private OutgoingQueue _outgoingQueue;
        private Stream _ioStream;
        private ManualResetEvent _pingReceived = new ManualResetEvent(false);
        private ManualResetEvent _settingsAckReceived = new ManualResetEvent(false);
        private bool _disposed;
        private ICompressionProcessor _comprProc;
        private readonly FlowControlManager _flowCtrlManager;
        private readonly ConnectionEnd _ourEnd;
        private readonly ConnectionEnd _remoteEnd;
        private readonly bool _isSecure;
        private int _lastId;            //streams creation
        private int _lastPromisedId;    //check pushed  (server) streams ids
        private bool _wasSettingsReceived;
        private bool _wasResponseReceived;
        private bool _wasFirstConnectionWinSizeSent;
        private Frame _lastFrame;
        private readonly CancellationToken _cancelSessionToken;
        private readonly HeadersSequenceList _headersSequences; 
        private Dictionary<int, string> _promisedResources; 

        /// <summary>
        /// Occurs when settings frame was sent.
        /// </summary>
        public event EventHandler<SettingsSentEventArgs> OnSettingsSent;

        public event EventHandler<FrameReceivedEventArgs> OnFrameReceived;

        public event EventHandler<RequestSentEventArgs> OnRequestSent;

        /// <summary>
        /// Session closed event.
        /// </summary>
        public event EventHandler<System.EventArgs> OnSessionDisposed;

        /// <summary>
        /// Setting for max payload of frames
        /// </summary>
        public Int32 MaxFrameSize { get; set; }

        internal StreamDictionary StreamDictionary { get; private set; }

        /// <summary>
        /// How many parallel streams can our endpoint support
        /// Gets or sets our max concurrent streams.
        /// </summary>
        /// <value>
        /// Our max concurrent streams.
        /// </value>
        internal Int32 OurMaxConcurrentStreams { get; set; }

        /// <summary>
        /// How many parallel streams can our endpoint support
        /// Gets or sets the remote max concurrent streams.
        /// </summary>
        /// <value>
        /// The remote max concurrent streams.
        /// </value>
        internal Int32 RemoteMaxConcurrentStreams { get; set; }

        internal bool IsPushEnabled { get; private set; }

        public Http2Session(Stream stream, ConnectionEnd end, 
                            bool isSecure, CancellationToken cancel)
        {
            if (stream == null)
                throw new ArgumentNullException("stream is null");

            if (cancel == null)
                throw new ArgumentNullException("cancellation token is null");

            /*14 -> 6.5.2
            SETTINGS_MAX_FRAME_SIZE (0x5):  Indicates the size of the largest
            frame payload that a receiver is willing to accept.
            The initial value is 2^14 (16,384) octets.*/
            MaxFrameSize = 16384;

            _ourEnd = end;
            _isSecure = isSecure;

            _cancelSessionToken = cancel;

            if (_ourEnd == ConnectionEnd.Client)
            {
                _remoteEnd = ConnectionEnd.Server;
                _lastId = -1; // Streams opened by client are odd

                //if we got unsecure connection then server will respond with id == 1. We cant initiate 
                //new stream with id == 1.
                if (!(stream is SslStream))
                {
                    _lastId = 3;
                }
            }
            else
            {
                _remoteEnd = ConnectionEnd.Client;
                _lastId = 0; // Streams opened by server are even
            }

            _goAwayReceived = false;
            _comprProc = new CompressionProcessor();
            _ioStream = stream;

            _frameReader = new FrameReader(_ioStream);

            _outgoingQueue = new OutgoingQueue(_ioStream, _comprProc);

            OurMaxConcurrentStreams = Constants.DefaultMaxConcurrentStreams;
            RemoteMaxConcurrentStreams = Constants.DefaultMaxConcurrentStreams;            

            _headersSequences = new HeadersSequenceList();
            _promisedResources = new Dictionary<int, string>();

            StreamDictionary = new StreamDictionary();
            _flowCtrlManager = new FlowControlManager(StreamDictionary);

            for (int i = 0; i < OurMaxConcurrentStreams; i++)
            {
                var http2Stream = new Http2Stream(new HeadersList(), i + 1, _outgoingQueue, _flowCtrlManager)
                {
                    Idle = true,
                    MaxFrameSize = MaxFrameSize
                };
                StreamDictionary.Add(new KeyValuePair<int, Http2Stream>(i + 1, http2Stream));
            }

            _outgoingQueue.SetStreamDictionary(StreamDictionary);
        }

        private void SendConnectionPreface()
        {
            var bytes = Encoding.UTF8.GetBytes(Constants.ConnectionPreface);
            _ioStream.Write(bytes, 0 , bytes.Length);
        }

        private async Task<bool> TryGetConnectionPreface()
        {
            var buffer = new byte[Constants.ConnectionPreface.Length];

            try
            {
                int read = await _ioStream.ReadAsync(buffer, 0, buffer.Length, _cancelSessionToken);
                if (read == 0)
                {
                    throw new TimeoutException(String.Format("Connection preface was not received in timeout {0}",
                        _ioStream.ReadTimeout));
                }
            }
            catch (IOException ex)
            {
                Http2Logger.Error("Exception occured while getting connection preface: {0}", ex.Message);
                return false;
            }
            
            var receivedPreface = Encoding.UTF8.GetString(buffer);

            return string.Equals(receivedPreface, Constants.ConnectionPreface, StringComparison.OrdinalIgnoreCase);
        }

        // Calls only in unsecure connection case
        private void DispatchInitialRequest(IDictionary<string, string> initialRequest)
        {
            if (!initialRequest.ContainsKey(PseudoHeaders.Path))
            {
                initialRequest.Add(PseudoHeaders.Path, "/");
            }

            var initialStream = CreateStream(new HeadersList(initialRequest), 1);

            /* 14 -> 5.1.1
            HTTP/1.1 requests that are upgraded to HTTP/2 are
            responded to with a stream identifier of one (0x1).  After the
            upgrade completes, stream 0x1 is "half closed (local)" to the client.
            Therefore, stream 0x1 cannot be selected as a new stream identifier
            by a client that upgrades from HTTP/1.1. */
            if (_ourEnd == ConnectionEnd.Client)
            {
                GetNextId();
                initialStream.HalfClosedLocal = true;
            }
            else
            {
                initialStream.HalfClosedRemote = true;
                if (OnFrameReceived != null)
                {
                    OnFrameReceived(this, new FrameReceivedEventArgs(initialStream, new HeadersFrame(1, true)));
                }
            }
        }

        public async Task Start(IDictionary<string, string> initialRequest = null)
        {
            Http2Logger.Info("Http2 Session started");
            Http2Logger.Info(
                "Created {0} streams with initial window size={1}, initial connection window size={2}", 
                OurMaxConcurrentStreams, _flowCtrlManager.InitialWindowSize,
                _flowCtrlManager.ConnectionWindowSize);

            if (_ourEnd == ConnectionEnd.Server)
            {
                if (!await TryGetConnectionPreface())
                {
                    /* 14 -> 3.5
                    Clients and servers MUST terminate the TCP connection if either peer
                    does not begin with a valid connection preface.  A GOAWAY frame
                    (Section 6.8) can be omitted if it is clear that the peer is not
                    using HTTP/2. */
                    Http2Logger.Error("Invalid connection preface");
                    _goAwayReceived = true; // Close method will not send GOAWAY frame
                    Close(ResetStatusCode.ProtocolError);
                }
            }
            else
            {
                SendConnectionPreface();
            }
            // Listen for incoming Http/2 frames
            var incomingTask = new Task(() =>
                {
                    Thread.CurrentThread.Name = "Frame listening thread started";
                    PumpIncommingData();
                });

            // Send outgoing Http/2 frames
            var outgoingTask = new Task(() =>
                {
                    Thread.CurrentThread.Name = "Frame writing thread started";
                    PumpOutgoingData();
                });

            outgoingTask.Start();
            incomingTask.Start();

            // Write settings. Settings must be the first frame in session.
            if (_ourEnd == ConnectionEnd.Client)
            {
                WriteSettings(new[]
                    {
                        new SettingsPair(SettingsIds.InitialWindowSize,
                                            Constants.MaxFramePayloadSize)
                    }, false);

            }

            // Handle upgrade handshake headers.
            if (initialRequest != null && !_isSecure)
                DispatchInitialRequest(initialRequest);
            
            var endPumpsTask = Task.WhenAll(incomingTask, outgoingTask);

            // Cancellation token
            endPumpsTask.Wait();
        }

        /// <summary>
        /// Pumps the incomming data and calls dispatch for it
        /// </summary>
        private void PumpIncommingData()
        {
            while (!_goAwayReceived && !_disposed)
            {
                Frame frame;
                try
                {
                    frame = _frameReader.ReadFrame();

                    if (!_wasResponseReceived)
                    {
                        _wasResponseReceived = true;
                    }
                }
                catch (IOException)
                {
                    // Connection was closed by the remote endpoint
                    Http2Logger.Info("Connection was closed by the remote endpoint");
                    Dispose();
                    break;
                }
                catch (Exception)
                {
                    // Read failure, abort the connection/session.
                    Http2Logger.Info("Read failure, abort the connection/session");
                    Dispose();
                    break;
                }

                if (frame != null)
                {
                    DispatchIncomingFrame(frame);
                }
                else
                {
                    // Looks like connection was lost
                    //Dispose();
                    break;
                }
            }

            Http2Logger.Info("Read thread finished");
        }

        /// <summary>
        /// Pumps the outgoing data to outgoing queue
        /// </summary>
        /// <returns></returns>
        private void PumpOutgoingData()
        {
            try
            {
                _outgoingQueue.PumpToStream(_cancelSessionToken);
            }
            catch (OperationCanceledException)
            {
                Http2Logger.Error("Handling session was cancelled");
                Dispose();
            }
            catch (Exception)
            {
                Http2Logger.Info("Sending frame was cancelled because connection was lost");
                Dispose();
            }

            Http2Logger.Info("Write thread finished");
        }

        private void DispatchIncomingFrame(Frame frame)
        {
            Http2Stream stream = null;
            
            try
            {
                if (frame.PayloadLength > MaxFrameSize)
                {
                    throw new ProtocolError(ResetStatusCode.FrameSizeError,
                                            String.Format("Frame too large: Type: {0} {1}", frame.FrameType,
                                                          frame.PayloadLength));
                }

                /* 14 -> 6.5
                A SETTINGS frame MUST be sent by both endpoints at the start of a
                connection, and MAY be sent at any other time by either endpoint over
                the lifetime of the connection. */
                /* 14 -> 3.2.1
                The content of the "HTTP2-Settings" header field is the payload of a
                SETTINGS frame, encoded as a base64url string. Acknowledgement of the 
                SETTINGS parameters is not necessary, since a 101 response serves as
                implicit acknowledgment. */
                if (frame.FrameType != FrameType.Settings && !_wasSettingsReceived && _isSecure)
                {
                    throw new ProtocolError(ResetStatusCode.ProtocolError,
                                            "Settings frame was not the first frame in the session");
                }

                switch (frame.FrameType)
                {
                    case FrameType.Headers:
                        HandleHeaders(frame as HeadersFrame, out stream);
                        break;
                    case FrameType.Continuation:
                        HandleContinuation(frame as ContinuationFrame, out stream);
                        break;
                    case FrameType.Priority:
                        HandlePriority(frame as PriorityFrame, out stream);
                        break;
                    case FrameType.RstStream:
                        HandleRstFrame(frame as RstStreamFrame, out stream);
                        break;
                    case FrameType.Data:
                        HandleDataFrame(frame as DataFrame, out stream);
                        break;
                    case FrameType.Ping:
                        HandlePingFrame(frame as PingFrame);
                        break;
                    case FrameType.Settings:
                        HandleSettingsFrame(frame as SettingsFrame);
                        if (!(frame as SettingsFrame).IsAck)
                        {
                            // sending ACK settings
                            WriteSettings(new SettingsPair[0], true);
                        }
                        break;
                    case FrameType.WindowUpdate:
                        HandleWindowUpdateFrame(frame as WindowUpdateFrame, out stream);
                        break;
                    case FrameType.GoAway:
                        HandleGoAwayFrame(frame as GoAwayFrame);
                        break;
                    case FrameType.PushPromise:
                        HandlePushPromiseFrame(frame as PushPromiseFrame, out stream);
                        if (stream != null) //This means that sequence is complete
                        {
                            _promisedResources.Add(stream.Id, stream.Headers.GetValue(PseudoHeaders.Path));
                        }
                        break;
                    default:
                        /* 14 -> 5.5
                        Implementations MUST discard frames that 
                        unknown or unsupported types */
                        break;
                }

                _lastFrame = frame;

                if (frame is IEndStreamFrame && ((IEndStreamFrame) frame).IsEndStream)
                {
                    // Tell the stream that it was the last frame
                    Http2Logger.Debug("Final frame for stream id=" + stream.Id);
                    stream.HalfClosedRemote = true;

                    // Promised resource has been pushed
                    if (_promisedResources.ContainsKey(stream.Id))
                        _promisedResources.Remove(stream.Id);
                }

                if (stream == null || OnFrameReceived == null) 
                    return;

                OnFrameReceived(this, new FrameReceivedEventArgs(stream, frame));
                stream.FramesReceived++;
            }

            /* 14 -> 5.1
            An endpoint MUST NOT send frames on a closed stream.  An endpoint
            that receives any frame other than PRIORITY after receiving a
            RST_STREAM MUST treat that as a stream error of type STREAM_CLOSED. */
            catch (Http2StreamNotFoundException ex)
            {
                Http2Logger.Warn(
                    "Frame for already Closed stream: streamId={0}, WasRstOnStream={1}", 
                    ex.Id, stream.WasRstOnStream);

                /* 14 -> 5.4.2
                An endpoint MUST NOT send a RST_STREAM in response to an RST_STREAM
                frame, to avoid looping. */
                /* 14 -> 5.1
                An endpoint MUST ignore frames that it receives on
                closed streams after it has sent a RST_STREAM frame. */   
                if (!stream.WasRstOnStream)
                {
                    var rstFrame = new RstStreamFrame(ex.Id, ResetStatusCode.StreamClosed);
                    _outgoingQueue.WriteFrame(rstFrame);
                    Http2Logger.FrameSend(rstFrame);
                    stream.WasRstOnStream = true;
                }                          
            }
            catch (CompressionError ex)
            {
                // The endpoint is unable to maintain the compression context for the connection.
                Http2Logger.Error("Compression error occurred: " + ex.Message);
                Close(ResetStatusCode.CompressionError);
            }
            catch (ProtocolError pEx)
            {
                Http2Logger.Error("Protocol error occurred: " + pEx.Message);
                Close(pEx.Code);
            }
            catch (MaxConcurrentStreamsLimitException)
            {
                // Remote side tries to open more streams than allowed
                Dispose();
            }
            catch (Exception ex)
            {
                Http2Logger.Error("Unknown error occurred: " + ex.Message);
                Close(ResetStatusCode.InternalError);
            }
        }

        public Http2Stream CreateStream(HeadersList headers, int streamId, int priority = -1)
        {
            if (headers == null)
                throw new ArgumentNullException("pairs is null");

            if (priority == -1)
                priority = Constants.DefaultStreamPriority;

            if (priority < 0 || priority > Constants.MaxPriority)
                throw new ArgumentOutOfRangeException("priority is not between 0 and MaxPriority");

            if (StreamDictionary.GetOpenedStreamsBy(_remoteEnd) + 1 > OurMaxConcurrentStreams)
            {
                throw new MaxConcurrentStreamsLimitException();
            }

            var streamSequence = new HeadersSequence(streamId, (new HeadersFrame(streamId, true){Headers = headers}));
            _headersSequences.Add(streamSequence);

            var stream = StreamDictionary[streamId];
            stream.OnFrameSent += (o, args) =>
                {
                    if (!(args.Frame is IHeadersFrame))
                        return;

                    var streamSeq = _headersSequences.Find(stream.Id);
                    streamSeq.AddHeaders(args.Frame as IHeadersFrame);
                };

            stream.OnClose += (o, args) =>
                {
                    var streamSeq = _headersSequences.Find(stream.Id);

                    if (streamSeq != null)
                        _headersSequences.Remove(streamSeq);
                };
            stream.Priority = priority;
            stream.Headers = headers;
            stream.Opened = true;

            return stream;
        }

        internal Http2Stream CreateStream(HeadersSequence sequence)
        {
            if (sequence == null)
                throw new ArgumentNullException("sequence is null");

            if (sequence.Priority < 0 || sequence.Priority > Constants.MaxPriority)
                throw new ArgumentOutOfRangeException("priority is not between 0 and MaxPriority");

            if (StreamDictionary.GetOpenedStreamsBy(_remoteEnd) + 1 > OurMaxConcurrentStreams)
            {
                throw new MaxConcurrentStreamsLimitException();
            }

            int id = sequence.StreamId;
            int priority = sequence.Priority;
            var headers = sequence.Headers;

            var stream = StreamDictionary[id];
            stream.OnFrameSent += (o, args) =>
            {
                if (!(args.Frame is IHeadersFrame))
                    return;

                var streamSeq = _headersSequences.Find(stream.Id);
                streamSeq.AddHeaders(args.Frame as IHeadersFrame);
            };

            stream.OnClose += (o, args) =>
            {
                var streamSeq = _headersSequences.Find(stream.Id);

                if (streamSeq != null)
                    _headersSequences.Remove(streamSeq);
            };

            stream.Headers = headers;
            stream.Priority = priority;
            stream.Opened = true;

            return stream;
        }

        /// <summary>
        /// Gets the next id.
        /// </summary>
        /// <returns>Next stream id</returns>
        private int GetNextId()
        {
            _lastId += 2;
            return _lastId;
        }

        /// <summary>
        /// Creates new http2 stream.
        /// </summary>
        /// <param name="priority">The stream priority.</param>
        /// <returns></returns>
        /// <exception cref="System.InvalidOperationException">Thrown when trying to create more streams than allowed by the remote side</exception>
        public Http2Stream CreateStream(int priority)
        {
            if (priority < 0 || priority > Constants.MaxPriority)
                throw new ArgumentOutOfRangeException("priority is not between 0 and MaxPriority");

            if (StreamDictionary.GetOpenedStreamsBy(_ourEnd) + 1 > RemoteMaxConcurrentStreams)
            {
                throw new MaxConcurrentStreamsLimitException();
            }
            int nextId = GetNextId();
            var stream = StreamDictionary[nextId];
            var streamSequence = new HeadersSequence(nextId, (new HeadersFrame(nextId, true)));
            _headersSequences.Add(streamSequence);

            stream.OnFrameSent += (o, args) =>
            {
                if (!(args.Frame is IHeadersFrame))
                    return;

                var streamSeq = _headersSequences.Find(nextId);
                streamSeq.AddHeaders(args.Frame as IHeadersFrame);
            };

            stream.OnClose += (o, args) =>
                {
                    var streamSeq = _headersSequences.Find(stream.Id);

                    if (streamSeq != null)
                        _headersSequences.Remove(streamSeq);
                };

            stream.Priority = priority;
            stream.Opened = true;

            return stream;
        }

        /// <summary>
        /// Sends the headers with request headers.
        /// </summary>
        /// <param name="pairs">The header pairs.</param>
        /// <param name="priority">The stream priority.</param>
        /// <param name="isEndStream">True if initial headers+priority is also the final frame from endpoint.</param>
        public void SendRequest(HeadersList pairs, int priority, bool isEndStream)
        {
            if (_ourEnd == ConnectionEnd.Server)
                throw new ProtocolError(ResetStatusCode.ProtocolError, "Server should not initiate request");

            if (pairs == null)
                throw new ArgumentNullException("pairs is null");

            if (priority < 0 || priority > Constants.MaxPriority)
                throw new ArgumentOutOfRangeException("priority is not between 0 and MaxPriority");

            var path = pairs.GetValue(PseudoHeaders.Path);

            if (path == null)
                throw new ProtocolError(ResetStatusCode.ProtocolError, "Invalid request ex");

            /* 14 -> 8.2.2
            Once a client receives a PUSH_PROMISE frame and chooses to accept the
            pushed resource, the client SHOULD NOT issue any requests for the
            promised resource until after the promised stream has closed. */

            if (_promisedResources.ContainsValue(path))
                throw new ProtocolError(ResetStatusCode.ProtocolError, "Resource has been promised. Client should not request it.");

            var stream = CreateStream(priority);

            stream.WriteHeadersFrame(pairs, isEndStream, true);

            var streamSequence = _headersSequences.Find(stream.Id);
            streamSequence.AddHeaders(new HeadersFrame(stream.Id, true) { Headers = pairs });

            if (OnRequestSent != null)
            {
                OnRequestSent(this, new RequestSentEventArgs(stream));
            }
        }

        /// <summary>
        /// Gets the stream from stream dictionary.
        /// </summary>
        /// <param name="id">The stream id.</param>
        /// <returns></returns>
        internal Http2Stream GetStream(int id)
        {
            Http2Stream stream;
            if (!StreamDictionary.TryGetValue(id, out stream))
            {
                return null;
            }
            return stream;
        }

        /// <summary>
        /// Writes the SETTINGS frame.
        /// </summary>
        /// <param name="settings">The settings pairs.</param>
        /// <param name="isAck">The ACK flag.</param>
        public void WriteSettings(SettingsPair[] settings, bool isAck)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");

            var frame = new SettingsFrame(new List<SettingsPair>(settings), isAck);

            Http2Logger.FrameSend(frame);

            _outgoingQueue.WriteFrame(frame);

            if (!isAck && !_settingsAckReceived.WaitOne(60000))
            {
                WriteGoAway(ResetStatusCode.SettingsTimeout);
                Dispose();
            }
            
            _settingsAckReceived.Reset();

            if (OnSettingsSent != null)
            {
                OnSettingsSent(this, new SettingsSentEventArgs(frame));
            }
        }

        /// <summary>
        /// Writes the GOAWAY frame.
        /// </summary>
        /// <param name="code">The Reset Status code.</param>
        public void WriteGoAway(ResetStatusCode code)
        {           
            //if there were no streams opened
            if (_lastId == -1)
            {
                _lastId = 0; //then set lastId to 0 as spec tells. (See GoAway chapter)
            }

            var frame = new GoAwayFrame(_lastId, code);

            Http2Logger.FrameSend(frame);

            _outgoingQueue.WriteFrame(frame);
        }

        /// <summary>
        /// Pings session.
        /// </summary>
        /// <returns></returns>
        public TimeSpan Ping()
        {
            var pingFrame = new PingFrame(false);
            _outgoingQueue.WriteFrame(pingFrame);
            var now = DateTime.UtcNow;

            if (!_pingReceived.WaitOne(3000))
            {
                //Remote endpoint was not answer at time.
                Dispose();
            }
            _pingReceived.Reset();

            var newNow = DateTime.UtcNow;
            Http2Logger.Info("Ping: " + (newNow - now).Milliseconds);
            return newNow - now;
        }

        /// <summary>
        /// Writes WINDOW_UPDATE frame for entire connection.
        /// </summary>
        public void WriteConnectionWindowUpdate(int windowSize)
        {
            /* 14 -> 6.9
            The WINDOW_UPDATE frame can be specific to a stream or to the entire
            connection.  In the former case, the frame's stream identifier
            indicates the affected stream; in the latter, the value "0" indicates
            that the entire connection is the subject of the frame. */
            var frame = new WindowUpdateFrame(0, windowSize);
            Http2Logger.FrameSend(frame);
            _outgoingQueue.WriteFrame(frame);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            Close(ResetStatusCode.None);
        }

        private void Close(ResetStatusCode status)
        {
            if (_disposed)
                return;

            Http2Logger.Info("Http2 Session closing: status={0}", status);

            _disposed = true;

            // Dispose of all streams
            foreach (var stream in StreamDictionary.Values)
            {
                // Cancel all opened streams
                stream.Close(ResetStatusCode.None);
            }

            if (!_goAwayReceived)
            {
                WriteGoAway(status);

                // TODO: fix delay. wait for goAway send and then dispose OutgoingQueue
                using (var goAwayDelay = new ManualResetEvent(false))
                {
                    goAwayDelay.WaitOne(500);
                }
            }
            OnSettingsSent = null;
            OnFrameReceived = null;

            if (_frameReader != null)
            {
                _frameReader.Dispose();
                _frameReader = null;
            }

            if (_outgoingQueue != null)
            {
                _outgoingQueue.Flush();
                _outgoingQueue.Dispose();
            }

            if (_comprProc != null)
            {
                _comprProc.Dispose();
                _comprProc = null;
            }

            if (_ioStream != null)
            {
                _ioStream.Close();
                _ioStream = null;
            }

            if (_pingReceived != null)
            {
                _pingReceived.Dispose();
                _pingReceived = null;
            }

            if (_settingsAckReceived != null)
            {
                _settingsAckReceived.Dispose();
                _settingsAckReceived = null;
            }

            if (OnSessionDisposed != null)
            {
                OnSessionDisposed(this, null);
            }

            OnSessionDisposed = null;

            Http2Logger.Info("Http2 Session closed: status={0}", status);
        }
    }
}

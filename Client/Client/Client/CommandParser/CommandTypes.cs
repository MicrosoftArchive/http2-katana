﻿namespace Client
{
    internal enum CommandType : byte
    {
        None = 0,
        Connect = 1,
        Get = 2,
        Disconnect = 3,
        CaptureStatsOn = 4,
        CaptureStatsOff = 5,
        Dir = 6,
        Exit = 7,
        Help = 8,
        Unknown = 9,
        Empty = 10
    }
}

namespace O2JamPatchServer;

public enum RequestCommand : ushort
{
    GetVersion           = 0x0000, // 0
    GetDownloadLocations = 0x0002, // 2
    GetGameServers       = 0x0009, // 9
}

public enum ResponseCommand : ushort
{
    GetVersion           = 0x0001, // 1
    GetDownloadLocations = 0x0003, // 3
    GetGameServers       = 0x000A, // 10
}

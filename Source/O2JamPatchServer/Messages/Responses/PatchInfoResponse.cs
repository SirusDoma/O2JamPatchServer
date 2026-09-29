using Encore.Messaging;

namespace O2JamPatchServer.Messages.Responses;

public class PatchInfoResponse : IMessage
{
    public static Enum Command => ResponseCommand.GetVersion;

    [MessageField(order: 0)]
    public uint VersionResult { get; init; }

    [StringMessageField(order: 1)]
    public required string GameVersion { get; init; }

    [StringMessageField(order: 2)]
    public required string PatchClientVersion { get; init; }

    [StringMessageField(order: 3)]
    public required string LauncherVersion { get; init; }

    [StringMessageField(order: 4)]
    public required string MusicListVersion { get; init; }

    [MessageField(order: 5)]
    public uint LocationsResult { get; init; }

    [StringMessageField(order: 6)]
    public required string FtpHost { get; init; }

    [StringMessageField(order: 7)]
    public required string FtpRoot { get; init; }

    [StringMessageField(order: 8)]
    public required string MusicServer { get; init; }

    [StringMessageField(order: 9)]
    public required string MusicPath { get; init; }

    [MessageField(order: 10)]
    public uint ServersResult { get; init; }

    [CollectionMessageField(order: 11, prefixSizeType: TypeCode.UInt32)]
    public required IReadOnlyList<GameServer> Servers { get; init; }

    [StringMessageField(order: 12)]
    public required string MinimumGameVersion { get; init; }
}

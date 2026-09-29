using Encore.Messaging;

namespace O2JamPatchServer.Messages.Responses;

public class DownloadLocationsResponse : IMessage
{
    public static Enum Command => ResponseCommand.GetDownloadLocations;

    [MessageField(order: 0)]
    public uint Result { get; init; }

    [StringMessageField(order: 1)]
    public required string FtpHost { get; init; }

    [StringMessageField(order: 2)]
    public required string FtpRoot { get; init; }

    [StringMessageField(order: 3)]
    public required string MusicServer { get; init; }

    [StringMessageField(order: 4)]
    public required string MusicPath { get; init; }
}

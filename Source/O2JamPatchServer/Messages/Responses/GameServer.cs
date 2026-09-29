using Encore.Messaging;

namespace O2JamPatchServer.Messages.Responses;

public class GameServer : SubMessage
{
    [StringMessageField(order: 0)]
    public required string Host { get; init; }

    [MessageField(order: 1)]
    public uint Port { get; init; }
}

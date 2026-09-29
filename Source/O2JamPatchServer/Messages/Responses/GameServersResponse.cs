using Encore.Messaging;

namespace O2JamPatchServer.Messages.Responses;

public class GameServersResponse : IMessage
{
    public static Enum Command => ResponseCommand.GetGameServers;

    [MessageField(order: 0)]
    public uint Result { get; init; }

    [CollectionMessageField(order: 1, prefixSizeType: TypeCode.UInt32)]
    public required IReadOnlyList<GameServer> Servers { get; init; }
}

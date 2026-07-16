using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    // Client to server chat send. A dedicated, exact-size binary WS message
    // rather than a CommandType riding on ClientCommandPacket - unlike
    // gameplay commands, a chat message is free-form user text that does not
    // fit any existing ClientCommandPacket field, and chat is a real-time,
    // broadcast-to-everyone concern independent of the 10Hz simulation tick,
    // not a per-player state mutation. HandleClientLoopAsync's receive loop
    // recognizes this by its exact byte size (see NetworkPacketLayoutGuard),
    // checked before the general ClientCommandPacket size check so the two
    // message types never collide.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct RequestChatMessagePacket
    {
        public const int MessageCapacity = 128;

        public ushort MessageLength;

        // Modul: 0 = Global, 1 = Guild (see ChatEngine.GlobalChannelType/
        // GuildChannelType). Guild-channel messages are routed strictly to
        // the sender's own guild members by NetworkBroadcastSystem, using
        // the sender's server-cached GuildId, never a client-supplied one.
        public byte ChannelType;
        public fixed byte MessageText[MessageCapacity];
    }
}

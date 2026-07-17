using System.Runtime.InteropServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

        // Modul: 0 = Global, 1 = Guild, 2 = Whisper (see ChatEngine.
        // GlobalChannelType/GuildChannelType/WhisperChannelType).
        // Guild-channel messages are routed strictly to the sender's own
        // guild members by NetworkBroadcastSystem, using the sender's
        // server-cached GuildId, never a client-supplied one.
        public byte ChannelType;

        // Modul: Full-Stack Social Layer, Part 3. Only read when
        // ChannelType == WhisperChannelType - the intended recipient's
        // PlayerId. Ignored (must be 0) for Global/Guild sends.
        public long TargetPlayerId;
        public fixed byte MessageText[MessageCapacity];
    }
}

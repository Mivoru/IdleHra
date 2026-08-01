using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    // Modul: mirrors server ChatEngine.GlobalChannelType/GuildChannelType/
    // WhisperChannelType exactly (0/1/2) - the raw byte values are what
    // actually go over the wire (RequestChatMessagePacket.ChannelType),
    // this enum only exists for call-site readability
    // (WebSocketClient.SendChatMessageZeroAlloc).
    public enum ChatChannelType : byte
    {
        Global = 0,
        Guild = 1,
        Whisper = 2,

        // Modul: high-rarity announcements, 2026-08-01. Server-authored, never
        // sent BY a client - the send paths all take Global/Guild/Whisper. It
        // exists so the World window can tell an announcement apart from an
        // ordinary message and render it with rarity colour and a congratulate
        // button, without parsing message text to find out.
        Announcement = 3
    }

    // Client to server chat send. A dedicated, exact-size binary WS message
    // rather than a CommandType riding on ClientCommandPacket - see the
    // matching server-side struct for the full rationale. WebSocketClient's
    // receive/send code recognizes this by its exact byte size, distinct
    // from every other packet size in this wire protocol (see
    // NetworkPacketLayoutGuard).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct RequestChatMessagePacket
    {
        public const int MessageCapacity = 128;

        public ushort MessageLength;

        // Modul: 0 = Global, 1 = Guild, 2 = Whisper - mirrors server
        // ChatEngine.GlobalChannelType/GuildChannelType/WhisperChannelType
        // exactly.
        public byte ChannelType;

        // Modul: Full-Stack Social Layer, Part 3. Only read server-side
        // when ChannelType == Whisper - the intended recipient's PlayerId.
        // Leave 0 for Global/Guild sends.
        public long TargetPlayerId;
        public fixed byte MessageText[MessageCapacity];
    }
}

using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    // Modul: mirrors server ChatEngine.GlobalChannelType/GuildChannelType
    // exactly (0/1) - the raw byte values are what actually go over the
    // wire (RequestChatMessagePacket.ChannelType), this enum only exists
    // for call-site readability (WebSocketClient.SendChatMessageZeroAlloc).
    public enum ChatChannelType : byte
    {
        Global = 0,
        Guild = 1
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

        // Modul: 0 = Global, 1 = Guild - mirrors server ChatEngine.
        // GlobalChannelType/GuildChannelType exactly.
        public byte ChannelType;
        public fixed byte MessageText[MessageCapacity];
    }
}

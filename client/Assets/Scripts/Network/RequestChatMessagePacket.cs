using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
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
        public fixed byte MessageText[MessageCapacity];
    }
}

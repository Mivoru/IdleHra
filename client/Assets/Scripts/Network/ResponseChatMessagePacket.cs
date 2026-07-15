using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    // Server to client chat broadcast. Sent as its own exact-size binary WS
    // message (see RequestChatMessagePacket) whenever any pod's Redis
    // Pub/Sub subscription receives a message. WebSocketClient's receive
    // loop recognizes this by its exact byte size, distinct from every
    // other packet size in this wire protocol (see NetworkPacketLayoutGuard).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ResponseChatMessagePacket
    {
        public const int MessageCapacity = 128;

        public long SenderPlayerId;
        public long TimestampEpochMs;
        public ushort MessageLength;
        public fixed byte MessageText[MessageCapacity];
    }
}

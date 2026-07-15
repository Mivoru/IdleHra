using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    // Server to client chat broadcast. Sent as its own exact-size binary WS
    // message (see RequestChatMessagePacket for why chat does not ride on
    // StateUpdatePacket or ClientCommandPacket) whenever this pod's Redis
    // Pub/Sub subscription (see ChatEngine) receives a message published by
    // any pod, including itself. WebSocketClient's receive loop recognizes
    // this by its exact byte size, distinct from every other packet size in
    // this wire protocol (see NetworkPacketLayoutGuard).
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

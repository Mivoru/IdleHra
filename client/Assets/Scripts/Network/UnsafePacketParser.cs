using System.Runtime.CompilerServices;

namespace FolkIdle.Client.Network
{
    public static class UnsafePacketParser
    {
        // Guards against truncated or corrupt inbound buffers before any raw
        // pointer read - a WebSocket delivery that ends short (a torn frame,
        // a stalled or malicious relay) must never be walked into
        // ReadUnaligned<StateUpdatePacket>, which reads a fixed-size span
        // starting at offset 0 regardless of how many bytes were actually
        // received into the buffer.
        public static unsafe bool TryParseState(byte[] buffer, int receivedCount, out StateUpdatePacket packet)
        {
            int requiredSize = Unsafe.SizeOf<StateUpdatePacket>();
            if (buffer == null || receivedCount < requiredSize || buffer.Length < requiredSize)
            {
                packet = default;
                return false;
            }

            fixed (byte* ptr = buffer)
            {
                packet = Unsafe.ReadUnaligned<StateUpdatePacket>(ptr);
            }
            return true;
        }

        // Mirrors TryParseState's exact truncated-buffer guard, for the
        // separately-sized ResponseChatMessagePacket wire message.
        public static unsafe bool TryParseChatMessage(byte[] buffer, int receivedCount, out ResponseChatMessagePacket packet)
        {
            int requiredSize = Unsafe.SizeOf<ResponseChatMessagePacket>();
            if (buffer == null || receivedCount < requiredSize || buffer.Length < requiredSize)
            {
                packet = default;
                return false;
            }

            fixed (byte* ptr = buffer)
            {
                packet = Unsafe.ReadUnaligned<ResponseChatMessagePacket>(ptr);
            }
            return true;
        }
    }
}

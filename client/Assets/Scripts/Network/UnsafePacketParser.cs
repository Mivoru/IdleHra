using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    public static class UnsafePacketParser
    {
        public static unsafe StateUpdatePacket ParseState(byte[] buffer)
        {
            fixed (byte* ptr = buffer)
            {
                return Unsafe.ReadUnaligned<StateUpdatePacket>(ptr);
            }
        }
    }
}

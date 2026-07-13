using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Server.Network
{
    public static class NetworkPacketLayoutGuard
    {
        public const int ExpectedClientCommandSize = 384;
        public const int ExpectedStateUpdateSize = 584;

        public static void Validate()
        {
            int stateSize = Unsafe.SizeOf<StateUpdatePacket>();
            if (stateSize != ExpectedStateUpdateSize)
            {
                throw new InvalidOperationException($"StateUpdatePacket byte layout mismatch. Expected {ExpectedStateUpdateSize}, got {stateSize}.");
            }

            int commandSize = Unsafe.SizeOf<ClientCommandPacket>();
            if (commandSize != ExpectedClientCommandSize)
            {
                throw new InvalidOperationException($"ClientCommandPacket byte layout mismatch. Expected {ExpectedClientCommandSize}, got {commandSize}.");
            }
        }
    }
}

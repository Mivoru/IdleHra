using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Server.Network
{
    public static class NetworkPacketLayoutGuard
    {
        public const int ExpectedClientCommandSize = 384;
        public const int ExpectedStateUpdateSize = 663;
        public const int ExpectedAuthHandshakeSize = 530;

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

            int authSize = Unsafe.SizeOf<AuthHandshakePacket>();
            if (authSize != ExpectedAuthHandshakeSize)
            {
                throw new InvalidOperationException($"AuthHandshakePacket byte layout mismatch. Expected {ExpectedAuthHandshakeSize}, got {authSize}.");
            }
        }
    }
}

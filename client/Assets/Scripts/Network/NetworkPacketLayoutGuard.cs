using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Client.Network
{
    public static class NetworkPacketLayoutGuard
    {
        // Modul: Full-Stack Production Hardening Phase 3, Part 4. Mirrors
        // server NetworkPacketLayoutGuard exactly - see that file's own
        // comment for the byte-count breakdown.
        public const int ExpectedClientCommandSize = 352;
        public const int ExpectedStateUpdateSize = 689;
        public const int ExpectedAuthHandshakeSize = 530;
        public const int ExpectedRequestChatMessageSize = 131;
        public const int ExpectedResponseChatMessageSize = 147;

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

            int requestChatSize = Unsafe.SizeOf<RequestChatMessagePacket>();
            if (requestChatSize != ExpectedRequestChatMessageSize)
            {
                throw new InvalidOperationException($"RequestChatMessagePacket byte layout mismatch. Expected {ExpectedRequestChatMessageSize}, got {requestChatSize}.");
            }

            int responseChatSize = Unsafe.SizeOf<ResponseChatMessagePacket>();
            if (responseChatSize != ExpectedResponseChatMessageSize)
            {
                throw new InvalidOperationException($"ResponseChatMessagePacket byte layout mismatch. Expected {ExpectedResponseChatMessageSize}, got {responseChatSize}.");
            }
        }
    }
}

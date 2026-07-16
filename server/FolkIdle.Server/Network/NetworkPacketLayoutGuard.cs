using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Server.Network
{
    public static class NetworkPacketLayoutGuard
    {
        // Modul: Full-Stack Production Hardening Phase 3, Part 4. Shrank
        // again - ClientCommandPacket from 384 to 352 (TelemetryBurstPadding,
        // SecurityPadding, Sprint70ExpansionPadding[24] removed - 32 bytes
        // of dead reserved filler, never read or written by any code on
        // either side); StateUpdatePacket from 696 to 680 (34 bytes of
        // dead *Reserved* filler removed, offset by the +18 bytes the
        // Part 5 command-result ring buffer added replacing a 2-byte
        // scalar with 4 explicit byte+uint slot pairs).
        public const int ExpectedClientCommandSize = 352;
        public const int ExpectedStateUpdateSize = 680;
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

            if (ExpectedRequestChatMessageSize == ExpectedClientCommandSize || ExpectedRequestChatMessageSize == ExpectedStateUpdateSize || ExpectedRequestChatMessageSize == ExpectedAuthHandshakeSize ||
                ExpectedResponseChatMessageSize == ExpectedClientCommandSize || ExpectedResponseChatMessageSize == ExpectedStateUpdateSize || ExpectedResponseChatMessageSize == ExpectedAuthHandshakeSize ||
                ExpectedRequestChatMessageSize == ExpectedResponseChatMessageSize)
            {
                throw new InvalidOperationException("Packet size collision detected - the WS receive loops on both sides distinguish inbound message types by exact byte size, so every packet type must have a unique size.");
            }
        }
    }
}

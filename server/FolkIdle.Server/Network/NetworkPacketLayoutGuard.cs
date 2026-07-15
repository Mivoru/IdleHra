using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Server.Network
{
    public static class NetworkPacketLayoutGuard
    {
        public const int ExpectedClientCommandSize = 384;
        public const int ExpectedStateUpdateSize = 703;
        public const int ExpectedAuthHandshakeSize = 530;
        public const int ExpectedRequestChatMessageSize = 130;
        public const int ExpectedResponseChatMessageSize = 146;

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

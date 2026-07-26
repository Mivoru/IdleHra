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

        // Modul: Play Mode audit fix. 689 -> 691: TownHallLevel +
        // CraftingWorkshopLevel (1 byte each) - mirrors server
        // NetworkPacketLayoutGuard exactly.
        public const int ExpectedStateUpdateSize = 699;
        public const int ExpectedAuthHandshakeSize = 530;

        // Modul: Full-Stack Social Layer, Part 3. 131 -> 139: Whisper
        // channel routing added TargetPlayerId (8 bytes, long) - mirrors
        // server NetworkPacketLayoutGuard exactly.
        public const int ExpectedRequestChatMessageSize = 139;
        public const int ExpectedResponseChatMessageSize = 147;

        // Modul: Loot Event Feed. 22 bytes: PlayerId(8) + ItemId(4) +
        // Quantity(4) + MonsterId(4) + QualityTier(1) + DropKind(1) -
        // mirrors server NetworkPacketLayoutGuard exactly.
        public const int ExpectedResponseLootDropSize = 22;

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

            int lootDropSize = Unsafe.SizeOf<ResponseLootDropPacket>();
            if (lootDropSize != ExpectedResponseLootDropSize)
            {
                throw new InvalidOperationException($"ResponseLootDropPacket byte layout mismatch. Expected {ExpectedResponseLootDropSize}, got {lootDropSize}.");
            }
        }
    }
}

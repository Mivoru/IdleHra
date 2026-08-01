using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Client.Network
{
    public static class NetworkPacketLayoutGuard
    {
        // Modul: Full-Stack Production Hardening Phase 3, Part 4. Mirrors
        // server NetworkPacketLayoutGuard exactly - see that file's own
        // comment for the byte-count breakdown.
        // 352 -> 359: reroll operation kind (1), auto max attempts (4),
        // stop min rarity (1), stop affix index (1). Both guards move in
        // the same commit - the client copy silently drifted once before
        // and threw on every startup.
        public const int ExpectedClientCommandSize = 359;

        // Modul: offhand slot. 686 -> 694: EquippedOffhandId (8 bytes, long).
        //
        // This constant was STALE at 699 and had been since the wire compaction
        // (698 -> 686) landed: that commit updated the client packet struct and
        // the server's copy of this guard, but not this file. Validate() is
        // called unguarded from WebSocketClient.Start(), so it threw
        // "byte layout mismatch. Expected 699, got 686" on every startup,
        // before FlightRecorder.Initialize() and ClientContentRegistry.
        // Initialize() on the two lines after it ever ran. Mirrors server
        // NetworkPacketLayoutGuard exactly - the two must be changed together.
        // Modul: race unlock feedback. 694 -> 695: UnlockedRaceBitmask (1 byte).
        public const int ExpectedStateUpdateSize = 695;
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

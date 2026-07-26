using System;
using System.Runtime.CompilerServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

        // Modul: Full-Stack Expansion, Part 1. 680 -> 689: the Leggings
        // equipment slot added EquippedLeggingsId (8 bytes) +
        // EquippedLeggingsAffixLocked (1 byte). Still strictly under the
        // 700-byte structural ceiling the tests pin.
        //
        // Modul: Play Mode audit fix. 689 -> 691: TownHallLevel +
        // CraftingWorkshopLevel (1 byte each). 691 -> 699: LegacyPerksBitmask
        // (8 bytes) - see StateUpdatePacket's own comments. Still under the
        // 700-byte ceiling, but with only 1 byte of headroom left before the
        // next addition needs to either shrink something else or move the
        // ceiling.
        //
        // Modul: larder + halt reasons. 699 -> 694, the first time this packet
        // has shrunk while gaining fields. Added 13 bytes (six ushort larder
        // slots + one ActivityHaltReason byte); removed 18 by deleting
        // IsQuarantineActive (a duplicate of Quarantine_Active),
        // UiScreenShakeIntensity (never written or read anywhere), and the
        // three server-diagnostic gauges TotalAnalyticsEventsLoggedCount,
        // VisualActiveConnectionThroughput and CurrentNodeMemoryLoadMetrics -
        // the last of which called GC.GetTotalMemory once per player per tick
        // to ship the server's own heap size to a client that never read it.
        // See StateUpdatePacket's own comment. Headroom under the 700-byte
        // ceiling the tests pin is back to 6 bytes, 4 of which the inventory
        // census then spent on InventoryCapacity: 694 -> 698.
        //
        // Modul: wire compaction for 6-slot equipment. 698 -> 686, the second
        // time this packet has shrunk while gaining fields. Added 30 bytes
        // (EquippedHelmetId/GlovesId/BootsId, the Slot2/Slot3 activity ids and
        // their halt reasons); reclaimed 42 by deleting eight fields that were
        // mirrored into VisualSyncProxy properties no UI element reads -
        // ActiveMatchId, LogicalEpochFrameIndex, ActiveChronoEngineStatus,
        // ActiveBankedChronoSeconds, VisualActiveMatchMmr, ActiveMasteryBitmask,
        // CraftingEngineStatus and NotificationQueueStateLength. See
        // StateUpdatePacket's own comment for why each one went.
        //
        // Headroom under the 700-byte ceiling the tests pin: 14 bytes.
        public const int ExpectedStateUpdateSize = 686;
        public const int ExpectedAuthHandshakeSize = 530;

        // Modul: Full-Stack Social Layer, Part 3. 131 -> 139: Whisper
        // channel routing added TargetPlayerId (8 bytes, long).
        public const int ExpectedRequestChatMessageSize = 139;
        public const int ExpectedResponseChatMessageSize = 147;

        // Modul: Loot Event Feed. 22 bytes: PlayerId(8) + ItemId(4) +
        // Quantity(4) + MonsterId(4) + QualityTier(1) + DropKind(1).
        // Deliberately nowhere near any other packet size on this wire, so
        // the size-based demultiplexing in both receive loops stays
        // unambiguous.
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

            if (ExpectedResponseLootDropSize == ExpectedClientCommandSize || ExpectedResponseLootDropSize == ExpectedStateUpdateSize ||
                ExpectedResponseLootDropSize == ExpectedAuthHandshakeSize || ExpectedResponseLootDropSize == ExpectedRequestChatMessageSize ||
                ExpectedResponseLootDropSize == ExpectedResponseChatMessageSize)
            {
                throw new InvalidOperationException("Packet size collision detected - the WS receive loops on both sides distinguish inbound message types by exact byte size, so every packet type must have a unique size.");
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

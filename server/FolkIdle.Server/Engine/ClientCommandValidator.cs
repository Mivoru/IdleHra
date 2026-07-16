using System;
using System.Collections.Generic;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public static class ClientCommandValidator
    {
        public static bool ValidateAssetIntegrity(long clientHash, long clientSignature, long playerId)
        {
            if (!long.TryParse(Environment.GetEnvironmentVariable("ExpectedCatalogHashLong"), out long expectedHash))
                expectedHash = 0;

            if (!long.TryParse(Environment.GetEnvironmentVariable("ExpectedPlatformSignatureLong"), out long expectedSignature))
                expectedSignature = 0;

            if (expectedHash != 0 && clientHash != expectedHash)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 4, Value1 = 9, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (expectedSignature != 0 && clientSignature != expectedSignature)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 4, Value1 = 9, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        // Hot-path epoch interception gate (allocation-free: long comparison on unmanaged registers).
        // Returns false and emits EventType=5 if the client epoch diverges from the session register.
        public static bool ValidateEpochSynchronization(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.LogicEpochCounter != payload.LogicEpochCounter)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent
                {
                    PlayerId = payload.PlayerId,
                    EventType = 5,
                    Value1 = (int)(payload.LogicEpochCounter & 0x7FFFFFFF),
                    Value2 = (int)(packet.LogicEpochCounter & 0x7FFFFFFF),
                    Timestamp = Environment.TickCount64
                });
                return false;
            }
            return true;
        }
        public static bool ValidateConsumableRequest(ref FolkIdle.Server.Network.ClientCommandPacket packet, LiveSessionContext sessionContext)
        {
            if (!AlchemyCompendium.IsValidConsumable(packet.ConsumableItemId))
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent
                {
                    PlayerId = sessionContext.PlayerId,
                    EventType = 3, // Unauthorized item id
                    Value1 = (int)packet.ConsumableItemId,
                    Timestamp = Environment.TickCount64
                });
                return false;
            }

            if (sessionContext.ActiveStatusEffects.RemainingBuffDurationTicks > 72000) // maximum saturation cap (e.g. 2 hours at 10 ticks/sec)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent
                {
                    PlayerId = sessionContext.PlayerId,
                    EventType = 3, // Duration overflow
                    Value1 = (int)sessionContext.ActiveStatusEffects.RemainingBuffDurationTicks,
                    Timestamp = Environment.TickCount64
                });
                return false;
            }

            return true;
        }

        public static bool ValidateFusionCommand(ref TickStatePayload payload, long targetId, long sacId1, long sacId2)
        {
            if (targetId <= 0 || sacId1 <= 0 || sacId2 <= 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 4, Value1 = 2, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (targetId == sacId1 || targetId == sacId2 || sacId1 == sacId2)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 4, Value1 = 2, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (payload.ForgeLevel == 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = 7, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateForgeSplicingRequest(ref TickStatePayload payload, long targetId, long sacId1, long sacId2, IReadOnlyList<EquipmentInstance> lockedItems)
        {
            if (targetId <= 0 || sacId1 <= 0 || sacId2 <= 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (targetId == sacId1 || targetId == sacId2 || sacId1 == sacId2)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (lockedItems.Count != 3)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            bool foundTarget = false;
            bool foundSac1 = false;
            bool foundSac2 = false;

            for (int i = 0; i < lockedItems.Count; i++)
            {
                var item = lockedItems[i];
                if (item.Id == targetId) foundTarget = true;
                if (item.Id == sacId1) foundSac1 = true;
                if (item.Id == sacId2) foundSac2 = true;

                if (item.PlayerId != payload.PlayerId)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = 4, Timestamp = Environment.TickCount64 });
                    return false;
                }

                if (item.IsAffixLocked || HasLockedAffixPayload(item.AffixPayload))
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = 5, Timestamp = Environment.TickCount64 });
                    return false;
                }
            }

            if (!foundTarget || !foundSac1 || !foundSac2)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = 6, Timestamp = Environment.TickCount64 });
                return false;
            }

            for (int i = 0; i < lockedItems.Count; i++)
            {
                if (lockedItems[i].Id == targetId)
                {
                    int targetTier = lockedItems[i].QualityTier + 1;
                    if (payload.ForgeLevel < targetTier)
                    {
                        TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 2, Value2 = targetTier, Timestamp = Environment.TickCount64 });
                        return false;
                    }
                    break;
                }
            }

            return true;
        }

        private static bool HasLockedAffixPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            return payload.Contains("\"is_affix_locked\":true", StringComparison.OrdinalIgnoreCase)
                || payload.Contains("\"is_affix_locked\":1", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ValidateLoginTime(ref TickStatePayload payload, long currentUnixTimestamp)
        {
            if (payload.LastLogoutTimestamp > currentUnixTimestamp)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent 
                { 
                    PlayerId = payload.PlayerId, 
                    EventType = 3, 
                    Value1 = 0, 
                    Value2 = 0, 
                    Timestamp = Environment.TickCount64 
                });
                return false;
            }
            return true;
        }

        public static bool ValidateCommand(ref TickStatePayload payload, byte commandType)
        {
            // Only validate state commands that affect gameplay.
            if (commandType == 1 || commandType == 2 || commandType == 8 || commandType == 24 || commandType == 25 || commandType == 26 || commandType == 27 || commandType == 28 || commandType == 29 || commandType == 30 || commandType == 32 || commandType == 33 || commandType == 47 || commandType == 48 || commandType == 49 || commandType == 50 || commandType == 51 || commandType == 52)
            {
                long currentTick = Environment.TickCount64;

                // 1. Static 100ms ceiling regardless of acceleration.
                if (payload.LastCommandTimestamp != 0 && (currentTick - payload.LastCommandTimestamp) < 100)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent 
                    { 
                        PlayerId = payload.PlayerId, 
                        EventType = 3, 
                        Value1 = commandType, 
                        Value2 = 1, 
                        Timestamp = currentTick 
                    });
                    return false;
                }

                // 2. Multiplier verification
                if (payload.SpeedMultiplier > 1 && payload.AccumulatedTimeBankMs < 100 && payload.BankedChronoSeconds <= 0.0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent 
                    { 
                        PlayerId = payload.PlayerId, 
                        EventType = 3, 
                        Value1 = commandType, 
                        Value2 = 2, 
                        Timestamp = currentTick 
                    });
                    return false;
                }

                payload.LastCommandTimestamp = currentTick;
            }

            return true;
        }

        public static bool ValidateChronoCommands(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.ConsumeChronoCore)
            {
                return true;
            }

            if (packet.TargetId <= 0 || packet.SecondaryId < 0 || packet.TertiaryId < 0 || packet.LimitPrice < 0 || packet.QualityTier < 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 24, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 24, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (payload.Quarantine_Active)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 24, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateNoAntiCheatPayload(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command == FolkIdle.Server.Network.CommandType.AntiCheatChallengeResponse)
            {
                return true;
            }

            if (packet.ChallengeId != 0 || packet.ChallengeVerificationHash != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 31, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateAntiCheatChallengeResponse(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.AntiCheatChallengeResponse)
            {
                return true;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetGuid != Guid.Empty || packet.SecondaryGuid != Guid.Empty || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 31, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            long now = Environment.TickCount64;
            if (payload.ActiveChallengeSeed == 0 || payload.ActiveChallengeAnswered != 0 || now - payload.ActiveChallengeIssuedAtMs > 500L)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 31, Value2 = 2, Timestamp = now });
                return false;
            }

            uint expected = AntiCheatTelemetryEngine.ComputeChallengeHash(payload.ActiveChallengeSeed, payload.PlayerId, payload.LogicEpochCounter);
            if (packet.ChallengeId != payload.ActiveChallengeSeed || packet.ChallengeVerificationHash != expected)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 31, Value2 = 3, Timestamp = now });
                return false;
            }

            return true;
        }

        public static bool ValidateNoPushCompliancePayload(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command == FolkIdle.Server.Network.CommandType.RegisterPushToken ||
                packet.Command == FolkIdle.Server.Network.CommandType.TriggerGdprPurge ||
                packet.Command == FolkIdle.Server.Network.CommandType.SwitchLanguage)
            {
                return true;
            }

            if (packet.TargetPlatformFamily != 0 ||
                packet.ConfirmationHash != 0 ||
                packet.TargetLanguageId != 0 ||
                packet.PushReserved0 != 0 ||
                packet.ComplianceReserved0 != 0 ||
                packet.ComplianceReserved1 != 0 ||
                packet.ComplianceReserved2 != 0 ||
                HasAnyDeviceTokenBytes(ref packet))
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 57, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateLegacyStoreRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.PurchaseLegacyUnlocks)
            {
                return true;
            }

            if (payload.PlayerId <= 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 25, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId < 0 || packet.SecondaryId < 0 || packet.TertiaryId < 0 || packet.LimitPrice < 0 || packet.QualityTier < 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 25, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 25, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetUnlockId != LegacyStoreEngine.CitizenMultiSlotUnlockId || packet.RequestedSlotIndex > LegacyStoreEngine.MaxCitizenSlotIndex)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 25, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            int requestedMask = 1 << (int)packet.RequestedSlotIndex;
            if ((payload.CitizenMultiSlotsUnlocked & requestedMask) != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 25, Value2 = 5, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateGuildDepositRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.DepositGuildMaterial)
            {
                return true;
            }

            if (payload.PlayerId <= 0 || payload.GuildId <= 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 26, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.DepositQuantity == 0 || packet.DepositQuantity > int.MaxValue)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 26, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.MaterialId == 0 || packet.MaterialId > ContentRegistry.ItemDefinitions.Length)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 26, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 26, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateCombatTurnRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.ExecuteCombatTurn)
            {
                return true;
            }

            if (payload.PlayerId <= 0 || payload.GuildId <= 0 || packet.MatchId == 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 27, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.ClientPredictedTurnCounter > int.MaxValue)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 27, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 27, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateMentorshipRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.EstablishMentorship && packet.Command != FolkIdle.Server.Network.CommandType.TerminateMentorship)
            {
                return true;
            }

            if (payload.PlayerId <= 0 || packet.TargetPlayerId == 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 28, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if ((long)packet.TargetPlayerId == payload.PlayerId)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 28, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.MentorshipRole != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 28, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetGuid != Guid.Empty || packet.SecondaryGuid != Guid.Empty)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 28, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 28, Value2 = 5, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (payload.AcademyLevel == 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 28, Value2 = 6, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (payload.ActiveMentorshipContractCount >= payload.AcademyLevel)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 28, Value2 = 7, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateBreedingRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.ExecuteBreeding)
            {
                return true;
            }

            if (payload.BreedingLevel == 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 15, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetGuid == Guid.Empty || packet.SecondaryGuid == Guid.Empty || packet.TargetGuid == packet.SecondaryGuid)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 15, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 15, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateVillageManagementRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.UpgradeBuilding && packet.Command != FolkIdle.Server.Network.CommandType.EvictVillager)
            {
                return true;
            }

            byte commandValue = (byte)packet.Command;
            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetGuid != Guid.Empty || packet.SecondaryGuid != Guid.Empty || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = commandValue, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.Command == FolkIdle.Server.Network.CommandType.UpgradeBuilding)
            {
                if (!VillageManagementEngine.IsValidBuildingId(packet.TargetBuildingId) || packet.TargetVillagerSlot != 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 29, Value2 = 2, Timestamp = Environment.TickCount64 });
                    return false;
                }
            }
            else if (packet.TargetBuildingId != 0 || packet.TargetVillagerSlot > int.MaxValue)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 30, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateCombatTurnRequest(long playerId, long guildId, ref FolkIdle.Server.Network.ClientCommandPacket packet, GuildWarActiveMatch match)
        {
            if (guildId != match.AttackingGuildId && guildId != match.DefendingGuildId)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 27, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            uint serverTurn = GuildCombatSimulationEngine.ExtractTurnCounter(match.CurrentStateBitmask);
            long drift = (long)packet.ClientPredictedTurnCounter - serverTurn;
            if (drift < 0L) drift = -drift;
            if (drift > 2L)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 27, Value2 = 5, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateMarketCommands(ref TickStatePayload payload, byte commandType, long targetId, int price)
        {
            if (commandType == 9 || commandType == 10)
            {
                if (price <= 0 && commandType == 9)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 4, Value1 = commandType, Value2 = 1, Timestamp = Environment.TickCount64 });
                    return false;
                }

                if (targetId <= 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 4, Value1 = commandType, Value2 = 2, Timestamp = Environment.TickCount64 });
                    return false;
                }
            }
            return true;
        }

        // Modul: CommandType.ChangeActivity previously wrote cmd.TargetId
        // (an unbounded client-supplied long) straight into
        // currentPayload.ActiveActivityId with no validator call at all -
        // every sibling command branch gates through one. A negative or
        // arbitrarily large TargetId was not immediately exploitable only
        // because downstream bound checks elsewhere in the tick happened
        // to catch it; this closes the gap at the source instead of
        // depending on that being true forever. Accepts exactly the
        // domain the tick loop already treats as a valid activity: 0
        // (idle), the World Boss sentinel, any authored gathering node
        // ActivityId, or any id within the authored monster range.
        private const long WorldBossActivityId = 9999L;

        public static bool ValidateChangeActivityRequest(ref TickStatePayload payload, long targetActivityId)
        {
            if (targetActivityId < 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 6, Value1 = 1, Value2 = 0, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (targetActivityId == 0 || targetActivityId == WorldBossActivityId)
            {
                return true;
            }

            if (ContentRegistry.TryGetGatheringNode(targetActivityId, out _))
            {
                return true;
            }

            if (targetActivityId <= ContentRegistry.Monsters.Length)
            {
                return true;
            }

            TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 6, Value1 = 2, Value2 = 0, Timestamp = Environment.TickCount64 });
            return false;
        }

        public static bool ValidateMailCommands(ref TickStatePayload payload, byte commandType, long targetId)
        {
            if (commandType == 11 || commandType == 12)
            {
                if (targetId <= 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 4, Value1 = commandType, Value2 = 1, Timestamp = Environment.TickCount64 });
                    return false;
                }
            }
            return true;
        }

        public static bool ValidateAffixReroll(ref TickStatePayload payload, long targetId, int affixIndex)
        {
            if (targetId <= 0 || affixIndex < 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 4, Value1 = 14, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }
            return true;
        }

        public static bool ValidateCombatConfiguration(ref TickStatePayload payload, int thresholdValue)
        {
            if (thresholdValue < 0 || thresholdValue > 100)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 16, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }
            return true;
        }

        // Modul: structural validity only - is skillId even a real skill ID.
        // Soft-fail conditions (not yet unlocked, insufficient mana, still on
        // cooldown, insufficient skill points) are normal gameplay outcomes,
        // not a cheat signal, so they are handled inline in SimulationEngine's
        // RequestCastSkill/RequestUnlockSkill dispatch without disconnecting
        // the session - only a structurally impossible skillId (one the real
        // client UI could never produce) reaches this check and disconnects.
        public static bool ValidateSkillCommand(ref TickStatePayload payload, long skillId, byte commandType)
        {
            if (skillId < 1 || skillId > ActiveSkillEngine.MaxSkillId)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = commandType, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateGuildContributions(ref TickStatePayload payload, long quantity)
        {
            if (quantity <= 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 5, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }
            return true;
        }

        public static bool ValidateWorldBossRegistration(ref TickStatePayload payload, long damage)
        {
            // Simple sanity check for one-shot damage injections
            if (damage > 10000000 || damage <= 0) // arbitrarily large limit
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 19, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }
            return true;
        }

        public static bool ValidateWorldBossAttackRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet, uint activeBossId, bool bossIsDead, bool eventActive)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.AttackWorldBoss)
            {
                return true;
            }

            if (!eventActive)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 32, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.ClientPredictedDamage == 0 ||
                (packet.ClientPredictedDamage & 0x80000000u) != 0 ||
                packet.ClientPredictedDamage > WorldBossEngine.MaxClientPredictedDamage)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 32, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetedBossId != activeBossId)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 32, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (bossIsDead)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 32, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetGuid != Guid.Empty || packet.SecondaryGuid != Guid.Empty || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0 || packet.ChallengeId != 0 || packet.ChallengeVerificationHash != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 32, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateCraftingRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.CraftItem)
            {
                return true;
            }

            if (packet.TargetRecipeId == 0 || !CraftingReceptuary.TryGetRecipe((int)packet.TargetRecipeId, out _))
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 42, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.CraftingSlotIndex >= 5)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 42, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateGdprPurgeRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.TriggerGdprPurge)
            {
                return true;
            }

            uint expected = ComputeGdprConfirmationHash(payload.PlayerId, payload.LogicEpochCounter);
            if (packet.ConfirmationHash != expected)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 34, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetGuid != Guid.Empty || packet.SecondaryGuid != Guid.Empty || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0 || packet.ChallengeId != 0 || packet.ChallengeVerificationHash != 0 || packet.TargetedBossId != 0 || packet.ClientPredictedDamage != 0 || packet.TargetPlatformFamily != 0 || packet.TargetLanguageId != 0 || HasAnyDeviceTokenBytes(ref packet))
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 34, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateLanguageSwitchRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.SwitchLanguage)
            {
                return true;
            }

            if (packet.TargetLanguageId == 0 || packet.TargetLanguageId > 4)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 35, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetGuid != Guid.Empty || packet.SecondaryGuid != Guid.Empty || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0 || packet.ChallengeId != 0 || packet.ChallengeVerificationHash != 0 || packet.TargetedBossId != 0 || packet.ClientPredictedDamage != 0 || packet.TargetPlatformFamily != 0 || packet.ConfirmationHash != 0 || HasAnyDeviceTokenBytes(ref packet))
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 35, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static uint ComputeGdprConfirmationHash(long playerId, long logicEpochCounter)
        {
            uint value = 0x47D99513u;
            value ^= unchecked((uint)playerId);
            value = XorShift32(value);
            value ^= unchecked((uint)(playerId >> 32));
            value = XorShift32(value + unchecked((uint)logicEpochCounter * 0x9E3779B9u));
            value ^= 0xA5C3F19Bu;
            return XorShift32(value);
        }

        private static uint XorShift32(uint value)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return value == 0u ? 0x6D2B79F5u : value;
        }

        private static unsafe bool HasAnyDeviceTokenBytes(ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            fixed (byte* token = packet.DeviceTokenBytes)
            {
                for (int i = 0; i < 64; i++)
                {
                    if (token[i] != 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static unsafe bool ValidateDeviceRegistrationRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.RegisterPushToken)
            {
                return true;
            }

            if (packet.TargetPlatformFamily == 0 || packet.TargetPlatformFamily > 2)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 33, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            bool hasNonZero = false;
            bool hasTerminator = false;
            fixed (byte* token = packet.DeviceTokenBytes)
            {
                for (int i = 0; i < 64; i++)
                {
                    byte value = token[i];
                    if (value == 0)
                    {
                        hasTerminator = true;
                        continue;
                    }

                    if (hasTerminator || value < 33 || value > 126)
                    {
                        TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 33, Value2 = 2, Timestamp = Environment.TickCount64 });
                        return false;
                    }

                    hasNonZero = true;
                }
            }

            if (!hasNonZero)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 33, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetId != 0 || packet.SecondaryId != 0 || packet.TertiaryId != 0 || packet.LimitPrice != 0 || packet.IsBuy != 0 || packet.QualityTier != 0 || packet.TargetGuid != Guid.Empty || packet.SecondaryGuid != Guid.Empty || packet.TargetUnlockId != 0 || packet.RequestedSlotIndex != 0 || packet.MaterialId != 0 || packet.DepositQuantity != 0 || packet.MatchId != 0 || packet.ClientPredictedTurnCounter != 0 || packet.TargetPlayerId != 0 || packet.MentorshipRole != 0 || packet.TargetBuildingId != 0 || packet.TargetVillagerSlot != 0 || packet.ChallengeId != 0 || packet.ChallengeVerificationHash != 0 || packet.TargetedBossId != 0 || packet.ClientPredictedDamage != 0 || packet.ConfirmationHash != 0 || packet.TargetLanguageId != 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 33, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateUpgradeRequest(ref TickStatePayload payload, byte commandType, int targetId)
        {
            if (commandType == 20 || commandType == 21)
            {
                if (targetId < 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = commandType, Value2 = 1, Timestamp = Environment.TickCount64 });
                    return false;
                }
                
                if (commandType == 20 && (targetId < 1 || targetId > 4))
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = commandType, Value2 = 2, Timestamp = Environment.TickCount64 });
                    return false;
                }
            }
            return true;
        }

        public static bool ValidateMentorshipAssignment(ref TickStatePayload payload, System.Guid characterId, int slotIndex)
        {
            if (characterId == System.Guid.Empty)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 22, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (slotIndex < 0 || slotIndex >= 5)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 22, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (payload.AcademyLevel == 0 || slotIndex >= payload.AcademyLevel)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 22, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateCodexQueries(long sessionPlayerId, long requestPlayerId)
        {
            if (sessionPlayerId != requestPlayerId)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = sessionPlayerId, EventType = 3, Value1 = 25, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }
            return true;
        }

        public static bool ValidateStorefrontQuery(long playerId, string query)
        {
            if (!string.IsNullOrEmpty(query))
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 47, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateChronoRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet, uint authoritativeBankBalance)
        {
            return ValidateChronoManipulation(ref payload, ref packet, authoritativeBankBalance);
        }

        public static bool ValidateChronoManipulation(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet, uint authoritativeBankBalance)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.ActivateChronoBoost &&
                packet.Command != FolkIdle.Server.Network.CommandType.ConsumeTimeWarpCore)
            {
                return true;
            }

            if (payload.IsQuarantined || payload.Quarantine_Active)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            long serverEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long drift = Math.Abs(serverEpoch - packet.LogicEpochCounter);
            if (packet.LogicEpochCounter <= 0 || drift > 5L)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.Command == FolkIdle.Server.Network.CommandType.ActivateChronoBoost)
            {
                if (packet.RequestedSpeedMultiplier != 2.0 && packet.RequestedSpeedMultiplier != 4.0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 3, Timestamp = Environment.TickCount64 });
                    return false;
                }

                if (authoritativeBankBalance == 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 4, Timestamp = Environment.TickCount64 });
                    return false;
                }

                return true;
            }

            uint requestedWarpSeconds = packet.ChronoWarpDurationSeconds != 0 ? packet.ChronoWarpDurationSeconds : packet.ChronoSecondsRequested;
            if (requestedWarpSeconds == 0 || requestedWarpSeconds > authoritativeBankBalance || requestedWarpSeconds > ChronoBufferEngine.MaxBankedChronoSeconds)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 5, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }
        public static bool ValidateLeaderboardQuery(long playerId, int skip, int take)
        {
            if (skip < 0 || skip > 10000)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 38, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (take < 0 || take > 50)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 38, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        // Modul 40: gateway-level bounds check for the marketplace browser's
        // paginated listing query, mirroring ValidateLeaderboardQuery. Rejects
        // before FetchActiveListingsAsync ever runs a Skip/Take against the
        // caller-supplied values - a negative pageIndex would Skip a negative
        // count (an ArgumentException from EF Core), and an unbounded pageSize
        // would let a single request pull the entire active order book.
        public static bool ValidateMarketBrowserQuery(long playerId, int pageIndex, int pageSize)
        {
            if (pageIndex < 0 || pageIndex > 10000)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 39, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (pageSize <= 0 || pageSize > 50)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 39, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        // Modul: gateway-level bounds check for the Breeding Lab preview
        // endpoint, mirroring ValidateBreedingRequest's own non-empty/
        // distinct parent GUID check - ownership of both parents is still
        // verified against the DB inside HandleBreedingPreview itself, this
        // only rejects the obviously-malformed shapes before any query runs.
        public static bool ValidateBreedingPreviewQuery(long playerId, Guid paternalId, Guid maternalId)
        {
            if (paternalId == Guid.Empty || maternalId == Guid.Empty || paternalId == maternalId)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 40, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateAchievementClaimRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (payload.IsQuarantined || payload.Quarantine_Active) return false;
            if (packet.TargetAchievementId <= 0) return false;
            
            return true;
        }

        public static bool ValidateBattlePassClaimRequest(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.ClaimBattlePassReward)
            {
                return true;
            }

            if (payload.IsQuarantined || payload.Quarantine_Active)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 46, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TargetMilestoneIndex >= 50)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 46, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            uint requiredXp = (packet.TargetMilestoneIndex + 1U) * 1000U;
            if (payload.AccumulatedSeasonalXp < requiredXp)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 46, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateGuildWarAction(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.RegisterGuildDefense &&
                packet.Command != FolkIdle.Server.Network.CommandType.SubmitShardAttack)
            {
                return true;
            }

            if (payload.GuildId <= 0 || payload.IsQuarantined || payload.Quarantine_Active)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = (byte)packet.Command, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.Command == FolkIdle.Server.Network.CommandType.RegisterGuildDefense)
            {
                if (packet.TargetMatchUuid != Guid.Empty || packet.ClientPredictedDamage != 0 || packet.IsBuy != 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 49, Value2 = 2, Timestamp = Environment.TickCount64 });
                    return false;
                }

                return true;
            }

            if (packet.TargetMatchUuid == Guid.Empty || packet.ClientPredictedDamage == 0 || packet.ClientPredictedDamage > 100000000U)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 50, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (payload.ActiveCrossShardMatchId != Guid.Empty && payload.ActiveCrossShardMatchId != packet.TargetMatchUuid)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 50, Value2 = 4, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidatePingNetworkDiagnostics(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (payload.IsSuspended) return false;
            return true;
        }

        public static bool ValidateTelemetryBurst(ref TickStatePayload payload, ref FolkIdle.Server.Network.ClientCommandPacket packet)
        {
            if (packet.Command != FolkIdle.Server.Network.CommandType.ReportTelemetryBurst)
            {
                return true;
            }

            if (payload.IsQuarantined || payload.Quarantine_Active)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 51, Value2 = 1, Timestamp = Environment.TickCount64 });
                return false;
            }

            if (packet.TelemetryEventCount > 32U)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 51, Value2 = 2, Timestamp = Environment.TickCount64 });
                return false;
            }

            long logicalDrift = Math.Abs(packet.LogicEpochCounter - payload.LogicEpochCounter);
            if (packet.LogicEpochCounter <= 0 || logicalDrift > 5L)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = payload.PlayerId, EventType = 3, Value1 = 51, Value2 = 3, Timestamp = Environment.TickCount64 });
                return false;
            }

            return true;
        }

        public static bool ValidateNetworkThroughput(ref FolkIdle.Server.Network.TokenBucket bucket, long playerId, ref FolkIdle.Server.Network.ClientCommandPacket packet, out int reasonCode)
        {
            reasonCode = 0;
            if (!FolkIdle.Server.Network.NetworkThrottlingEngine.TryConsume(ref bucket))
            {
                reasonCode = 1;
                return false;
            }

            if (packet.Command == FolkIdle.Server.Network.CommandType.PingNetworkDiagnostics)
            {
                if (packet.NetworkDiagnosticsToken == 0U ||
                    packet.TargetMatchUuid != Guid.Empty ||
                    packet.TelemetryEventCount != 0U)
                {
                    reasonCode = 2;
                    return false;
                }
            }

            return true;
        }
    }
}

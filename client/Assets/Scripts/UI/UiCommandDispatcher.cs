using System;
using UnityEngine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    public class UiCommandDispatcher : MonoBehaviour
    {
        public WebSocketClient NetworkClient;
        
        [Header("Command Context")]
        public byte TargetCommandType;
        public int TargetArgumentValue;

        // Binds natively in Unity Inspector without lambda closures
        public void DispatchConfiguredCommand()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendCommandZeroAlloc(TargetCommandType, TargetArgumentValue);
            }
        }

        // Specific endpoint for Region Selection (CommandType = 1)
        public void DispatchRegionSelection()
        {
            if (NetworkClient != null)
            {
                // 1 corresponds to CommandType.ChangeActivity
                NetworkClient.SendCommandZeroAlloc(1, TargetArgumentValue);
            }
        }

        // Specific endpoint for Chrono-Acceleration (CommandType = 8)
        public void DispatchChronoToggle()
        {
            if (NetworkClient != null)
            {
                // 8 corresponds to Chrono-Acceleration 
                NetworkClient.SendCommandZeroAlloc(8, TargetArgumentValue);
            }
        }

        [Header("Forge Fusion Context")]
        public long FusionTargetId;
        public long FusionSacrificeId1;
        public long FusionSacrificeId2;

        public void DispatchForgeFusion()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendFusionCommandZeroAlloc(FusionTargetId, FusionSacrificeId1, FusionSacrificeId2);
            }
        }

        [Header("Breeding Context")]
        [System.NonSerialized] public System.Guid BreedingPaternalId;
        [System.NonSerialized] public System.Guid BreedingMaternalId;

        public void DispatchExecuteBreeding()
        {
            if (NetworkClient != null)
            {
                // 15 = ExecuteBreeding
                NetworkClient.SendBreedingCommandZeroAlloc(15, BreedingPaternalId, BreedingMaternalId);
            }
        }

        [Header("Market Context")]
        public long MarketTargetInstanceId;
        public int MarketListingPrice;

        public void DispatchExecuteForgeFusion()
        {
            if (NetworkClient != null)
            {
                // 2 = ExecuteForgeFusion
                NetworkClient.SendFusionCommandZeroAlloc(FusionTargetId, FusionSacrificeId1, FusionSacrificeId2);
            }
        }

        public void DispatchToggleChronoAcceleration()
        {
            if (NetworkClient != null)
            {
                // 8 = ToggleChronoAcceleration
                NetworkClient.SendCommandZeroAlloc(8, 0);
            }
        }

        public void DispatchMarketListItem()
        {
            if (NetworkClient != null)
            {
                // 9 = MarketListItem
                NetworkClient.SendMarketCommandZeroAlloc(9, MarketTargetInstanceId, MarketListingPrice);
            }
        }

        [Header("Consumable Context")]
        public uint TargetConsumableItemId;
        public uint TargetConsumableSlotTarget;

        public void DispatchConsumeConsumableAsset()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendConsumableCommandZeroAlloc(45, TargetConsumableItemId, TargetConsumableSlotTarget);
            }
        }

        [Header("Chronicle Pass Context")]
        public uint TargetMilestoneIndex;

        public void DispatchClaimBattlePassReward()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendBattlePassClaimCommandZeroAlloc(TargetMilestoneIndex);
            }
        }

        public void DispatchMarketBuyItem()
        {
            if (NetworkClient != null)
            {
                // 10 = MarketBuyItem
                NetworkClient.SendMarketCommandZeroAlloc(10, MarketTargetInstanceId, 0);
            }
        }

        [Header("Mail & Bank Context")]
        public long MailTargetId;
        public long BankInstanceTargetId;

        public void DispatchClaimMail()
        {
            if (NetworkClient != null)
            {
                // 11 = ClaimMailItem
                NetworkClient.SendMailCommandZeroAlloc(11, MailTargetId);
            }
        }

        public void DispatchDepositToBank()
        {
            if (NetworkClient != null)
            {
                // 12 = DepositToBank
                NetworkClient.SendMailCommandZeroAlloc(12, BankInstanceTargetId);
            }
        }

        public void DispatchWithdrawFromBank()
        {
            if (NetworkClient != null)
            {
                // 13 = WithdrawFromBank
                NetworkClient.SendMailCommandZeroAlloc(13, BankInstanceTargetId);
            }
        }

        [Header("Reroll Context")]
        public long RerollTargetId;
        public int RerollAffixIndex;

        public void DispatchRerollAffix()
        {
            if (NetworkClient != null)
            {
                // 14 = RerollItemAffix
                NetworkClient.SendRerollCommandZeroAlloc(RerollTargetId, RerollAffixIndex);
            }
        }

        [Header("Guild Context")]
        public int GuildContributionItemDefinitionId;
        public int GuildContributionQuantity;

        public void DispatchGuildContribution()
        {
            if (NetworkClient != null)
            {
                // 5 = ContributeToGuild
                NetworkClient.SendGuildContributionCommandZeroAlloc(GuildContributionItemDefinitionId, GuildContributionQuantity);
            }
        }

        [Header("Crafting Context")]
        public long CraftingResultItemId;
        public uint TargetRecipeId;
        public uint CraftingSlotIndex;

        public void DispatchCraftItem()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendEquipmentCraftingCommandZeroAlloc(TargetRecipeId, CraftingSlotIndex);
            }
        }

        public void DispatchInitializeCrafting()
        {
            if (NetworkClient != null)
            {
                // 18 = InitializeCrafting
                NetworkClient.SendCraftingCommandZeroAlloc(18, CraftingResultItemId);
            }
        }

        [Header("World Boss Context")]
        public long WorldBossDamageAmount;
        public uint TargetedBossId = 1;
        public uint ClientPredictedBossDamage = 1000;

        public void DispatchRegisterWorldBossDamage()
        {
            if (NetworkClient != null)
            {
                // 19 = RegisterWorldBossDamage
                NetworkClient.SendWorldBossDamageCommandZeroAlloc(WorldBossDamageAmount);
            }
        }

        public void DispatchAttackWorldBoss()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendWorldBossAttackCommandZeroAlloc(TargetedBossId, ClientPredictedBossDamage);
            }
        }

        [Header("Push Notifications")]
        public PushDeviceTokenProvider DeviceTokenProvider;
        private readonly byte[] _deviceTokenScratch = new byte[64];

        public void DispatchRegisterPushToken()
        {
            if (NetworkClient != null && DeviceTokenProvider != null && DeviceTokenProvider.TryCopyToken(_deviceTokenScratch, out byte platformFamily))
            {
                NetworkClient.SendPushTokenCommandZeroAlloc(_deviceTokenScratch, platformFamily);
            }
        }

        [Header("Compliance")]
        public byte TargetLanguageId = 1;

        public void DispatchTriggerGdprPurge()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendGdprPurgeCommandZeroAlloc();
            }
        }

        public void DispatchSwitchLanguage()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendLanguageSwitchCommandZeroAlloc(TargetLanguageId);
            }
        }

        [Header("Village Infrastructure Context")]
        public int BuildingTypeTarget;
        public uint TargetBuildingId = 1;
        public uint TargetVillagerSlot;

        public void DispatchUpgradeBuilding()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendVillageUpgradeCommandZeroAlloc(TargetBuildingId == 0 ? (uint)BuildingTypeTarget : TargetBuildingId);
            }
        }

        public void DispatchEvictVillager()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendVillagerEvictionCommandZeroAlloc(TargetVillagerSlot);
            }
        }

        public void DispatchUpgradeTool()
        {
            if (NetworkClient != null)
            {
                // 21 = UpgradeTool
                NetworkClient.SendUpgradeCommandZeroAlloc(21, 0);
            }
        }

        [Header("Mentorship Context")]
        [System.NonSerialized] public System.Guid AssignMentorCharacterId;
        public int AssignMentorSlotIndex;

        public void DispatchAssignMentor()
        {
            if (NetworkClient != null)
            {
                // 22 = AssignMentor
                NetworkClient.SendMentorshipCommandZeroAlloc(AssignMentorCharacterId, AssignMentorSlotIndex);
            }
        }

        [Header("Chrono Core Context")]
        public long ChronoCoreItemId;

        public void DispatchConsumeChronoCore()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendChronoCoreCommandZeroAlloc(ChronoCoreItemId);
            }
        }

        [Header("Legacy Store Context")]
        public uint LegacyTargetUnlockId = 1;
        public uint LegacyRequestedSlotIndex;

        public void DispatchPurchaseLegacyUnlock()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendLegacyUnlockCommandZeroAlloc(LegacyTargetUnlockId, LegacyRequestedSlotIndex);
            }
        }

        [Header("Guild Logistics Depot")]
        public uint GuildDepositMaterialId;
        public uint GuildDepositQuantity;

        public void DispatchDepositGuildMaterial()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendGuildMaterialDepositCommandZeroAlloc(GuildDepositMaterialId, GuildDepositQuantity);
            }
        }

        [Header("Guild Combat Simulation")]
        public uint CombatMatchId;
        public uint CombatPredictedTurnCounter;

        public void DispatchExecuteCombatTurn()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendCombatTurnCommandZeroAlloc(CombatMatchId, CombatPredictedTurnCounter);
            }
        }

        [Header("Mentorship Contract")]
        public uint MentorshipTargetPlayerId;

        public void DispatchEstablishMentorship()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendEstablishMentorshipCommandZeroAlloc(MentorshipTargetPlayerId);
            }
        }

        [Header("Sprint 58 - Chrono Boost and Time Warp")]
        public uint ChronoBoostMultiplier = 2;
        public uint TimeWarpSecondsToConsume = 86400;
        public uint ChronoWarpTargetSlot;

        public void DispatchActivateChronoBoost()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendActivateChronoBoostCommandZeroAlloc(ChronoBoostMultiplier);
            }
        }

        public void DispatchExecuteTimeWarp()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendConsumeTimeWarpCoreCommandZeroAlloc(TimeWarpSecondsToConsume, ChronoWarpTargetSlot);
            }
        }

        [Header("Sprint 68 - Cross-Shard Guild War")]
        public string TargetMatchUuidText;
        public uint ShardAttackDamage;
        public bool IsFinalShardBlow;

        public void DispatchRegisterGuildDefense()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendRegisterGuildDefenseCommandZeroAlloc();
            }
        }

        public void DispatchSubmitShardAttack()
        {
            if (NetworkClient != null && System.Guid.TryParse(TargetMatchUuidText, out System.Guid matchUuid))
            {
                NetworkClient.SendSubmitShardAttackCommandZeroAlloc(matchUuid, ShardAttackDamage, IsFinalShardBlow);
            }
        }

        [Header("Sprint 69 - Analytics Telemetry")]
        public uint TelemetryBurstEventCount = 1;
        public uint TelemetryBurstEventTypeHash = 0xC1E17001u;
        public uint TelemetryBurstPayloadMetric;

        public void DispatchTelemetryBurst()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendTelemetryBurstCommandZeroAlloc(TelemetryBurstEventCount, TelemetryBurstEventTypeHash, TelemetryBurstPayloadMetric);
            }
        }

        [Header("Sprint 59 - Leaderboard Slice")]
        public int LeaderboardSkipCount = 0;
        public int LeaderboardTakeCount = 50;

        public void DispatchRequestLeaderboardSlice()
        {
            if (NetworkClient != null)
            {
                // Dispatch Command 38 for telemetry or triggering HTTP fetch workflow on the client side
                NetworkClient.SendCommandZeroAlloc(38, LeaderboardSkipCount);
            }
        }

        [Header("Sprint 60 - Premium Store")]
        public string NativeReceiptPayload;
        public uint NativeProductIdHash;
        public int NativePremiumValue;
        private readonly byte[] _receiptScratchBuffer = new byte[64];

        public unsafe void DispatchSubmitPurchaseReceipt()
        {
            if (NetworkClient != null && !string.IsNullOrEmpty(NativeReceiptPayload))
            {
                int len = Math.Min(64, NativeReceiptPayload.Length);
                for (int i = 0; i < len; i++) _receiptScratchBuffer[i] = (byte)NativeReceiptPayload[i];
                for (int i = len; i < 64; i++) _receiptScratchBuffer[i] = 0;
                
                NetworkClient.SendPurchaseReceiptCommandZeroAlloc(_receiptScratchBuffer, NativeProductIdHash, NativePremiumValue);
            }
        }

        [Header("Sprint 63 - Achievements")]
        public uint TargetAchievementId = 1;

        public void DispatchClaimAchievementReward()
        {
            if (NetworkClient != null)
            {
                // 43 = ClaimAchievementReward
                NetworkClient.SendAchievementClaimCommandZeroAlloc(TargetAchievementId);
            }
        }

        [Header("Node Migration")]
        public uint MigrationToken;

        public void DispatchInitiateNodeMigration()
        {
            if (NetworkClient != null)
            {
                // Bypass new struct allocation overhead by directly writing to a scratch buffer if needed, 
                // but NetworkClient already abstracts this. We will just pass the token.
                NetworkClient.SendMigrationCommandZeroAlloc(MigrationToken);
            }
        }

        [Header("Sprint 70 - Network Diagnostics")]
        public uint NetworkDiagnosticsToken;

        public void DispatchPingNetworkDiagnostics()
        {
            if (NetworkClient != null)
            {
                // 52 = PingNetworkDiagnostics
                NetworkClient.SendPingCommandZeroAlloc(NetworkDiagnosticsToken);
            }
        }
    }
}

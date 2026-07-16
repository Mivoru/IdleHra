using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    // Modul: mirrors server/FolkIdle.Server/Network/StateUpdatePacket.cs
    // exactly - see that file's comment. Generic client error-feedback
    // channel: LastCommandResultCode carries the reason the most recently
    // attempted rejectable command (forge fusion, market listing, guild
    // contribution, reroll) failed, replacing the previous silent no-op.
    public enum CommandResultCode : byte
    {
        Success = 0,
        InvalidPrice = 1,
        ItemEquipped = 2,
        InsufficientMaterials = 3,
        InvalidActivity = 4,
        InsufficientGold = 5,
        TargetNotFound = 6,
        GuildNotFound = 7,
        GenericValidationFailure = 8,

        // Modul: mirrors server CommandResultCode exactly - returned when a
        // deposit/withdraw/claim command targets a player who already has
        // an unresolved bank transaction in flight.
        TransactionPending = 9
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StateUpdatePacket
    {
        public long PlayerId;
        public long ActiveActivityId;
        public int CurrentProgressTicks;
        public int RequiredProgressTicks;
        public int InventorySpaceRemaining;

        public int CurrentMonsterId;
        public int CurrentMonsterHp;
        public int PlayerHp;
        public byte Quarantine_Active;
        
        public int CurrentLevel;
        public long CurrentXp;

        public System.Guid Slot1_CharacterId;
        public long Slot1_AgeTicks;
        public int Slot1_AgePhase;

        public System.Guid Slot2_CharacterId;
        public long Slot2_AgeTicks;
        public int Slot2_AgePhase;

        public System.Guid Slot3_CharacterId;
        public long Slot3_AgeTicks;
        public int Slot3_AgePhase;

        public int CachedMentorCount;

        public int WoodcuttingMasteryXp;
        public int WoodcuttingMasteryLevel;
        public int MiningMasteryXp;
        public int MiningMasteryLevel;
        public int GatheringProgressTicks;
        
        public int CompletedAreaFlags;
        public int ClaimedAchievementFlags;
        public int HumanMasteryLevel;
        public int VilaMasteryLevel;
        public int DraugrMasteryLevel;
        
        public int VillagePopulation;
        public long AccumulatedTimeBankMs;
        public double BankedChronoSeconds;
        public byte IsChronoAccelerating;
        public int AutoEatThreshold;
        public int STR;
        public int DEX;
        public int CON;
        public int LCK;

        public long EquippedWeaponId;
        public byte EquippedWeaponAffixLocked;
        
        public long EquippedArmorId;
        public byte EquippedArmorAffixLocked;

        public int CachedMiningMonolithLevel;
        public int CachedWoodcuttingMonolithLevel;
        
        public int ActiveOffensivePotionId;
        public int OffensivePotionDurationMs;
        public int ActiveDefensivePotionId;
        public int DefensivePotionDurationMs;

        public long WorldBossMaxHp;
        public uint WorldBossCurrentHp;
        public byte ActiveEventType;

        // Modul: mirrors server/FolkIdle.Server/Network/StateUpdatePacket.cs
        // exactly - see that file's comment. Repurposes what was
        // LiveOpsReserved0; packet size unchanged.
        public byte IsFreshAccount;

        // Modul: mirrors server/FolkIdle.Server/Network/StateUpdatePacket.cs
        // exactly - Accuracy/Armor/BlockStrength combat axes, server-
        // computed so client UI can never drift from what was actually
        // rolled. Repurposes what were LiveOpsReserved1-12 (12 bytes);
        // packet size unchanged.
        public int PlayerAccuracyRating;
        public int PlayerArmorRating;
        public float PlayerBlockStrengthPct;

        // Modul: mirrors server/FolkIdle.Server/Network/StateUpdatePacket.cs
        // exactly. Repurposes what was LiveOpsReserved13; packet size
        // unchanged.
        public byte LastCommandResultCode;

        // Modul: mirrors server/FolkIdle.Server/Network/StateUpdatePacket.cs
        // exactly - incrementing counter, not a flag, so two rejections
        // with the identical CommandResultCode back-to-back are still
        // distinguishable client-side. Repurposes what was
        // LiveOpsReserved14; packet size unchanged.
        public byte LastCommandResultTick;

        // Village Infrastructure
        public int CachedCurrentToolTier;
        public int CachedMaxPopulationCapacity;
        public int CachedInnMaturationBonus;

        public int ActiveChildMaturationMs;

        public long ActiveGuildWarId;
        public float CachedWarMultiplier;
        public int GuildCombatVanguardPoints;
        public int GuildProductionLogisticsPoints;
        public int GuildGatheringSupplyChainPoints;
        public int EnemyCombatVanguardPoints;
        public int EnemyProductionLogisticsPoints;
        public int EnemyGatheringSupplyChainPoints;
        public long LogicEpochCounter;
        public int LegacyShardBalance;
        public int CitizenMultiSlotsUnlocked;
        public long GuildLogisticsCurrentStock;
        public long GuildLogisticsTargetRequirement;
        public long CombatSimulationMatchId;
        public int CombatSimulationTurnCounter;
        public int CombatSimulationDamageDelta;
        public long ActiveMentorPlayerId;
        public double MentorshipExpBonusMultiplier;
        public byte ForgeLevel;
        public byte InnLevel;
        public byte BreedingLevel;
        public byte AcademyLevel;
        public byte CurrentPopulationCount;
        public byte VillageReserved0;
        public byte VillageReserved1;
        public byte VillageReserved2;
        public byte VillageReserved3;
        public byte VillageReserved4;
        public byte VillageReserved5;
        public byte VillageReserved6;
        public byte VillageReserved7;
        public byte VillageReserved8;
        public byte VillageReserved9;
        public byte VillageReserved10;
        public uint ActiveChallengeSeed;
        public byte IsQuarantineActive;
        public byte AntiCheatReserved0;
        public byte AntiCheatReserved1;
        public byte AntiCheatReserved2;
        public byte NotificationQueueStateLength;
        public byte NotificationReserved0;
        public byte NotificationReserved1;
        public byte NotificationReserved2;
        public byte NotificationReserved3;
        public byte NotificationReserved4;
        public byte NotificationReserved5;
        public byte NotificationReserved6;
        public byte ActiveLanguageState;
        public byte ComplianceStateReserved0;
        public byte ComplianceStateReserved1;
        public byte ComplianceStateReserved2;
        public byte ComplianceStateReserved3;
        public byte ComplianceStateReserved4;
        public byte ComplianceStateReserved5;
        public byte ComplianceStateReserved6;
        public uint ActiveBankedChronoSeconds;
        public byte CurrentSimulationSpeedMultiplier;
        public byte ChronoReserved0;
        public byte ChronoReserved1;
        public byte ChronoReserved2;
        public uint PremiumCurrencyBalance;
        public uint EventHorizonTransactionCount;
        public byte ActiveAudioTrackId;
        public byte UiScreenShakeIntensity;
        public byte AudioReserved5;
        public uint TotalItemsCraftedCount;
        public byte CraftingEngineStatus;
        public uint TotalAchievementsClaimedCount;
        public uint ActiveMasteryBitmask;
        public ulong LogicalEpochFrameIndex;
        public uint ActiveStatusEffectModifierBitmask;
        public uint RemainingBuffDurationTicks;
        public uint ActiveChroniclePassLevel;
        public uint AccumulatedSeasonalXp;
        public uint VisualBankedChronoSeconds;
        public uint ActiveChronoEngineStatus;
        public ulong ActiveChronoLockExpirationTicks;
        public uint VisualActiveMatchMmr;
        public uint GlobalNodeRemainingHp;
        public System.Guid ActiveMatchId;
        public ulong GuildWarExpansionPadding0;
        public ulong GuildWarExpansionPadding1;
        public uint NetworkDiagnosticsToken;
        public uint GuildWarExpansionPadding2;
        public ulong TotalAnalyticsEventsLoggedCount;
        public uint VisualActiveConnectionThroughput;
        public uint CurrentNodeMemoryLoadMetrics;
        public long Gold;
        public byte WorldBossAttemptCount;
        public byte WorldBossEventState;
        public long WorldBossEventEndEpoch;
        public int GuildLogisticsLevel;
        public int GuildRaidTier;
        public long GuildRaidBossCurrentHp;
        public long GuildRaidBossMaxHp;

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        public byte LumberjackLevel;
        public byte QuarryLevel;
        public byte MineLevel;
        public byte WarehouseLevel;
        public long CachedWoodStock;
        public long CachedStoneStock;
        public long CachedIronOreStock;

        // Modul 16: timed upgrade queue - PendingUpgradeBuildingId == 0 means
        // no upgrade is currently in flight for this player's village.
        public byte PendingUpgradeBuildingId;
        public long PendingUpgradeCompletesAtEpoch;

        // Active Skill Tree (see server ActiveSkillEngine). "ResponseSkillCastPacket"
        // semantics are carried as fields on this recurring broadcast rather
        // than as a separate wire message type - this is the only channel
        // this client's receive loop ever parses. LastSkillCastResultTick
        // increments on every cast the server processes so UiActionBar can
        // edge-detect "a new cast just resolved" versus "the same result
        // repeated," mirroring the existing ActiveChallengeSeed pattern.
        public uint UnlockedSkillsBitmask;
        public int CurrentMana;
        public int MaxMana;
        public int AvailableSkillPoints;
        public uint Skill1CooldownRemainingMs;
        public uint Skill2CooldownRemainingMs;
        public uint Skill3CooldownRemainingMs;
        public uint Skill4CooldownRemainingMs;
        public byte LastSkillCastId;
        public byte LastSkillCastSuccess;
        public byte SkillReserved0;
        public byte SkillReserved1;
        public uint LastSkillCastResultTick;

        // Modul: Offline "Welcome Back" flow - mirrors server
        // StateUpdatePacket exactly. Set once at login, carrying exactly
        // what OfflineSimulationEngine's catch-up granted this session -
        // never a running total. OfflineSummaryTick only increments when a
        // real, non-zero catch-up ran; this client edge-detects a change
        // in that value (see VisualSyncProxy.OnOfflineSummaryAvailable) to
        // show the summary exactly once per login.
        public long OfflineElapsedSeconds;
        public long OfflineGoldEarned;
        public long OfflineXpEarned;
        public int OfflineMaterialDropsGranted;
        public byte OfflineSummaryTick;

        // Modul: save trust indicator - mirrors server StateUpdatePacket
        // exactly. TicksSinceLastFlush / 10 is the whole-second age of the
        // last successful server-side save (see UiSaveTrustIndicator).
        public int TicksSinceLastFlush;

        // Modul: mirrors server StateUpdatePacket exactly - see
        // UiSeasonPassWindow.
        public ulong ClaimedMilestonesBitmask;
    }
}

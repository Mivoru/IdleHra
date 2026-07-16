using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
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

        // Modul: onboarding flag - true (1) when this account's first
        // character has never aged (Slot1_AgeTicks == 0), the signal
        // UiLoginWindow/UiTutorialController use to decide whether to arm
        // the FTUE. Repurposes what was LiveOpsReserved0 - same byte, same
        // offset, packet size unchanged.
        public byte IsFreshAccount;

        // Modul: Combat System Overhaul - the Accuracy/Armor/BlockStrength
        // axes GAME_DESIGN_SPEC.md previously documented as unimplemented
        // placeholders. Transmitted as the server-computed values actually
        // used in that tick's combat resolution (see StatsCalculator's
        // AccuracyRating/BlockStrengthPct and CombatStats.FlatPhysicalArmor)
        // rather than left for the client to reconstruct from raw DEX/CON,
        // so UI combat feedback can never drift from what the server
        // actually rolled against. Repurposes what were
        // LiveOpsReserved1-12 (12 bytes) - packet size unchanged.
        public int PlayerAccuracyRating;
        public int PlayerArmorRating;
        public float PlayerBlockStrengthPct;

        public byte LiveOpsReserved13;
        public byte LiveOpsReserved14;

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

        // Active Skill Tree (see ActiveSkillEngine). "ResponseSkillCastPacket"
        // semantics are carried as fields on this recurring broadcast rather
        // than as a separate wire message type - this is the only channel
        // the client's receive loop ever parses (see UnsafePacketParser/
        // WebSocketClient.ParseAndEnqueuePacket), and every prior feature in
        // this codebase followed the same "add fields to the existing
        // packet" convention rather than inventing a new one (e.g. breeding
        // confirmation has no dedicated packet either). LastSkillCastResultTick
        // increments on every RequestCastSkill the server processes so the
        // client can edge-detect "a new cast just resolved" versus "the same
        // result repeated," mirroring the existing ActiveChallengeSeed
        // edge-detection pattern.
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
    }
}

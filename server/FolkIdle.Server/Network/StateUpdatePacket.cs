using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    // Modul: generic client error-feedback channel. Previously every
    // rejected command (forge fusion, market listing, guild contribution,
    // reroll) was a silent no-op from the player's perspective - the
    // rejection reason existed only as a server-side Console.WriteLine.
    // StateUpdatePacket.LastCommandResultCode carries the outcome of the
    // most recently resolved rejectable command back to the client;
    // Success (0) means either nothing has failed yet this session or the
    // last attempted command succeeded - callers that need to distinguish
    // "no command attempted" from "the last command succeeded" should track
    // that separately client-side, this field only ever tells you the
    // reason for the most recent rejection.
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

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 1.
        // Returned by MailboxAndBankEngine when a deposit/withdraw/claim
        // command targets a player who already has an unresolved bank
        // transaction in flight - see that engine's own doc comment on
        // _pendingBankTransactions.
        TransactionPending = 9,

        // Modul: Final Production Polish, Part 1. Returned by
        // ForgeSplicingEngine when the target item is already at
        // MaxQualityTier and cannot be fused further.
        MaxTierReached = 10,

        // Modul: Final Production Polish, Part 1. Returned by the mail
        // claim / bank withdraw request drains when InventorySpaceRemaining
        // is exhausted and the item could not be delivered.
        InventoryFull = 11
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

        // Modul: generic client error-feedback channel - the result code of
        // the most recently resolved rejectable command (see the
        // CommandResultCode enum above). Repurposes what was
        // LiveOpsReserved13 - same byte, same offset, packet size
        // unchanged.
        public byte LastCommandResultCode;

        // Modul: incrementing counter, not a boolean flag - mirrors
        // LastSkillCastResultTick's own rationale exactly (see below): a
        // flag would either miss two rejections landing back-to-back with
        // the identical CommandResultCode (e.g. two consecutive
        // InsufficientGold rejections), or need its own separate
        // acknowledgement round-trip. Incremented once per applied
        // CommandResultNotification, regardless of whether the new code
        // differs from the previous one. Repurposes what was
        // LiveOpsReserved14 - same byte, same offset, packet size
        // unchanged.
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
        public byte ActiveAudioTrackId;
        public byte UiScreenShakeIntensity;
        public byte AudioReserved5;
        public uint TotalItemsCraftedCount;
        public byte CraftingEngineStatus;
        public uint ActiveMasteryBitmask;
        public ulong LogicalEpochFrameIndex;
        public uint ActiveStatusEffectModifierBitmask;
        public uint RemainingBuffDurationTicks;
        public uint VisualBankedChronoSeconds;
        public uint ActiveChronoEngineStatus;
        public ulong ActiveChronoLockExpirationTicks;
        public uint VisualActiveMatchMmr;
        public uint GlobalNodeRemainingHp;
        public System.Guid ActiveMatchId;
        public uint NetworkDiagnosticsToken;
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

        // Modul: Phase - Full-Stack Production Polish, Part 1.1 (Offline
        // "Welcome Back" flow). Set once by OfflineSimulationEngine.
        // ExtrapolateOfflineProgressAsync at login, carrying exactly what
        // that catch-up granted this session - never a running total.
        // OfflineSummaryTick mirrors LastSkillCastResultTick/
        // LastCommandResultTick's own edge-detection idiom exactly: it only
        // increments when a real, non-zero catch-up ran, and never resets
        // afterward - the client is responsible for comparing it against
        // its own last-seen value to show the summary exactly once per
        // login, not on every subsequent broadcast of the same tick.
        public long OfflineElapsedSeconds;
        public long OfflineGoldEarned;
        public long OfflineXpEarned;
        public int OfflineMaterialDropsGranted;
        public byte OfflineSummaryTick;

        // Modul: Phase - Full-Stack Production Polish, Part 1.3 (save trust
        // indicator). Mirrors TickStatePayload.TicksSinceLastFlush exactly -
        // resets to 0 the tick StateCheckpointManager.FlushState actually
        // commits this player's row, increments by 1 every 10Hz tick
        // otherwise (see SimulationEngine's main tick loop) - so
        // TicksSinceLastFlush / 10 is exactly the whole-second age of the
        // last successful save. Previously tracked only server-side.
        public int TicksSinceLastFlush;

        // Modul: Production Release Hardening, Part 2. ClaimedMilestonesBitmask,
        // ActiveChroniclePassLevel, AccumulatedSeasonalXp,
        // ClaimedAchievementFlags, TotalAchievementsClaimedCount, and
        // EventHorizonTransactionCount were removed from this hot-path
        // packet (all low-frequency/static metadata, none of them "ticks,
        // health, resources, active XP, positioning, live combat status")
        // and now live behind /api/v1/achievements/state and
        // /api/v1/player/metadata instead - see
        // NetworkBroadcastSystem.HandleAchievementsState/
        // HandlePlayerMetadata. GuildWarExpansionPadding0/1/2 were also
        // removed outright: dead reserved filler, never read or written by
        // any code on either side.
    }
}

using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    // Modul: generic client error-feedback channel. Previously every
    // rejected command (forge fusion, market listing, guild contribution,
    // reroll) was a silent no-op from the player's perspective - the
    // rejection reason existed only as a server-side Console.WriteLine.
    // The CommandResult0-3 ring buffer (see its own doc comment below)
    // carries the outcome of up to the 4 most recently resolved
    // rejectable commands back to the client; a ResultTick of 0 in a slot
    // means that slot has never been populated this session - callers
    // that need to distinguish "no command attempted" from "the last
    // command succeeded" should track that separately client-side, these
    // slots only ever tell you the reason for a rejection.
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

        // Modul: Full-Stack Production Hardening Phase 3, Part 5. Replaces
        // the single-slot LastCommandResultCode/LastCommandResultTick pair
        // with a flattened 4-slot ring buffer (4 explicit byte+uint field
        // pairs, matching this struct's own all-flat-fields convention
        // rather than nesting a CommandResultEntry struct into a
        // [Pack = 1] wire struct). A scalar could only ever carry the
        // single most recent rejection - a client that missed exactly one
        // broadcast (e.g. across a reconnect gap) while two or more
        // commands were rejected back to back would only ever see the
        // last one, silently and permanently losing the earlier
        // rejection's feedback. ResultTick is a per-player monotonically
        // increasing counter (never reset, never wraps in practice), so
        // the client can always tell which slots are newer than what it
        // has already displayed and in what order to apply them - see
        // TickStatePayload.CommandResultSlot0-3 (server) and
        // VisualSyncProxy.ApplyCommandResultState (client) for the
        // producing/consuming ends.
        public byte CommandResult0_Code;
        public uint CommandResult0_Tick;
        public byte CommandResult1_Code;
        public uint CommandResult1_Tick;
        public byte CommandResult2_Code;
        public uint CommandResult2_Tick;
        public byte CommandResult3_Code;
        public uint CommandResult3_Tick;

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
        public uint ActiveChallengeSeed;
        public byte IsQuarantineActive;
        public byte NotificationQueueStateLength;
        public byte ActiveLanguageState;
        public uint ActiveBankedChronoSeconds;
        public byte CurrentSimulationSpeedMultiplier;
        public uint PremiumCurrencyBalance;
        public byte ActiveAudioTrackId;
        public byte UiScreenShakeIntensity;
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
        public uint LastSkillCastResultTick;

        // Modul: Phase - Full-Stack Production Polish, Part 1.1 (Offline
        // "Welcome Back" flow). Set once by OfflineSimulationEngine.
        // ExtrapolateOfflineProgressAsync at login, carrying exactly what
        // that catch-up granted this session - never a running total.
        // OfflineSummaryTick mirrors LastSkillCastResultTick's own
        // edge-detection idiom exactly: it only increments when a real,
        // non-zero catch-up ran, and never resets
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

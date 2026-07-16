namespace FolkIdle.Server.Engine
{
    // Modul: Full-Stack Production Hardening Phase 3, Part 5. Compact
    // zero-allocation representation of one buffered command-rejection
    // outcome. ResultTick is a per-player monotonically increasing
    // counter, not a boolean edge-flag, so two rejections that happen to
    // share the same ResultCode are still distinguishable and orderable -
    // see TickStatePayload.CommandResultSlot0-3's own comment for the
    // 4-slot ring buffer this populates.
    public struct CommandResultEntry
    {
        public byte ResultCode;
        public uint ResultTick;
    }

    public struct TickStatePayload
    {
        public TickStatePayload()
        {
        }

        public long PlayerId;
        public System.Guid AccountId;
        public long ActiveActivityId;
        public int CurrentProgressTicks;
        public int RequiredProgressTicks;
        public int InventorySpaceRemaining;
        public bool IsDirty;
        public int TicksSinceLastFlush;
        public int CurrentLevel;
        public long CurrentXp;
        public int SelectedLineageId;
        
        public System.Guid Slot1_CharacterId;
        public long Slot1_AgeTicks;
        public int Slot1_AgePhase;
        public long Slot1_GeneticVector;

        public System.Guid Slot2_CharacterId;
        public long Slot2_AgeTicks;
        public int Slot2_AgePhase;
        public long Slot2_GeneticVector;

        public System.Guid Slot3_CharacterId;
        public long Slot3_AgeTicks;
        public int Slot3_AgePhase;
        public long Slot3_GeneticVector;
        
        public int CachedMentorCount;
        public long LastLogoutTimestamp;
        public long AccumulatedTimeBankMs;
        public int SpeedMultiplier;

        // Combat mechanics
        public int CurrentMonsterId;
        public int CurrentMonsterHp;
        public int PlayerHp;
        public int CombatTargetTickAccumulator;
        
        public bool IsSuspended;
        public bool Quarantine_Active;

        public long CurrentGold;
        public int PremiumCurrency;
        
        public long LastCommandTimestamp;

        // Gathering mastery
        public int WoodcuttingMasteryXp;
        public int WoodcuttingMasteryLevel;
        public int MiningMasteryXp;
        public int MiningMasteryLevel;
        public int GatheringProgressTicks;
        public int VillagePopulation;

        public int STR;
        public int DEX;
        public int CON;
        public int LCK;

        // Modul 13.4.3: cached inherited genetic loci, hydrated at login from
        // the active character's CharacterLineages row (see
        // StateCheckpointManager.LoadPlayerState). Read as plain O(1) field
        // access from StatsCalculator/gathering every tick - never re-derived
        // from GeneticVector on the hot path.
        public bool IsEpicMutation;
        public int LocusSpeed;
        public int LocusCrit;
        public int LocusYield;

        // Modul 13.4.3: set when the active character's lineage was flagged
        // IsInbred at breeding time (see BreedingEngine). Consumed by
        // RaceAttributeGrowth to apply a -25% level-up growth penalty.
        public bool IsInbred;

        // Modul 13.4.3: unix-epoch-seconds until which character XP generation
        // is reduced by 20 percent (see MentorshipEngine.ExecuteTerminateMentorshipAsync).
        // 0 means no active penalty.
        public long XpPenaltyExpiresEpoch;

        public int AutoEatThreshold;
        public int Food1_ItemId;
        public int Food1_Count;
        public int Food2_ItemId;
        public int Food2_Count;
        public int Food3_ItemId;
        public int Food3_Count;

        public long EquippedWeaponId;
        public bool EquippedWeaponAffixLocked;
        
        public long EquippedArmorId;
        public bool EquippedArmorAffixLocked;

        public int CachedMiningMonolithLevel;
        public int CachedWoodcuttingMonolithLevel;
        public long GuildId;
        public long ActiveGuildWarId;
        public System.Guid ActiveCrossShardMatchId;
        public int ActiveMatchMmr;
        public long GlobalNodeRemainingHp;
        public float CachedWarMultiplier;
        public int GuildCombatVanguardPoints;
        public int GuildProductionLogisticsPoints;
        public int GuildGatheringSupplyChainPoints;
        public int EnemyCombatVanguardPoints;
        public int EnemyProductionLogisticsPoints;
        public int EnemyGatheringSupplyChainPoints;
        
        // Alchemy Buffs
        public int ActiveOffensivePotionId;
        public int OffensivePotionDurationMs;
        public int ActiveDefensivePotionId;
        public int DefensivePotionDurationMs;

        public long WorldBossMaxHp;
        public long WorldBossCurrentHp;
        public int ActiveGlobalEventId;

        // Village Infrastructure
        public int CachedCurrentToolTier;
        public int CachedMaxPopulationCapacity;
        public int CachedInnMaturationBonus;

        // Modul: Prestige perk tree (see LegacyPerkResolver) and the
        // Logistics achievement family's stackable gathering-speed reward
        // (see AchievementMilestones) - both cached here from PlayerRecord
        // at session load (StateCheckpointManager) so combat/gathering math
        // can read them on the 10Hz hot path without a database round trip.
        public long CachedLegacyPerks;
        public int CachedLogisticsGatheringSpeedBonusPct;

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1
        // (UiSeasonPassWindow). Mirrors PlayerChroniclePass.
        // ClaimedMilestonesBitmask, loaded at session start/ReloadState
        // (StateCheckpointManager) - previously tracked only in the
        // database, never cached onto the live payload or sent to the
        // client.
        public ulong CachedClaimedMilestonesBitmask;

        // Modul: Phase - Full-Stack Production Polish, Part 1.1 (Offline
        // "Welcome Back" flow). Populated once, at login, by
        // OfflineSimulationEngine.ExtrapolateOfflineProgressAsync - the
        // exact delta this catch-up projection just granted, not a running
        // total. Never reset afterward (matches LastCommandResultTick/
        // LastSkillCastResultTick's own established idiom exactly):
        // OfflineSummaryTick only increments when a real, non-zero
        // catch-up actually ran, and the client is responsible for
        // edge-detecting a change in that tick value (never re-showing the
        // same summary twice for an unchanged tick) rather than the server
        // clearing these fields after one broadcast.
        public long OfflineElapsedSeconds;
        public long OfflineGoldEarned;
        public long OfflineXpEarned;
        public int OfflineMaterialDropsGranted;
        public byte OfflineSummaryTick;
        public byte ForgeLevel;
        public byte InnLevel;
        public byte BreedingLevel;
        public byte AcademyLevel;
        public byte CurrentPopulationCount;
        public byte ActiveMentorshipContractCount;

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        public byte LumberjackLevel;
        public byte QuarryLevel;
        public byte MineLevel;
        public byte WarehouseLevel;

        // Modul 16: timed upgrade queue - PendingUpgradeBuildingId == 0 means
        // no upgrade is currently in flight for this player's village.
        public byte PendingUpgradeBuildingId;
        public long PendingUpgradeCompletesAtEpoch;

        // In-memory mirror of the player's wood/stone/iron_ore CommodityRecords,
        // refreshed at login. Used by the 10 Hz tick to check the warehouse cap
        // without a DB read on the hot path.
        public long CachedWoodStock;
        public long CachedStoneStock;
        public long CachedIronOreStock;

        // Deltas awaiting write-behind flush into CommodityRecords (see
        // RedisSessionCache.TryStoreFrame / RedisWriteBehindEngine), mirroring
        // the existing RedisPendingGoldDelta pattern below.
        public long PendingWoodDelta;
        public long PendingStoneDelta;
        public long PendingIronDelta;

        // Fractional-tick production accumulators. Internal bookkeeping only,
        // never read by StateUpdatePacket.
        public float AccumulatedWood;
        public float AccumulatedStone;
        public float AccumulatedIron;

        public int ActiveChildMaturationMs;

        // Codex and Achievements
        public int CompletedAreaFlags;
        public int ClaimedAchievementFlags;
        public uint TotalAchievementsClaimedCount;

        // Modul 13: live counters for the auto-awarded tiered achievements
        // (Treasury reuses CurrentGold directly, no separate counter needed).
        // Evaluated against AchievementMilestones during StateCheckpointManager
        // flushes; never queried or allocated on the hot path.
        public int ForgeUpgradeCount;
        public int HighestForgeSynthesisTier;
        public long HarvestLoopCount;

        // Modul 16/21: cached, pre-summed equipped-gear stat bonuses (weapon +
        // armor combined). Recomputed only by EquipmentSlotEngine on equip/
        // unequip (see EquipmentSlotUpdateQueue), read as plain O(1) field
        // access from StatsCalculator every tick - never re-parsed from JSON or
        // re-queried from the DB on the hot path.
        public int CachedEquippedFlatAttack;
        public int CachedEquippedFlatDefense;
        public int CachedEquippedCritBonus;
        public int CachedEquippedLuckBonus;

        // Cached passive Codex multipliers. Recomputed only on login or Codex
        // level-up (see CodexEngine.RecalculateAndSyncMultipliersAsync); read as
        // plain O(1) field access from the 10 Hz tick, never recalculated there.
        public float CachedCodexYieldMultiplier = 1.0f;
        public float CachedCodexDamageMultiplier = 1.0f;

        // Modul: daily quest live tracking - loaded once from
        // DailyQuestRecords at login (see QuestEngine.LoadIntoPayloadAsync)
        // and mutated as plain struct field increments inside the 10 Hz
        // tick (monster-kill resolution, CraftingCompletionQueue drain),
        // satisfying the "quest-tracking must use pre-allocated buffers"
        // constraint - no dictionary/list allocation occurs on the
        // increment path. QuestSlotDirty marks which slots changed since
        // the last flush so StateCheckpointManager's periodic write-back
        // (QuestEngine.FlushDirtySlotsAsync) only persists what actually
        // moved. QuestType: 0 = KillMonsters, 1 = CraftItems (see
        // QuestEngine.QuestType*).
        public long DailyQuestDateKeyUtc;
        public byte QuestSlot0Type;
        public int QuestSlot0Target;
        public int QuestSlot0Progress;
        public bool QuestSlot0Claimed;
        public byte QuestSlot1Type;
        public int QuestSlot1Target;
        public int QuestSlot1Progress;
        public bool QuestSlot1Claimed;
        public byte QuestSlot2Type;
        public int QuestSlot2Target;
        public int QuestSlot2Progress;
        public bool QuestSlot2Claimed;
        public bool QuestProgressDirty;

        // Modul: Full-Stack Production Hardening Phase 3, Part 5. Replaces
        // the single-slot LastCommandResultCode/LastCommandResultTick pair -
        // a scalar could only ever carry the single most recent rejection,
        // so a client that missed exactly one broadcast (e.g. across a
        // reconnect gap) while two or more commands were rejected back to
        // back would only ever see the last one; earlier rejections were
        // silently and permanently lost. Four explicitly named slots (not
        // a managed array) match this codebase's established fixed-size-
        // buffer convention (see ActiveSkillEngine's four cooldown fields) -
        // fully embedded value data, no heap array, no allocation risk
        // from struct-copy or session re-creation. CommandResultRingWriteIndex
        // tracks which slot the next append overwrites (mirrors
        // PlayerSessionRegistry.CommandResultQueue's drain in
        // SimulationEngine - see that drain loop's own comment).
        // CommandResultTickCounter is a per-player monotonically
        // increasing counter stamped onto each new entry (never reset), so
        // the client can always tell which of the 4 slots are newer than
        // what it has already displayed, and in what order to apply them.
        public CommandResultEntry CommandResultSlot0;
        public CommandResultEntry CommandResultSlot1;
        public CommandResultEntry CommandResultSlot2;
        public CommandResultEntry CommandResultSlot3;
        public byte CommandResultRingWriteIndex;
        public uint CommandResultTickCounter;

        // Race Masteries
        public int HumanMasteryLevel;
        public int VilaMasteryLevel;
        public int DraugrMasteryLevel;
        // Modul 13: session-cached mastery levels for the remaining three races.
        // Server-internal only (used to gate passive bonuses live) - not mirrored
        // into StateUpdatePacket since /api/v1/mastery/snapshot serves the client.
        public int KoboldMasteryLevel;
        public int VodnikMasteryLevel;
        public int MoosleuteMasteryLevel;
        public long LogicEpochCounter;
        // Chrono-buffer: unmanaged registers — operated on 100% allocation-free on hot-path.
        public double BankedChronoSeconds;
        public bool IsChronoAccelerating;
        public double ActiveChronoSpeedMultiplier;
        public long ActiveChronoLockExpirationTicks;
        public int LegacyShardBalance;
        public int CitizenMultiSlotsUnlocked;
        public long GuildLogisticsCurrentStock;
        public long GuildLogisticsTargetRequirement;
        public int CachedGuildLogisticsLevel;
        public long CombatSimulationMatchId;
        public int CombatSimulationTurnCounter;
        public int CombatSimulationDamageDelta;

        // Co-op PvE guild raid boss cache (distinct from the PvP CombatSimulation* fields above).
        public int CachedGuildRaidTier;
        public long CachedGuildRaidBossCurrentHp;
        public long CachedGuildRaidBossMaxHp;
        public long ActiveMentorPlayerId;
        public double MentorshipExpBonusMultiplier;
        
        public uint NetworkDiagnosticsToken;

        // Redis write-behind session flags. Internal only; never serialized into network packets.
        public bool RequiresRedisFlush;
        public long RedisPendingGoldDelta;
        public FolkIdle.Server.Models.ObfuscatedInt64 ObfuscatedGold;
        public FolkIdle.Server.Models.ObfuscatedInt32 ObfuscatedPremiumCurrency;
        public FolkIdle.Server.Models.ObfuscatedInt32 ObfuscatedLegacyShards;
        public long ObfuscationSessionKey;
        public uint ActiveChallengeSeed;
        public long ActiveChallengeIssuedAtMs;
        public byte ActiveChallengeAnswered;
        public bool IsQuarantined;
        public byte ActiveLanguageState;
        public byte WorldBossAttemptCount;
        public uint ActiveUiContextBitmask;
        public uint ActiveChroniclePassLevel;
        public uint AccumulatedSeasonalXp;

        // Active Skill Tree (see ActiveSkillEngine). CurrentMana is a live
        // combat-session resource, not persisted to PlayerRecord - it resets
        // to full at login, the same convention PlayerHp already uses.
        // AvailableSkillPoints and UnlockedSkillsBitmask ARE persisted (see
        // PlayerRecord.AvailableSkillPoints and the PlayerSkillUnlocks table,
        // hydrated once at login into this bitmask so the hot loop never hits
        // the DB to check unlock state). Cooldown expiry timestamps are
        // Environment.TickCount64 milliseconds, not wall-clock epoch.
        public int CurrentMana;
        public int AvailableSkillPoints;
        public uint UnlockedSkillsBitmask;
        public long Skill1CooldownExpiresAtMs;
        public long Skill2CooldownExpiresAtMs;
        public long Skill3CooldownExpiresAtMs;
        public long Skill4CooldownExpiresAtMs;

        // Set by a successful RequestCastSkill and consumed by the very next
        // attack resolution in ProcessSubTick (then reset to 0) - "injected
        // into the next tick's StatsCalculator combat resolution" per the
        // task. 0 means no active skill bonus.
        public float PendingSkillDamageMultiplier;
        public byte LastSkillCastId;
        public byte LastSkillCastSuccess;
        public uint LastSkillCastResultTick;

        // Modul: status synergy bits applied to the player's currently
        // fought monster by active skills (Chilled/Vulnerable). PvE combat
        // is strictly one player vs one active monster at a time and
        // monsters carry no independent session state, so a player-scoped
        // field is semantically equivalent to a target-status field. Reset
        // to 0 on monster kill/respawn so a status never leaks onto the
        // next monster. Primitive byte bitmask - zero allocation.
        public byte TargetStatusEffectBitmask;

        public void InitializeObfuscation(long sessionKey)
        {
            ObfuscationSessionKey = sessionKey == 0L ? PlayerId ^ 0x5F3759DF5F3759DFL : sessionKey;
            ObfuscatedGold = new FolkIdle.Server.Models.ObfuscatedInt64(CurrentGold, ObfuscationSessionKey);
            ObfuscatedPremiumCurrency = new FolkIdle.Server.Models.ObfuscatedInt32(PremiumCurrency, (int)(ObfuscationSessionKey & 0x7FFFFFFF));
            ObfuscatedLegacyShards = new FolkIdle.Server.Models.ObfuscatedInt32(LegacyShardBalance, (int)((ObfuscationSessionKey >> 17) & 0x7FFFFFFF));
        }

        public void SetGold(long value)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            CurrentGold = value;
            ObfuscatedGold.Value = value;
        }

        public void AddGold(long delta)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            CurrentGold += delta;
            ObfuscatedGold.Value = CurrentGold;
        }

        public void SetPremiumCurrency(int value)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            PremiumCurrency = value;
            ObfuscatedPremiumCurrency.Value = value;
        }

        public void SetLegacyShards(int value)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            LegacyShardBalance = value;
            ObfuscatedLegacyShards.Value = value;
        }
    }
}

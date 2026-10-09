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

        // Modul: the player's auto-salvage floor, mirrored from
        // PlayerRecord.AutoSalvageBelowTier at hydration.
        //
        // Here rather than read per kill because CombatLootEngine runs off the
        // tick thread and has no payload access - the same reason LootLuckPct
        // and BonusRarityTiers ride CombatLootDropRequest. A drop at or below
        // this tier is sold on the way in instead of becoming a row; 0 is off,
        // and off is the default. See PlayerRecord.AutoSalvageBelowTier for why
        // the chest needed a drain at all.
        public int AutoSalvageBelowTier;

        // Modul: the per-region auto-sell rules (task 81), packed as
        // ChestSalvageRules describes. Mirrored from
        // PlayerRecord.AutoSalvageRegionTiers at hydration, for the same reason
        // as the field above. CombatLootDropRequest.Build folds the two into one
        // tier, so the loot engine never reads this.
        public int AutoSalvageRegionTiers;

        public bool IsDirty;
        public int TicksSinceLastFlush;

        // Modul: checkpoints off the tick thread (task 43). How many of this
        // player's flushes are queued on CheckpointWriter and not yet
        // acknowledged. RequestFlush stamps a snapshot's epoch as
        // LogicEpochCounter + FlushesInFlight, so a second flush requested
        // before the first commits can never look split-brained against it.
        // Runtime-only: not on the wire, not persisted, carried across a
        // reload by StateReloadMerge.
        public int FlushesInFlight;
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

        // Combat mechanics
        public int CurrentMonsterId;
        // Milli-HP (1 HP = 1000). MUST be long: authored content already
        // ships a 3,000,000 HP boss, and 3,000,000 * 1000 exceeds int.MaxValue,
        // which wrapped negative and made the monster spawn already dead - see
        // the regression note in ContentRegistry.GetScaledMonsterMaxHp.
        public long CurrentMonsterHp;
        public int PlayerHp;
        public int CombatTargetTickAccumulator;
        
        public bool IsSuspended;

        // Modul: when this client last SAID anything, in Environment.TickCount64.
        //
        // Used by the anti-cheat challenge to tell a backgrounded tab from a
        // client that is refusing to answer. Server-side only - the wire packet
        // is a separate struct, so this costs nothing on the network.
        public long LastClientCommandAtMs;
        public bool Quarantine_Active;

        public long CurrentGold;
        public int PremiumCurrency;

        // Modul: the seasonal event currency (pumpkins for Samhain). Owned by
        // the live payload like PremiumCurrency: rolled on the tick, spent on
        // the tick, written by the checkpoint. EventCurrencyEventId stamps
        // WHICH event the balance belongs to, so a new event starts from zero
        // instead of inheriting last year's pumpkins. The day pair is the
        // daily earning cap - see SeasonalEventEarning.
        public int EventCurrency;
        public int EventCurrencyEventId;
        public int EventCurrencyDayKey;
        public int EventCurrencyEarnedToday;

        public long LastCommandTimestamp;

        // Gathering mastery
        public int WoodcuttingMasteryXp;
        public int WoodcuttingMasteryLevel;
        public int MiningMasteryXp;
        public int MiningMasteryLevel;
        public int FishingMasteryXp;
        public int FishingMasteryLevel;
        public int HerbalismMasteryXp;
        public int HerbalismMasteryLevel;
        public int GatheringProgressTicks;
        public int VillagePopulation;

        public int STR;
        public int DEX;
        public int CON;
        public int LCK;

        // Modul 13.4.3: cached lineage flags, hydrated at login from the active
        // character's CharacterLineages row (see StateCheckpointManager.
        // LoadPlayerState). The Speed/Crit/Yield loci and IsInbred that lived
        // here were retired on 2026-09-13 - traits replaced both.
        public bool IsEpicMutation;

        // Modul: the active character's trait bits, hydrated beside the
        // aptitudes and read through TraitTotals.From on the tick - never from
        // the database on the hot path.
        public long TraitMask;

        // Modul 13.4.3: unix-epoch-seconds until which character XP generation
        // is reduced by 20 percent (see MentorshipEngine.ExecuteTerminateMentorshipAsync).
        // 0 means no active penalty.
        public long XpPenaltyExpiresEpoch;

        public int AutoEatThreshold;

        // Modul: AUTO-EAT HAS A COOLDOWN NOW, and it is the difference between
        // gear mattering and food substituting for it.
        //
        // The larder fed once per TICK - ten bites a second - so healing was
        // bounded only by how many fish a player owned. No fight could be lost
        // while stock remained, whatever the monster hit for and whatever the
        // player wore, which made every boss a check on inventory rather than
        // on equipment.
        //
        // Ticks remaining before the next bite. Not a timestamp: the payload is
        // blitted onto the wire and a counter that ticks down survives a
        // process restart the way an Environment.TickCount64 deadline does not.
        public int AutoEatCooldownTicks;

        // Modul: TASK 55 - whether auto-eat took a bite during the CURRENT fight,
        // for the "Starved" boss challenge. Set at the bite, cleared at the two
        // places a fight starts (the first spawn and the respawn after a kill).
        // Runtime only: not on the wire, not checkpointed - a relogin starts a
        // fresh fight, and a missed clear could only make the challenge harder.
        public bool AteThisFight;

        // Modul: RATIONS (FoodRegistry.RationIntervalTicks). Ticks of combat
        // since the last ration, and whether the last one went unpaid. Runtime
        // only, like AteThisFight: a relogin restarts the count, which can only
        // delay one ration by a few seconds, and Hungry is re-derived at the
        // next ration from what the larder actually holds.
        public int RationTicksSinceMeal;
        public bool Hungry;

        // Modul: TASK 87 - the Boss Ascension ladder. BossAscensionPacked is a
        // CACHE of boss_ascension_progress (the authority), four bits a region,
        // filled at login and raised at a clear, so the tick can validate a
        // start-step without a query - the same arrangement as
        // DefeatedRegionBossMask. The rest is the ARMED attempt and is runtime
        // only, not checkpointed: an attempt does not survive a relogin (a
        // reload simply disarms it, which AscensionStep on the wire tells the
        // client). AscensionCharacterId pins it to the character that started
        // it, because the payload's fight register is swapped per slot and a
        // second character standing at the same boss must not inherit it.
        public int BossAscensionPacked;

        // Modul: TASK 84 - the Great Works. A CACHE of great_works_progress (the
        // authority), three bits a region holding how many stages are built,
        // filled at login and refreshed by GreatWorksTickCoordinator.DrainUpdates
        // after a deposit commits. The bonuses are DERIVED from it on every read
        // (GreatWorksRegistry.YieldPct / OfflineMinutes), never stored, so there
        // is no second copy to drift. Not on the wire: the panel reads REST.
        public int GreatWorksStagesPacked;
        public byte AscensionStep;
        public byte AscensionRegion;
        public System.Guid AscensionCharacterId;
        /// <summary>0 nothing to report, 2 the kill missed the step's time limit. Drained into a command result by the tick.</summary>
        public byte AscensionPendingResult;
        public int Food1_ItemId;
        public int Food1_Count;
        public int Food2_ItemId;
        public int Food2_Count;
        public int Food3_ItemId;
        public int Food3_Count;


        // Modul: inventory census. The backpack's total slot count, so
        // InventorySpaceRemaining can be recomputed as capacity minus a real
        // occupied-slot census rather than only ever decremented. Previously
        // capacity existed nowhere: hydration wrote "20 + human vault bonus"
        // straight into InventorySpaceRemaining and the number was thereafter
        // indistinguishable from remaining space, so nothing could ever restore
        // it. Also lets the client show "13/20" instead of a bare countdown.
        public int InventoryCapacity;

        // Modul: halt reasons. See Network.ActivityHaltReason. Owned by the
        // tick thread like the rest of this payload; set at each halt site and
        // cleared the moment an activity starts running again, so it is always
        // a statement about right now rather than a sticky history.
        public byte ActivityHaltReason;

        // Task 85: the ACTIVE REGISTER's automation rules, packed (see
        // Domain.Combat.AutomationRules). Per character, so it travels with the
        // character in SwapRegisterWith like ActivityHaltReason above; slots 2
        // and 3 park theirs on CharacterActivityState.AutomationRules. Hydrated
        // from characters."AutomationRules" and refreshed by
        // AutomationRulesQueue. Never on the wire - the rules panel reads REST.
        public long AutomationRules;

        public long EquippedWeaponId;
        public bool EquippedWeaponAffixLocked;

        public long EquippedArmorId;
        public bool EquippedArmorAffixLocked;

        // Modul: Full-Stack Expansion, Part 1. Third equipment slot -
        // Leggings, mirroring the weapon/armor pair above exactly.
        public long EquippedLeggingsId;
        public bool EquippedLeggingsAffixLocked;

        // Modul: per-character equipment. Helmet, gloves and boots. The old
        // model had one "Armor" slot standing in for all four armour pieces, so
        // three quarters of the armour catalogue was unwearable even though
        // AffixRegistry had always rolled slot-correct affixes for it.
        //
        // Like every other equipment field here, these are the ACTIVE
        // character's - the tick swaps each slot's own gear in and out through
        // SimulationEngine's register, so ProcessSubTick and StatsCalculator
        // read whichever character is currently being simulated without either
        // of them knowing slots exist.
        public long EquippedHelmetId;
        public long EquippedGlovesId;
        public long EquippedBootsId;
        public long EquippedAmuletId;
        public long EquippedRingId;

        // Modul: lifetime statistics. Deliberately ABSOLUTE running totals
        // hydrated from PlayerRecords at login, not deltas accumulated since
        // the last flush.
        //
        // The checkpoint writes these with plain assignment, so flushing the
        // same snapshot twice - which the batch and single-flush paths can both
        // do, and which a retried transaction does by definition - lands on the
        // same value instead of double counting. A "+= pending, then clear"
        // design would need the clear to happen back on the tick thread after a
        // successful off-thread commit, which is exactly the kind of seam that
        // has produced silent data bugs in this codebase before.
        public long LifetimeDeaths;

        // Modul: crafted-item counter. Unlike LifetimeDeaths above, this one is
        // hydrated and incremented for DISPLAY ONLY and is never written back by
        // the checkpoint. CraftingEngine already persists TotalItemsCrafted
        // inside the same transaction as the item grant, so it is the single
        // author; a checkpoint writing an absolute snapshot on top of that would
        // clobber any craft that committed between hydration and flush.
        //
        // The tick thread mirrors those increments here so the value the client
        // sees moves in real time rather than only after a relog - the tutorial
        // controller detects a completed craft purely from this counter rising.
        public long LifetimeItemsCrafted;

        // Modul: race unlock feedback. Which playable races this account owns,
        // as bit (raceId - 1). Six races fit one byte with two to spare.
        //
        // A monotonic MASK rather than a one-shot "you just unlocked X" field.
        // A one-shot needs the server to know the client saw it before clearing
        // it, and gets lost entirely on a disconnect between the grant and the
        // next packet. A mask lets the client diff against the last value it
        // saw, so the toast survives a reconnect and cannot fire twice.
        public byte UnlockedRaceBitmask;

        // Playtime is derived rather than ticked: the value at login plus the
        // wall-clock seconds since. Counting it on the 10Hz tick would drift
        // against real time and cost an add per player per frame for a number
        // nothing reads more than once a session.
        public long PlayTimeSecondsAtLogin;
        public long SessionStartEpochSeconds;

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

        // Modul: Deferred Part 5 Implementation, Part 2. Active food buff
        // (HP regeneration over time) - same unmanaged int id + ms
        // countdown idiom as the potion pair above; see ConsumableEngine.
        public int ActiveFoodBuffId;
        public int FoodBuffDurationMs;

        // Modul: Economy Polish, Part 2. Town Hall passive gold. The
        // accumulator adds the hourly rate once per 10Hz tick; every
        // 36000 accumulated units (36000 ticks = one hour) equal exactly
        // one hour's rate in whole gold - pure integer arithmetic, no
        // floats, no drift, zero allocation on the tick.
        public int TownHallLevel;
        public long TownHallGoldAccumulator;

        public long WorldBossMaxHp;
        public long WorldBossCurrentHp;
        public int ActiveGlobalEventId;

        // Modul: the character's real maximum health, in milli, so the client
        // can scale a health bar against it.
        //
        // This is derived inside ProcessSubTick from level, lineage, gear,
        // inheritance, the Fortitude bough and the Endurance aptitude, and it
        // was thrown away every tick. The client therefore scaled the player's
        // bar against a session high-water mark of PlayerHp, which is wrong
        // from the first frame and only ever coincidentally right. Cached here
        // so the wire can carry it; not persisted, because it is recomputed
        // from scratch on every tick anyway.
        public long CachedEffectiveMaxHp;

        // Modul: A RESTED CHARACTER WALKS INTO A FIGHT AT FULL HEALTH,
        // 2026-10-08. PlayerHp is only ever moved by the combat tick, so a
        // character that had been crafting, gathering or offline arrived at
        // whatever the hydration default (100 HP) or its last fight left -
        // reported as "my max HP was 2000 and crept up". Set at login, by any
        // non-combat work and by a deploy INTO combat; the combat tick spends
        // it by filling the bar against the real maximum it just computed.
        // Per character: it travels in CharacterActivityState with the bar.
        public bool RestedHpPending;

        // Modul: the character's attack power, in milli, before any per-swing
        // roll. Cached for the same reason the health pool above is - it is
        // rebuilt from gear, lineage, level, inheritance, the guild buff, the
        // legacy perk and the Strength aptitude on every tick and then thrown
        // away.
        //
        // THE WORLD BOSS NEEDS IT. Its attack used to be a damage figure the
        // CLIENT computed about itself and posted, bounded only by a
        // 100,000,000 clamp. The server can answer that question itself, and
        // this is the answer - the same number the live tick swings with, not a
        // second derivation of it.
        public long CachedEffectiveMilliAttack;

        // Village Infrastructure
        public int CachedCurrentToolTier;

        // Modul: one tier per profession. CachedCurrentToolTier was a single
        // number taken from the forge building, so an axe accelerated fishing
        // and a rod accelerated mining - and neither was ever actually owned.
        public byte AxeToolTier;
        public byte PickaxeToolTier;
        public byte RodToolTier;

        // Modul: what the equipped tools add, summed. Percentages, because a
        // tool's job is to multiply a rate and a weighted table - a flat bonus
        // would mean nothing against either.
        public ushort ToolGatherSpeedPct;
        public ushort ToolGatherYieldPct;
        public ushort ToolRareFindPct;
        public int CachedMaxPopulationCapacity;
        public int CachedInnMaturationBonus;

        // Modul: Prestige perk tree (see LegacyPerkResolver) and the
        // Logistics achievement family's stackable gathering-speed reward
        // (see AchievementMilestones) - both cached here from PlayerRecord
        // at session load (StateCheckpointManager) so combat/gathering math
        // can read them on the 10Hz hot path without a database round trip.
        public long CachedLegacyPerks;

        // Modul: inheritance stats. Six bytes carrying the bought level of each
        // permanent bonus, hydrated once at login and refreshed when one is
        // purchased. Bytes rather than ints because the cap is 20, and a fixed
        // set of fields rather than an array because this struct is blitted
        // onto the wire - see StateUpdatePacket.
        public byte Inherit_Damage;
        public byte Inherit_MaxHp;
        public byte Inherit_XpGain;
        public byte Inherit_GoldGain;
        public byte Inherit_GatheringYield;
        public byte Inherit_LootLuck;

        // Modul: REBIRTH (task 88). RenownedRebirths is hydrated from
        // PlayerRecords at login and never written back by the checkpoint -
        // RebirthEngine is its one writer, and a rebirth ends in a reload, so
        // the live copy is always the one the database just committed. Read on
        // the damage path through RebirthRules.DamageBonusPct.
        //
        // RebirthPending is RUNTIME-ONLY: set on the tick when a rebirth
        // suspends the session, it tells the Logout branch not to flush a
        // payload that is about to be reset, and StateReloadMerge not to carry
        // the old life's fight onto the reborn one. A reload replaces the
        // payload, which clears it.
        public int RenownedRebirths;
        public bool RebirthPending;

        // Modul: SKILL TREE. Five bytes, one per branch, same shape and same
        // reasons as the inheritance bytes above: the cap is 20, and this
        // struct is blitted onto the wire so an array is not an option.
        //
        // These reset with the season. Skill points come from account levels
        // and the season takes those back, so a tree that survived would be
        // paid for twice.
        public byte Skill_LootRarity;
        public byte Skill_WorldBossDamage;
        public byte Skill_CritChance;
        public byte Skill_CritDamage;
        public byte Skill_XpGain;

        // Modul: RINGS TWO AND THREE. Ten boughs and five crowns, one byte
        // each, in SkillTreeRegistry id order (5-19) so the ids index this
        // block directly.
        //
        // Fifteen named fields rather than an array for the same reason the
        // five above are: this struct is blitted, and C# has no blittable
        // array short of an unsafe fixed buffer. It is repetitive and it is
        // the cheap half - the registry entry and the EFFECT are the work.
        public byte Skill_Plenty;
        public byte Skill_Rarity;
        public byte Skill_FirstBlood;
        public byte Skill_TrophyHunter;
        public byte Skill_Guile;
        public byte Skill_Relentless;
        public byte Skill_Bloodthirst;
        public byte Skill_Fortitude;
        public byte Skill_Craft;
        public byte Skill_Harvest;
        public byte Skill_GoldenFleece;
        public byte Skill_Thunderer;
        public byte Skill_DoubleStrike;
        public byte Skill_LastStand;
        public byte Skill_Scholar;

        // Modul: whether a respec is available, so the panel can say so
        // without a REST round trip. See PlayerRecord for why there are two
        // of them rather than one "respecs remaining".
        public byte FreeRespecUsed;
        public byte PaidRespecGrants;

        // Modul: the ACTIVE character's aptitudes, so the tick can read them
        // without touching the database. Bred by BreedingAptitudes, stored on
        // character_lineage_registry, and hydrated here from slot 1's lineage.
        //
        // Per-character rather than per-account, unlike inheritance: aptitudes
        // are what a bloodline carries, so switching to a different child has
        // to change them.
        public byte Aptitude_Strength;
        public byte Aptitude_Skill;
        public byte Aptitude_Endurance;
        public byte Aptitude_Fortune;

        // Modul: ticks until Last Stand is available again.
        //
        // A COUNTDOWN, matching AutoEatCooldownTicks rather than storing an
        // absolute tick, because this payload has no absolute clock - and a
        // countdown survives the checkpoint round trip without needing one.
        // Server-side only; the client has no use for it and this struct is
        // not the wire packet, so it costs nothing on the network.
        public int LastStandCooldownRemaining;

        // Modul: kills since Golden Fleece last paid out. Server-side only -
        // the client draws the counter from nothing today, and this struct is
        // not the wire packet, so it costs no bytes on the network.
        public int KillsSinceFleece;
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

        // Modul: the welcome-back card said "you earned X" for a household of
        // up to three workers. Per-character so the player can see which one
        // is idle and which one is carrying them.
        public int OfflineSlot1Gold;
        public int OfflineSlot1Xp;
        public int OfflineSlot1Drops;
        public int OfflineSlot2Gold;
        public int OfflineSlot2Xp;
        public int OfflineSlot2Drops;
        public int OfflineSlot3Gold;
        public int OfflineSlot3Xp;
        public int OfflineSlot3Drops;
        public long OfflineXpEarned;
        public int OfflineMaterialDropsGranted;

        // Modul: the previous two clamps (theoretical window ceiling, then
        // live warehouse room) silently dropped whatever a full warehouse
        // couldn't hold - a system that renders correctly while doing
        // nothing, CLAUDE.md's own recurring shape. This is that discarded
        // amount, made visible. Populated once, at login, same as its
        // siblings above - the exact amount THIS catch-up discarded, never a
        // running total.
        public long OfflineMaterialsLostToFullWarehouse;
        public byte OfflineSummaryTick;

        // Modul: THE TWO MOMENTS THE GAME NEVER MARKED.
        //
        // A first boss clear and a death are the two loudest things that happen
        // to a character, and the client learned about neither: death showed up
        // as an ActivityHaltReason badge in the corner, and a first clear - the
        // hardest fight in the game, carrying five times the boss's health -
        // produced no acknowledgement at all. The player found out they had
        // unlocked a race by noticing a new option on a different screen.
        //
        // Carried on the packet rather than fetched, following the offline
        // summary exactly: the numbers are small and fixed, and the alternative
        // is a table, an endpoint and an acknowledgement round trip for
        // something shown once and dismissed.
        //
        // Both TICK fields are EDGES, not values - the same contract
        // OfflineSummaryTick established. They increment only when a real event
        // happened and never reset, so the client compares against its own
        // last-seen value and shows each thing exactly once.
        public int LastVictoryMonsterId;
        public int LastVictoryDurationSeconds;
        public long LastVictoryGold;
        public long LastVictoryXp;
        public byte LastVictoryTick;

        // Modul: personal records (task 51, PersonalRecords.cs). Lifetime bests
        // the tick sees happen: the highest single hit in whole hit points, and
        // each region boss's fastest kill in tenths of a second (0 = never).
        // Hydrated at login and merged by the checkpoint - never reset.
        public int BestHit;
        public int BossBestKillTenthsR1;
        public int BossBestKillTenthsR2;
        public int BossBestKillTenthsR3;
        public int BossBestKillTenthsR4;
        public int BossBestKillTenthsR5;

        public int LastDeathMonsterId;
        public byte LastDeathTick;

        // Modul: WHAT THE HIT LOOKED LIKE.
        //
        // This protocol carries no combat event of any kind - the client infers
        // every hit from the difference between two CurrentMonsterHp snapshots
        // (see the client's damage.ts). That is enough to show a number and
        // nothing else: it cannot know whether the blow crit, and it cannot
        // know what the character is swinging, so every hit looked identical.
        //
        // Two bytes fix both. LastHitWasCrit is set by the player's own attack
        // resolution - the only place that rolls it - and EquippedWeaponKind is
        // resolved from the equipped weapon's base id so the client does not
        // have to fetch an inventory to know whether to draw a slash, an arrow
        // or a burst.
        //
        // A BYTE RATHER THAN AN EVENT, deliberately. At 10 Hz against a ~1.5s
        // swing there is at most one hit between snapshots in ordinary play, so
        // "the last hit crit" answers the question the client actually asks
        // without inventing an event stream this wire has never had.
        public byte LastHitWasCrit;
        public byte EquippedWeaponKind;
        public byte ForgeLevel;
        public byte InnLevel;
        public byte BreedingLevel;
        public byte AcademyLevel;
        public byte CurrentPopulationCount;
        public byte ActiveMentorshipContractCount;

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        public byte LumberjackLevel;
        public byte MineLevel;
        public byte WarehouseLevel;

        // Modul: Play Mode audit fix - see StateUpdatePacket's own comment.
        // TownHallLevel already existed above (int, used by
        // GetTownHallGoldRatePerHour) - reused directly rather than adding
        // a colliding duplicate. CraftingWorkshopLevel is genuinely new.
        public byte CraftingWorkshopLevel;

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
        // the existing RedisPendingGoldDelta pattern below. Like gold, what
        // Redis did not take is banked by the checkpoint (FlushState /
        // FlushBatch), carried on the job and handed back by a failed ack.
        //
        // Modul: NOTHING WRITES THESE ANY MORE (2026-09-30). The village tick
        // produced "wood" / "iron_ore" through them; it produces the tier log
        // and ore now, through PendingVillage* and VillageProductionQueue,
        // which never touches Redis. The write-behind and the checkpoint still
        // bank whatever a buffer held from before, so these stay until that
        // has had a deploy to empty.
        public long PendingWoodDelta;
        public long PendingStoneDelta;
        public long PendingIronDelta;

        // Fractional-tick production accumulators. Internal bookkeeping only,
        // never read by StateUpdatePacket.
        //
        // Modul: Wood and Iron are GONE, 2026-09-30, with the "wood" /
        // "iron_ore" production they drove. The Lumberjack and Mine produce the
        // region's catalogued log and ore now, through the integer
        // accumulators below - VillageManagementEngine.AccrueProduction, the
        // same function the offline window calls. Stone never had a producer.
        public float AccumulatedStone;

        // Integer production accumulators (ProductionUnitScale = one unit),
        // runtime-only: at most one unit is in flight at a logout.
        public long LumberjackProductionAccumulator;
        public long MineProductionAccumulator;

        // Units produced this session, so the 1-in-10 rare share lands on the
        // same count an offline window of the same length pays
        // (VillageManagementEngine.RareShareOf).
        public long VillageLogUnitsProduced;
        public long VillageOreUnitsProduced;

        // Produced but not yet written: SimulationEngine hands them to
        // VillageProductionQueue once a minute and at session end, and the
        // drain writes them under the Warehouse cap.
        public long PendingVillageLog;
        public long PendingVillageRareLog;
        public long PendingVillageOre;
        public long PendingVillageRareOre;
        public int VillageProductionTicksSinceGrant;

        public int ActiveChildMaturationMs;

        // Codex and Achievements
        public int CompletedAreaFlags;

        // Modul: how far the player has actually GOT, 1-5.
        //
        // Gathering was completely ungated: a brand new character could work
        // the Abyssal Breach node on their first minute, which made the five
        // locations decoration rather than progress. CompletedAreaFlags could
        // not answer this - it means "killed every monster here a thousand
        // times", which is a completion badge, not a passport.
        //
        // One kill anywhere in a location is what counts as having reached it.
        public int HighestLocationReached;

        // Modul: which region bosses this player has already put down, one bit
        // per region.
        //
        // A boss is five times its health and twice its attack until it falls
        // once - see BossFirstClearRules - so the tick loop has to know, and
        // the tick loop has no database in reach. The authority is still the
        // monster codex; this is a cache filled on load and updated when a boss
        // dies, exactly like HighestUnlockedRegion below.
        //
        // HighestUnlockedRegion cannot stand in for it: clearing region 5's
        // boss leaves that number at 5, because there is no sixth region to
        // open, so the last boss in the game would read as never beaten and
        // stay at first-clear stats forever.
        //
        // Server-side only. TickStatePayload is not the wire packet, so this
        // costs nothing on the network and needs no protocol regeneration.
        public byte DefeatedRegionBossMask;

        // Modul: region progression. The highest region this player may ENTER,
        // which is also the highest RegionTier of gear they may wear - see
        // RegionUnlockGate. 1 for a new account, rising by one each time a
        // region boss falls.
        //
        // Cached on the payload rather than read per command because the
        // ChangeActivity handler has a synchronous branch with no database in
        // reach, and because the client needs the number too: without it the
        // map cannot grey out what is closed, and the only way a player learns
        // a region is locked is by being refused after choosing it.
        //
        // Distinct from HighestLocationReached, which is descriptive ("I have
        // killed something there") and follows you around. This one is
        // permissive ("I am allowed there") and is the thing that stops
        // HighestLocationReached from wandering into region 5 on day one.
        public int HighestUnlockedRegion;
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
        // Modul: Affix System Unification - see EquippedAffixTotals for why
        // this replaced four separate cached ints.
        public EquippedAffixTotals CachedAffixTotals;

        // Modul: Architecture Overhaul, Part 4. Per-slot equipment SetId
        // (0 = no set), refreshed alongside the flat totals above on every
        // equip/unequip and at login - fed into SetBonusEngine.Evaluate at
        // stat-recalculation time via a stack-allocated span, never a
        // heap array.
        // Modul: seven-slot set bonuses. Was three loose ints that collapsed
        // four armour slots into one - see EquippedSetIds.
        public EquippedSetIds CachedSetIds;

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

        // Modul: task 79, phase 2. Gold earned on the tick, by source, not yet
        // written to gold_income_daily. Moves with the checkpoint job exactly
        // as RedisPendingGoldDelta does, but never through Redis - see
        // GoldIncomeTally. Every `RedisPendingGoldDelta +=` that is new income
        // has a GoldLedger.TallyIncome beside it (GoldIncomeLedgerTests).
        public GoldIncomeTally PendingGoldIncome;
        public FolkIdle.Server.Models.ObfuscatedInt64 ObfuscatedGold;
        public FolkIdle.Server.Models.ObfuscatedInt32 ObfuscatedPremiumCurrency;
        public FolkIdle.Server.Models.ObfuscatedInt32 ObfuscatedLegacyShards;
        public long ObfuscationSessionKey;
        public uint ActiveChallengeSeed;
        public long ActiveChallengeIssuedAtMs;

        // Modul: challenge epoch pinning, 2026-08-01.
        //
        // The epoch that was current when the challenge was ISSUED. The client
        // hashes the epoch it saw in the broadcast, so validating against
        // whatever LogicEpochCounter happens to be when the answer arrives
        // rejects correct answers whenever a checkpoint flush lands in between -
        // and a flush is triggered by ordinary play, including every reroll.
        //
        // The effect was a correct client accumulating challenge misses until
        // it was quarantined and shadow-banned, with latency making it worse.
        // Observed on the dev account during a normal session.
        public long ActiveChallengeIssuedEpoch;
        public byte ActiveChallengeAnswered;

        // Modul: challenge response policy. Consecutive unanswered integrity
        // challenges. Reset to zero the moment one is answered, so only a
        // sustained run of misses escalates - a single hitch, a backgrounded
        // frame or one slow round trip does not. See
        // AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit.
        public int ConsecutiveChallengeMisses;
        public bool IsQuarantined;
        public byte ActiveLanguageState;
        public byte WorldBossAttemptCount;

        public uint ActiveUiContextBitmask;
        public uint ActiveChroniclePassLevel;
        public uint AccumulatedSeasonalXp;

        // Modul: the four active skills are gone; the POINTS stayed.
        //
        // What was here - mana, an unlock bitmask, four cooldown timestamps and
        // three cast-result fields - served a rotation that measured at +90%
        // damage for clicking every three seconds. See SkillTreeRegistry.
        //
        // AvailableSkillPoints is persisted on PlayerRecord and is still earned
        // one per account level; the branch levels beside the inheritance bytes
        // above are what it buys now.
        public int AvailableSkillPoints;

        // Modul: ATTRIBUTES ARE SPENT, NOT DEALT, 2026-09-06.
        //
        // Levelling used to allocate STR/DEX/CON/LCK for the player, by race,
        // with no say in it - and the offline path forgot to do even that, so
        // the only account past level 1 sat at level 86 holding a fresh
        // registration's 50/50/50/25. A system nobody could see, nobody could
        // steer, and whose absence went unnoticed for months was not carrying
        // its weight.
        //
        // A level grants POINTS now and the player places them. Persisted on
        // PlayerRecord like AvailableSkillPoints above, and on the wire so the
        // screen can offer them.
        public int UnspentAttributePoints;

        // Set by a skill cast and consumed by the next attack. Nothing sets it
        // any more, and it is kept at zero rather than removed because the
        // damage step multiplies by it only when positive - a future active
        // ability would land here rather than inventing a second channel.
        public float PendingSkillDamageMultiplier;

        // Modul: status synergy bits applied to the player's currently
        // fought monster by active skills (Chilled/Vulnerable). PvE combat
        // is strictly one player vs one active monster at a time and
        // monsters carry no independent session state, so a player-scoped
        // field is semantically equivalent to a target-status field. Reset
        // to 0 on monster kill/respawn so a status never leaks onto the
        // next monster. Primitive byte bitmask - zero allocation.
        public byte TargetStatusEffectBitmask;

        // Modul: multi-slot simulation. Parked activity state for character
        // slots 2 and 3.
        //
        // Slot 1's activity state is NOT here - it is the flat
        // ActiveActivityId / PlayerHp / CurrentMonsterId / ... fields above,
        // which double as the tick's "active character register". Each tick the
        // engine swaps a slot's parked state into that register, runs the
        // ordinary per-activity tick against it, and swaps it back out (see
        // SimulationEngine.SwapSlotIntoActiveRegister). The swap is its own
        // inverse, and the loop always finishes with slot 1 loaded, so every
        // downstream consumer that reads the flat fields - the outbound packet,
        // the checkpoint flush, the offline extrapolation - keeps seeing the
        // main character exactly as it did before multi-slot existed.
        //
        // Doing it this way rather than indexing three uniform slot structs
        // left ProcessSubTick's 618 lines and all ~115 existing references to
        // the flat fields untouched, so the change could not silently alter
        // single-character behaviour.
        public CharacterActivityState Slot2Activity;
        public CharacterActivityState Slot3Activity;

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

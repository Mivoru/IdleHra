using System.Runtime.InteropServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{

    // Modul: halt reasons. Why a character is not currently earning, on the
    // wire so the client can name the cause. This exists because every one of
    // these states used to look identical to "idle by choice": the activity id
    // silently became 0 (or, for a full backpack, stayed non-zero while every
    // drop was discarded) and the player was left staring at a character that
    // had simply stopped, with nothing on screen saying why.
    public static class ActivityHaltReason
    {
        // Running normally, or idle because the player has not deployed.
        public const byte None = 0;

        // Auto-eat fired with all three larder slots empty. The activity is
        // stopped; restocking the larder and redeploying resumes it.
        public const byte OutOfFood = 1;

        // The character was killed and respawned at full HP. Combat activities
        // stop on death, gathering does not.
        public const byte Died = 2;

        // The activity is still running but the backpack is full, so every
        // material and equipment drop is being discarded. Not a stop - a
        // silent, ongoing loss, which is worse, and the reason this warning
        // is reported even while ActiveActivityId is non-zero.
        public const byte InventoryFull = 3;

        // No character is eligible to run an activity - typically the only
        // character is lent out as an Academy mentor.
        public const byte NoEligibleCharacter = 4;
    }

    // Modul: larder. Per-slot cap on stocked food. Chosen so a slot count
    // always fits the packet's ushort with room to spare, and so a single
    // command cannot move a player's whole cooking stockpile into the larder
    // in one click.
    public static class LarderLimits
    {
        public const int SlotCapacity = 999;
        public const int SlotCount = 3;
    }
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
        InventoryFull = 11,

        // Modul: Advanced Economy Refactoring, Part 2.1. Returned by
        // MarketEscrowEngine when a player without a guild (GuildId <= 0)
        // attempts to list or buy on the global market - trading requires
        // an active guild membership as its trade license.
        NoGuildLicense = 12,

        // Modul: Advanced Economy Refactoring, Part 2.3. Returned when a
        // player's CurrentLevel is below an item's derived RequiredLevel -
        // blocks both market purchase and equipping of over-leveled gear
        // (see EquipmentLevelGate).
        LevelTooLow = 13,

        // Modul: Architecture Overhaul, Part 2. Returned by
        // SimulationEngine.ChangeCharacterActivityAsync when another
        // character belonging to the same player already occupies the
        // requested gathering or combat activity id - see
        // CharacterSlotEngine.IsActivityOccupiedByAnotherSlot.
        NodeOccupied = 14,

        // Modul: Full-Stack Social Layer, Part 2. Returned by
        // RelationshipEngine.AddFriendAsync/BlockPlayerAsync when a
        // relationship row for the exact same (PlayerId, TargetPlayerId)
        // pair and RelationType already exists - the safe roll-back
        // condition the unique index on PlayerRelationships enforces.
        RelationshipAlreadyExists = 15
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StateUpdatePacket
    {
        public long PlayerId;
        public long ActiveActivityId;
        public int CurrentProgressTicks;
        public int RequiredProgressTicks;
        public int InventorySpaceRemaining;

        // Modul: inventory census. The backpack's total slot count, so
        // InventorySpaceRemaining can be recomputed as capacity minus a real
        // occupied-slot census rather than only ever decremented. Previously
        // capacity existed nowhere: hydration wrote "20 + human vault bonus"
        // straight into InventorySpaceRemaining and the number was thereafter
        // indistinguishable from remaining space, so nothing could ever restore
        // it. Also lets the client show "13/20" instead of a bare countdown.
        public int InventoryCapacity;

        // Modul: larder + halt reasons. Five fields were removed here to pay
        // for the larder's 13 bytes, all of them dead weight on a per-player
        // 10Hz packet:
        //   IsQuarantineActive              - a byte duplicating
        //     Quarantine_Active, which the client already reads (63 uses).
        //     Its only writer OR-ed the same two flags into both.
        //   UiScreenShakeIntensity          - never written and never read,
        //     anywhere, on either side.
        //   TotalAnalyticsEventsLoggedCount - a GLOBAL server counter, not
        //     player state.
        //   VisualActiveConnectionThroughput- a GLOBAL server metric.
        //   CurrentNodeMemoryLoadMetrics    - GC.GetTotalMemory(false)/1024,
        //     called once per player per tick to ship the server's own heap
        //     size to every client. The client mirrored all three into
        //     VisualSyncProxy properties that no UI element reads.
        // Server-diagnostic gauges belong on the /api/v1 metrics surface, not
        // on the hot path.

        // Modul: larder. The auto-eat larder, mirrored from TickStatePayload's
        // Food{1,2,3}_ItemId/_Count. Before this the payload's food slots were
        // read by four separate systems (auto-eat, both world-boss depletion
        // checks, the Chrono warp catch-up) and assigned by NOTHING - there was
        // no command, no UI and no persistence to put food in them - so every
        // player's larder was permanently empty and any combat activity halted
        // the first time HP crossed the auto-eat threshold. See
        // CommandType.StockFoodSlot.
        //
        // ushort rather than int on both axes: item ids run to ~140 and a slot
        // is capped at LarderSlotCapacity (999), so 2 bytes each is honest
        // sizing rather than 4 bytes of leading zeroes at 10Hz.
        public ushort Food1_ItemId;
        public ushort Food1_Count;
        public ushort Food2_ItemId;
        public ushort Food2_Count;
        public ushort Food3_ItemId;
        public ushort Food3_Count;

        // Modul: halt reasons. Why the activity is not running, so the client
        // can say so instead of showing a character that silently stopped.
        // ActiveActivityId dropping to 0 was previously indistinguishable from
        // "the player never deployed". See ActivityHaltReason.* constants.
        public byte ActivityHaltReason;

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

        public long EquippedChestId;

        // Modul: wire compaction for 6-slot equipment. Eight more fields were
        // removed here to pay for the equipment and roster registers below.
        // Every one was mirrored into a VisualSyncProxy property that no UI
        // element anywhere in the project reads - the same audit that removed
        // the server-diagnostic gauges in the previous pass, run again:
        //   ActiveMatchId (16 bytes)        - cross-shard match Guid
        //   LogicalEpochFrameIndex (8)      - duplicate of LogicEpochCounter
        //   ActiveChronoEngineStatus (4)    - one of FOUR chrono fields, of
        //                                     which only VisualBankedChronoSeconds
        //                                     and IsChronoAccelerating are read
        //   ActiveBankedChronoSeconds (4)   - as above
        //   VisualActiveMatchMmr (4)        - cross-shard MMR
        //   ActiveMasteryBitmask (4)        - Race Mastery reads REST instead
        //   CraftingEngineStatus (1)        - never written to anything
        //   NotificationQueueStateLength (1)- a global server queue depth
        // Total 42 bytes reclaimed. A 10Hz per-player packet is the most
        // expensive place in the game to carry a field, so anything no screen
        // renders does not belong on it.

        // Modul: 6-slot equipment sync. The ACTIVE character's full equipment.
        // EquippedArmorId became EquippedChestId: the old single "Armor" slot
        // held all four armour pieces, and chest is where the generic
        // "_armor_slot_" fallback still resolves (see
        // EquipmentSlotEngine.ResolveSlotIndex), so nothing moves.
        //
        // These are the character the tick is broadcasting - slot 1 in normal
        // operation, because the tick loop's register always ends a frame with
        // slot 1 loaded. Slots 2 and 3 do not ship their equipment on the hot
        // path: gear changes on a button press, not at 10Hz, and the roster
        // screen reads the other characters' loadouts from
        // /api/v1/player/inventory instead of paying 96 bytes a frame for data
        // that changes once a minute.
        public long EquippedHelmetId;
        public long EquippedGlovesId;
        public long EquippedBootsId;

        // Modul: roster registers. What characters 2 and 3 are doing, so the
        // roster screen can show three live characters rather than one.
        //
        // ushort, not the long that ActiveActivityId uses: activity ids are
        // gathering nodes (101-412) and monster ids (1-525), so the entire id
        // space fits in 16 bits nine times over. Slot 1's activity is not
        // duplicated here - it is ActiveActivityId above, and duplicating it
        // would create two values that could disagree.
        public ushort Slot2ActivityId;
        public ushort Slot3ActivityId;

        // Per-slot halt reason (see ActivityHaltReason). One character running
        // out of food says nothing about the other two, so "why is this one
        // idle" has to be answered per character or the roster would show the
        // main character's excuse against all three.
        public byte Slot2ActivityHaltReason;
        public byte Slot3ActivityHaltReason;

        public byte EquippedArmorAffixLocked;

        // Modul: Full-Stack Expansion, Part 1. Third equipment slot -
        // Leggings, mirroring the weapon/armor field pairs above (+9
        // bytes; see NetworkPacketLayoutGuard's updated pin).
        public long EquippedLeggingsId;
        public byte EquippedLeggingsAffixLocked;

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
        public byte ActiveLanguageState;
        public byte CurrentSimulationSpeedMultiplier;
        public uint PremiumCurrencyBalance;
        public byte ActiveAudioTrackId;
        public uint TotalItemsCraftedCount;
        public uint ActiveStatusEffectModifierBitmask;
        public uint RemainingBuffDurationTicks;
        public uint VisualBankedChronoSeconds;
        public ulong ActiveChronoLockExpirationTicks;
        public uint GlobalNodeRemainingHp;
        public uint NetworkDiagnosticsToken;
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

        // Modul: Play Mode audit fix. Town Hall/Crafting Workshop (see
        // VillageManagementEngine's TownHallBuildingId/CraftingWorkshopBuildingId)
        // existed server-side with real upgrade logic (gates every other
        // building's max level, feeds crafting rarity odds) but had no
        // client-visible level at all - discovered live when the client's
        // Village window turned out to have no way to ever raise the
        // level-2 ceiling every other building hits. 689 -> 691 bytes;
        // see NetworkPacketLayoutGuard's updated pin.
        public byte TownHallLevel;
        public byte CraftingWorkshopLevel;

        // Modul: Play Mode audit fix. LegacyStoreEngine's 3 prestige perks
        // (XP Multiplier/Gold Drop Rate/Combat Speed, packed into
        // PlayerRecord.LegacyPerks at byte offsets 0/8/16 - see
        // LegacyPerkResolver) already flowed into TickStatePayload.
        // CachedLegacyPerks and gated real combat math (XP/gold bonus %,
        // see ProgressionEngine/OfflineSimulationEngine's finalXpMultiplier
        // callers), but were never on the wire - the client had no way to
        // show current rank or compute next-rank cost, so the entire perk
        // half of the Legacy Shop (citizen-slot unlocks were already
        // reachable via CitizenMultiSlotsUnlocked) had no purchasable UI.
        // 691 -> 699 bytes; see NetworkPacketLayoutGuard's updated pin.
        public long LegacyPerksBitmask;

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

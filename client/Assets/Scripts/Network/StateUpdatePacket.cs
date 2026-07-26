using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
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
    // Modul: mirrors server/FolkIdle.Server/Network/StateUpdatePacket.cs
    // exactly - see that file's comment. Generic client error-feedback
    // channel: the CommandResult0-3 ring buffer carries the reason(s) the
    // most recently attempted rejectable command(s) (forge fusion, market
    // listing, guild contribution, reroll) failed, replacing the previous
    // silent no-op.
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
        TransactionPending = 9,

        // Modul: mirrors server CommandResultCode exactly - the forge
        // target item is already at MaxQualityTier.
        MaxTierReached = 10,

        // Modul: mirrors server CommandResultCode exactly - a mail claim
        // or bank withdraw could not be delivered because inventory space
        // is exhausted.
        InventoryFull = 11,

        // Modul: mirrors server CommandResultCode exactly - market
        // interactions require an active guild membership (trade license).
        NoGuildLicense = 12,

        // Modul: mirrors server CommandResultCode exactly - the player's
        // level is below the item's derived RequiredLevel.
        LevelTooLow = 13
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

        // Modul: mirrors server StateUpdatePacket exactly - third
        // equipment slot (Leggings).
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
        // exactly - a flattened 4-slot ring buffer replacing the previous
        // single-slot LastCommandResultCode/LastCommandResultTick pair. A
        // scalar could only ever carry the single most recent rejection -
        // a client that missed exactly one broadcast (e.g. across a
        // reconnect gap) while two or more commands were rejected back to
        // back would only ever see the last one, silently and permanently
        // losing the earlier rejection's feedback. ResultTick is a
        // per-player monotonically increasing counter (never reset), so
        // VisualSyncProxy.ApplyCommandResultState can always tell which
        // slots are newer than what it has already displayed and in what
        // order to apply them.
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

        // Modul: mirrors server StateUpdatePacket exactly - Town Hall gates
        // every other building's max level and boosts passive gold; the
        // Crafting Workshop boosts crafting rarity odds. Both existed
        // server-side with real upgrade logic but had no client-visible
        // level at all until this fix. 689 -> 691 bytes.
        public byte TownHallLevel;
        public byte CraftingWorkshopLevel;

        // Modul: Play Mode audit fix. LegacyPerks bitmask (3 prestige
        // perks packed at byte offsets 0/8/16 - see LegacyPerkResolver on
        // the server) mirrored onto the wire so the Legacy Shop can show
        // current rank / next-rank cost. 691 -> 699 bytes.
        public long LegacyPerksBitmask;

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

        // Modul: Production Release Hardening, Part 2. ClaimedMilestonesBitmask,
        // ActiveChroniclePassLevel, AccumulatedSeasonalXp,
        // ClaimedAchievementFlags, TotalAchievementsClaimedCount, and
        // EventHorizonTransactionCount were removed from this hot-path
        // packet and now live behind PlayerMetadataCache/
        // AchievementsStateCache (see /api/v1/player/metadata,
        // /api/v1/achievements/state). GuildWarExpansionPadding0/1/2 were
        // also removed outright: dead reserved filler, never read.
    }
}

using System.Runtime.InteropServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    public enum CommandType : byte
    {
        None = 0,
        ChangeActivity = 1,
        ExecuteForgeFusion = 2,
        PlaceLimitOrder = 3,
        ReloadState = 4,
        ContributeToGuild = 5,
        Logout = 6,
        Login = 7,
        ToggleChronoAcceleration = 8,
        MarketListItem = 9,
        MarketBuyItem = 10,
        ClaimMailItem = 11,
        DepositToBank = 12,
        WithdrawFromBank = 13,
        RerollItemAffix = 14,
        ExecuteBreeding = 15,
        UpdateAutoEatThreshold = 16,
        InitializeCrafting = 18,
        RegisterWorldBossDamage = 19,
        // 20 intentionally unused - was LegacyUpgradeBuilding, a
        // buildingType 1-4-only wrapper that the command dispatch loop never
        // actually routed to (UpgradeBuilding = 29, below, is the real one,
        // handling all building ids via VillageManagementEngine directly).
        // Gap left rather than renumbered, matching this enum's existing
        // convention (see 36/37).
        UpgradeTool = 21,
        AssignMentor = 22,
        ContributeToWarSupply = 23,
        ConsumeChronoCore = 24,
        PurchaseLegacyUnlocks = 25,
        DepositGuildMaterial = 26,
        ExecuteCombatTurn = 27,
        EstablishMentorship = 28,
        UpgradeBuilding = 29,
        EvictVillager = 30,
        AntiCheatChallengeResponse = 31,
        AttackWorldBoss = 32,
        RegisterPushToken = 33,
        TriggerGdprPurge = 34,
        SwitchLanguage = 35,
        SubmitPurchaseReceipt = 39,
        SyncBillingStatus = 40,
        ReportUiContextSwitch = 41,
        CraftItem = 42,
        ClaimAchievementReward = 43,
        InitiateNodeMigration = 44,
        ConsumeConsumableAsset = 45,
        ClaimBattlePassReward = 46,
        ActivateChronoBoost = 47,
        ConsumeTimeWarpCore = 48,
        RegisterGuildDefense = 49,
        SubmitShardAttack = 50,
        ReportTelemetryBurst = 51,
        PingNetworkDiagnostics = 52,
        LaunchGuildRaid = 53,
        EquipItem = 54,
        UnequipItem = 55,
        TerminateMentorship = 56,
        RequestUnlockSkill = 57,
        RequestCastSkill = 58,

        // Modul: Comprehensive Game System Audit, Part 4.3. Unlocks the
        // Chronicle Pass premium track by spending PremiumDiamonds
        // server-side (see ChroniclePassEconomy.PremiumPassPriceDiamonds) -
        // no direct cash IAP hook involved.
        PurchaseBattlePass = 59,

        // Modul: Full-Stack Social Layer, Part 2. Relationship matrix
        // commands - all four resolve their target via the existing
        // TargetPlayerId field (never a new wire field) and run through
        // RelationshipEngine inside an isolation-guaranteed Serializable
        // transaction.
        AddFriend = 60,
        RemoveFriend = 61,
        BlockPlayer = 62,
        UnblockPlayer = 63,

        // Modul: Play Mode audit fix. This used to share CommandType.
        // ContributeToGuild (5) with the materials/Monolith contribution
        // branch below in an if/else chain - the materials branch always
        // won, so gold/equipment guild-treasury contribution (which drives
        // real GuildRecords.CurrentTier progression and each member's
        // GuildMembers.ContributionPoints roster ranking - see
        // GuildContributionEngine) was permanently unreachable dead code.
        // Split into its own command type.
        ContributeGuildTreasury = 64,

        // Modul: inheritance stats. Buys one level of a permanent, season-
        // crossing bonus with diamonds. TargetSkillId carries the stat id -
        // reused rather than adding a wire field, exactly as the skill commands
        // do, because it is already a small bounded identifier on this packet
        // and a seventh id field would be a seventh thing to validate.
        // 66, not 65: StockFoodSlot already holds 65. A collision here is not
        // a compile error - the enum happily takes two names for one value, and
        // the server would then have read every larder stocking as a diamond
        // purchase. It surfaced only because the protocol generator emits one
        // entry per NAME and produced a duplicate key in the client's opcode
        // map. Check this list before adding to it.
        PurchaseInheritanceLevel = 66,

        // Modul: skill tree. Spends skill points - earned one per account
        // level - on one of five passive branches. TargetSkillId carries the
        // branch id, for the same reason the line above reuses it.
        //
        // 67 because 66 is taken and 65 before it; the comment above explains
        // what happens when that is not checked. Every id in this enum is used
        // exactly once as of this writing: 1..67 with no gaps worth reusing.
        PurchaseSkillTreeLevel = 67,

        // Modul: respec. Refunds every point in the tree and clears every
        // node, so the exclusive forks of ring 2 can be chosen again.
        //
        // Carries NO new wire fields - there is nothing to say beyond "undo
        // it all". A partial respec was considered and dropped: refunding one
        // fork while leaving its crown standing would need a rule for what
        // happens to a crown whose prerequisite just vanished, and "all of
        // it" has no such corner.
        RespecSkillTree = 68,

        // Modul: larder. Loads food from the backpack into one of the three
        // auto-eat slots, or unloads a slot back into the backpack. Carries no
        // new wire fields: ConsumableItemId is the food, TargetSlotIndex the
        // 0-based larder slot, DepositQuantity how many units to move (0 =
        // unload). The packet size is unchanged.
        //
        // Nothing anywhere could put food in those slots before this command
        // existed, so every player's larder was permanently empty and any
        // combat activity stopped the first time HP crossed the auto-eat
        // threshold. See LarderEngine.
        StockFoodSlot = 65,

        // Modul: hero x villager pairing - THE standard pair (LONG_GAME_SPEC
        // part 3). TargetGuid is the hero, TargetId the village_newcomers row.
        //
        // A SEPARATE COMMAND rather than ExecuteBreeding with one Guid left
        // empty. The two pairings have different parents (a character and a
        // newcomer are different tables with different ids), different gates
        // (only the hero needs level 50) and different outcomes (the villager
        // becomes an elder, forever). ValidateBreedingRequest also rejects a
        // non-zero TargetId outright, so overloading it would mean loosening
        // the check that stops every other field being smuggled through.
        //
        // 69 because 68 is RespecSkillTree - read this enum before adding to
        // it, see the note on PurchaseInheritanceLevel for what a collision
        // costs.
        ExecuteVillagerBreeding = 69
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ClientCommandPacket
    {
        public CommandType Command;
        public long TargetId;
        public long SecondaryId;
        public long TertiaryId;
        public int LimitPrice;
        public byte IsBuy;
        public int QualityTier;
        public System.Guid TargetGuid;
        public System.Guid SecondaryGuid;
        public long LogicEpochCounter;
        public uint TargetUnlockId;
        public uint RequestedSlotIndex;
        public uint MaterialId;
        public uint DepositQuantity;
        public uint MatchId;
        public uint ClientPredictedTurnCounter;
        public uint TargetPlayerId;
        public uint MentorshipRole;
        public uint TargetBuildingId;
        public uint TargetVillagerSlot;
        public uint ChallengeId;
        public uint ChallengeVerificationHash;
        public uint TargetedBossId;
        public uint ClientPredictedDamage;
        public fixed byte DeviceTokenBytes[64];
        public byte TargetPlatformFamily;
        public byte PushReserved0;
        public uint ConfirmationHash;
        public byte TargetLanguageId;
        public byte ComplianceReserved0;
        public byte ComplianceReserved1;
        public byte ComplianceReserved2;
        public uint ChronoSecondsRequested;
        public uint TargetSlotIndex;
        public fixed byte RawTransactionReceipt[64];
        public uint TargetProductIdHash;
        public uint ActiveUiContextBitmask;
        public uint TargetRecipeId;
        public uint CraftingSlotIndex;
        public uint TargetAchievementId;
        public uint MigrationToken;
        public uint ConsumableItemId;
        public uint ConsumableSlotTarget;
        public uint TargetMilestoneIndex;
        public uint ChronoWarpDurationSeconds;
        public uint ChronoTargetSlot;
        public double RequestedSpeedMultiplier;
        public System.Guid TargetMatchUuid;
        public uint TelemetryEventCount;
        public uint NetworkDiagnosticsToken;

        // Modul: reroll operations, 2026-08-01. NAMED fields rather than
        // smuggling these through LimitPrice, which is a market price field and
        // already carries an affix index for this same command - the pattern
        // logged as backlog item 29 and the shape of several past identity
        // bugs. Adding 7 bytes to a 352-byte packet is cheaper than another
        // overloaded field.
        //
        // RerollOperationKind: 0 = Value, 1 = StatType, 2 = UpgradeRarity.
        public byte RerollOperationKind;

        // 0 means "one reroll". Anything higher runs auto-reroll, clamped
        // server-side by AutoRerollPlanner.MaxAttemptsPerRequest - the client
        // number is a request, never a trusted bound.
        public uint RerollAutoMaxAttempts;

        // Auto-reroll stop condition. Rarity is a FLOOR (1-5); 1 means "any".
        public byte RerollStopMinRarity;

        // 1-based index into AffixRegistry.Definitions; 0 means "any stat".
        // An index rather than a string because the packet is fixed-layout, and
        // the registry order is the same authority on both sides.
        public byte RerollStopAffixIndex;
    }
}

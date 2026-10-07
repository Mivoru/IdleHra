using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// Everything the quest line's predicates read, loaded once per request by
    /// QuestLineEngine.LoadFactsAsync. A plain record so a test can build any
    /// player it likes without a database.
    /// </summary>
    /// <remarks>
    /// Modul: EVERY FIELD IS A DURABLE FACT, never client state. Where the
    /// "has this ever happened" evidence is a counter or a ledger row it is
    /// named for what it IS (FusionsCompleted, GoldSpentOnReroll); the acts with
    /// no evidence of their own are answered by the engine's latch, not here.
    /// </remarks>
    public sealed record QuestFacts
    {
        public int Level { get; init; }
        public long Gold { get; init; }
        public int Diamonds { get; init; }
        public int HighestUnlockedRegion { get; init; } = 1;
        /// <summary>The market's trade licence: listing and buying both need a guild.</summary>
        public bool InGuild { get; init; }

        public int ForgeLevel { get; init; }
        public int BreedingGroundsLevel { get; init; }
        /// <summary>Any building at level 1 or above, or an upgrade under way.</summary>
        public bool AnyVillageBuildingStarted { get; init; }

        public long FusionsCompleted { get; init; }
        public long GoldSpentOnFusion { get; init; }
        public long RerollsPerformed { get; init; }
        public long GoldSpentOnReroll { get; init; }
        public long GoldSpentOnVillage { get; init; }
        public long GoldSpentOnBreeding { get; init; }
        public long GoldSpentOnDelve { get; init; }

        public int InheritanceLevelsBought { get; init; }
        public int DelveDeepestFloor { get; init; }
        /// <summary>A sell listing is open, or one of the player's listings has sold.</summary>
        public bool HasOpenOrSoldListing { get; init; }
        /// <summary>The player has a row on the CURRENT world boss's board.</summary>
        public bool HasWorldBossAttemptRow { get; init; }
        public int HighestAscensionStep { get; init; }
        public int RebirthCount { get; init; }
        public long GoldFromChestSales { get; init; }
        public bool AutoSellRuleSet { get; init; }
    }

    public readonly record struct QuestReward(int Region, long Gold, string MaterialLog, string MaterialOre, int MaterialQuantity);

    /// <summary>
    /// One step of the quest line. <see cref="Unlocked"/> and <see cref="Done"/>
    /// are pure predicates over <see cref="QuestFacts"/>; the engine ORs Done
    /// with the durable latch.
    /// </summary>
    public sealed record QuestStep(
        string Id,
        int Order,
        string Title,
        string Explanation,
        string Screen,
        string[] GuideTargets,
        string UnlockHint,
        Func<QuestFacts, bool> Unlocked,
        Func<QuestFacts, bool> Done);

    /// <summary>
    /// The owner's quest line (2026-10-07): ten acts a player is MADE to try
    /// once, because a friend played for weeks without learning that fusion or
    /// affix reroll existed. Static definitions only; QuestLineEngine decides
    /// state and pays.
    /// </summary>
    /// <remarks>
    /// Modul: THE ORDER IS THE OWNER'S, and so is the rule that a step is done
    /// only when the act HAPPENED. A step never completes because a screen was
    /// opened or a button was merely visible - which is the difference between
    /// this and the tier-three objectives (tutorialObjectives.ts), which are
    /// acknowledged by being read. The client mirrors NOTHING from here: titles,
    /// explanations, screens and highlight targets all arrive in the quest
    /// view, so a retune is one edit on the server.
    ///
    /// Modul: EACH UNLOCK IS THE LEVEL OR BUILDING THAT MAKES THE ACT POSSIBLE,
    /// mirroring what the menu already gates (client ui/unlocks.ts): the Forge
    /// opens at level 5, the Market at 10, the Delve with its first entry fee in
    /// hand. Rebirth is gated at RebirthRules.RenownLevel rather than offered
    /// at any level - a rebirth is allowed at level 2 but resets the whole life,
    /// so the one step that wipes progress must not be pointed at a newcomer.
    /// A step the player has ALREADY done is never locked, whatever its unlock
    /// says: a veteran who fused a year ago sees it ticked, not greyed.
    ///
    /// Modul: GuideTargets ARE `data-guide` VALUES in client_web, most specific
    /// first - the same convention as guided.ts. They are the server's words
    /// for "which control", so adding one means putting the attribute on the
    /// button; the client degrades to the explanation alone when none is on the
    /// page (an empty chest has no Fuse button to light).
    /// </remarks>
    public static class QuestLineRegistry
    {
        // Modul: 2026-10-07 evening, owner: a friend never found out the chest
        // can be cleared in one go or that drops can sell themselves. Both are
        // the first chores a player meets, so they go first.
        public const string ClearChest = "clear_chest";
        public const string AutoSell = "auto_sell";
        public const string Fuse = "fuse";
        public const string Reroll = "reroll";
        public const string Village = "village";
        public const string Market = "market";
        public const string Breed = "breed";
        public const string Inheritance = "inheritance";
        public const string Delve = "delve";
        public const string WorldBoss = "world_boss";
        public const string Ascension = "ascension";
        public const string Rebirth = "rebirth";

        public const int ForgeOpenLevel = 5;
        public const int MarketOpenLevel = 10;
        public const int WorldBossOpenLevel = 10;

        private static bool ForgeOpen(QuestFacts f) => f.Level >= ForgeOpenLevel || f.ForgeLevel >= 1;

        /// <summary>The first thing Inheritance sells - its cheapest level, in diamonds.</summary>
        public static long CheapestInheritanceLevel => InheritanceRegistry.GetUpgradeCost(0);

        public static readonly IReadOnlyList<QuestStep> Steps = new QuestStep[]
        {
            new(ClearChest, 1, "Clear out the chest",
                "Drops pile up in the Village Chest. Open \"Clear out\" there to sell everything up to a rarity in one go - up to Epic is safe; Legendary and better is never swept.",
                "chest", new[] { "chest-sweep-sell", "chest-sweep" }, $"Reach level {ForgeOpenLevel}",
                ForgeOpen,
                f => f.GoldFromChestSales > 0),

            new(AutoSell, 2, "Set an auto-sell rule",
                "Under \"Auto-sell rules\" in the Chest, pick a rarity and drops at or below it sell the moment they land, per region if you like. No more clearing by hand.",
                "chest", new[] { "chest-rules" }, $"Reach level {ForgeOpenLevel}",
                ForgeOpen,
                f => f.AutoSellRuleSet),

            new(Fuse, 3, "Fuse an item",
                "The Forge fuses spare pieces of one rarity into one of the next rarity up. Fusing is how gear climbs past what drops.",
                "forge", new[] { "forge-fuse" }, $"Reach level {ForgeOpenLevel}",
                ForgeOpen,
                f => f.FusionsCompleted > 0 || f.GoldSpentOnFusion > 0),

            new(Reroll, 4, "Reroll an affix",
                "The Forge can reroll one affix on a piece you own for gold. The slot stays; what is in it changes.",
                "forge", new[] { "forge-reroll", "forge-reroll-item" }, $"Reach level {ForgeOpenLevel}",
                ForgeOpen,
                f => f.RerollsPerformed > 0 || f.GoldSpentOnReroll > 0),

            new(Village, 5, "Upgrade a village building",
                "Buildings produce materials and unlock whole systems while you are away. Start any upgrade; it finishes on its own.",
                "village", new[] { "village-upgrade" }, $"Reach level {ForgeOpenLevel}",
                ForgeOpen,
                f => f.AnyVillageBuildingStarted || f.GoldSpentOnVillage > 0),

            new(Market, 6, "List an item on the market",
                "The market is player to player. List a piece you will not wear instead of salvaging it - the mailbox pays you when it sells. Trading needs a guild.",
                "market", new[] { "market-list" }, $"Reach level {MarketOpenLevel} and join a guild",
                f => f.Level >= MarketOpenLevel && f.InGuild,
                f => f.HasOpenOrSoldListing),

            new(Breed, 7, "Start a breeding",
                "Two villagers can have a child who inherits from both. It costs gold, and the Breeding Grounds must be built first.",
                "breeding", new[] { "breeding-start" }, "Build the Breeding Grounds in the Village",
                f => f.BreedingGroundsLevel >= 1,
                f => f.GoldSpentOnBreeding > 0),

            new(Inheritance, 8, "Buy an inheritance level",
                "Inheritance is permanent: diamonds buy a bonus that survives every rebirth. Diamonds come from the Delve.",
                "inheritance", new[] { "inheritance-buy" }, "Hold enough diamonds for the cheapest level",
                f => f.Diamonds >= CheapestInheritanceLevel,
                f => f.InheritanceLevelsBought > 0),

            new(Delve, 9, "Enter the Delve",
                "The Delve turns gold into diamonds, if your nerve holds. Pay the entry fee, then pick a door on each floor.",
                "delve", new[] { "delve-start" }, "Hold one entry fee in gold",
                f => f.Gold >= DelveRegistry.EntryFeeForRegion(f.HighestUnlockedRegion),
                f => f.GoldSpentOnDelve > 0 || f.DelveDeepestFloor > 0),

            new(WorldBoss, 10, "Strike the world boss",
                "One strike a day on the shared boss. Everybody's damage is added up, and the board pays out weekly by damage.",
                "worldboss", new[] { "worldboss-strike" }, $"Reach level {WorldBossOpenLevel}",
                f => f.Level >= WorldBossOpenLevel,
                f => f.HasWorldBossAttemptRow),

            new(Ascension, 11, "Clear a boss Ascension step",
                "Every region boss can be fought again, stronger, at ten steps. Each first clear pays a title. Cosmetics only.",
                "combat", new[] { "ascension-start", "ascension" }, "Defeat the first region's boss",
                f => f.HighestUnlockedRegion >= 2,
                f => f.HighestAscensionStep >= 1),

            new(Rebirth, 12, "Take a Rebirth",
                "A rebirth starts a new life at level 1 but keeps what is permanent. One taken from level 50 or above also adds Renown.",
                "ancestors", new[] { "rebirth-start", "rebirth" }, $"Reach level {RebirthRules.RenownLevel}",
                f => f.Level >= RebirthRules.RenownLevel,
                f => f.RebirthCount > 0),
        };

        public static QuestStep? Find(string? id)
            => id == null ? null : Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

        /// <summary>
        /// What ONE step pays for a player whose highest unlocked region is
        /// <paramref name="region"/>.
        /// </summary>
        /// <remarks>
        /// Modul: DERIVED FROM EXISTING PER-REGION CONSTANTS, NOT A NEW TABLE.
        ///
        /// GOLD is a quarter of the region's Delve entry fee
        /// (DelveRegistry.EntryFeeByRegion: 7,000 / 17,000 / 52,500 / 150,000 /
        /// 250,000). That table is already written as "minutes of the region's
        /// own income" and asserted against measured income, so a reward that is
        /// a fixed fraction of it scales with the economy and moves when the
        /// economy is retuned - rather than being a second set of numbers that
        /// silently stops meaning "a few minutes" the next time income changes.
        /// A quarter is a few minutes of play and well under one fusion's fee,
        /// so the reward thanks a player for trying a feature without paying for
        /// the next try.
        ///
        /// MATERIALS are the region's own log and ore - the pair the village's
        /// upgrade ladder asks for at that tier (VillageManagementEngine
        /// .GetTierMaterials) - a quarter of that tier's first upgrade price
        /// each. A handful that makes the next village step cheaper, in the
        /// materials the player is actually short of in that region.
        ///
        /// The reward is the same for every step on purpose: the steps differ in
        /// how much WORK they ask, not in how much they are worth, and a ladder
        /// of growing rewards would make skipping the early ones look free.
        /// </remarks>
        public static QuestReward RewardFor(int region)
        {
            int r = Math.Clamp(region, 1, 5);
            int tierLevel = (r - 1) * 5;
            var mats = VillageManagementEngine.GetTierMaterials(tierLevel);
            long firstUpgrade = VillageManagementEngine.CalculateProductionUpgradeCost(tierLevel);
            int qty = (int)Math.Max(1L, firstUpgrade / 4L);
            long gold = Math.Max(1L, DelveRegistry.EntryFeeForRegion(r) / 4L);
            return new QuestReward(r, gold, mats.Log, mats.Ore, qty);
        }
    }
}

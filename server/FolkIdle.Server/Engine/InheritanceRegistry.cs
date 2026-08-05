using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The inheritance stats: what diamonds buy, and what survives a season.
    ///
    /// Deliberately SMALL and deliberately BROAD. Six entries, each moving one
    /// number a player already watches, because the point of a permanent bonus
    /// is that the next season starts faster - not that it starts different. A
    /// long list of narrow modifiers would make the choice a research project
    /// and the reset a spreadsheet.
    ///
    /// Every stat is a percentage, every level adds the same percentage, and
    /// the cost per level climbs. So the decision a player makes is how WIDE to
    /// go against how DEEP, which is a real choice at every diamond balance.
    ///
    /// A single authority: the cost curve, the cap and the per-level value all
    /// live here, and the client mirrors this table rather than restating it.
    /// </summary>
    public static class InheritanceRegistry
    {
        public const int StatDamage = 0;
        public const int StatMaxHp = 1;
        public const int StatXpGain = 2;
        public const int StatGoldGain = 3;
        public const int StatGatheringYield = 4;
        public const int StatLootLuck = 5;

        public const int StatCount = 6;

        /// <summary>
        /// The ceiling per stat. Twenty levels at 2% is +40% - meaningful after
        /// several seasons and nowhere near enough to trivialise a region on
        /// its own, which is the shape a permanent bonus has to have when it
        /// can only ever grow.
        /// </summary>
        public const int MaxLevel = 20;

        /// <summary>Percentage points added per level, for every stat.</summary>
        public const int PercentPerLevel = 2;

        // 40 diamonds for the first level, x1.28 each time. Level 20 costs
        // about 6,000 and a full stat runs to roughly 25,000 - years of daily
        // logins, or a purchase. Both are intended: this is the sink that gives
        // the premium currency a reason to exist.
        private const double CostBase = 40.0;
        private const double CostGrowth = 1.28;

        public static string GetName(int statId) => statId switch
        {
            StatDamage => "Damage",
            StatMaxHp => "Max health",
            StatXpGain => "Experience",
            StatGoldGain => "Gold",
            StatGatheringYield => "Gathering yield",
            StatLootLuck => "Loot luck",
            _ => "Unknown",
        };

        public static bool IsValidStat(int statId) => statId >= 0 && statId < StatCount;

        /// <summary>
        /// Diamonds to buy the NEXT level, given how many are already owned.
        /// Zero when the stat is already capped, which callers must treat as
        /// "refuse" rather than "free".
        /// </summary>
        public static long GetUpgradeCost(int currentLevel)
        {
            if (currentLevel < 0) currentLevel = 0;
            if (currentLevel >= MaxLevel) return 0L;

            double cost = CostBase * Math.Pow(CostGrowth, currentLevel);
            return (long)Math.Floor(cost);
        }

        /// <summary>The percentage this stat currently grants, in whole points.</summary>
        public static int GetBonusPct(int level)
        {
            if (level <= 0) return 0;
            if (level > MaxLevel) level = MaxLevel;
            return level * PercentPerLevel;
        }
    }
}

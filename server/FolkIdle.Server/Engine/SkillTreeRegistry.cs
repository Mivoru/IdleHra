using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The skill tree: what a level is worth, spent by the player.
    ///
    /// REPLACES THE FOUR ACTIVE SKILLS, which were removed for a measured
    /// reason rather than a taste one. They multiplied the next hit by 150 to
    /// 500 percent on cooldowns of three to twenty seconds, and mana refilled
    /// faster than the cooldowns cleared - so at a 1.5 second swing, 390 of
    /// every 400 swings could be buffed. That is +90% damage, +136% with the
    /// status synergy, available only to a player willing to click every three
    /// seconds. In an idle game that is two different games sharing a balance
    /// sheet, and the pacing model knew about neither.
    ///
    /// The points survive because they were the good part: one per account
    /// level, a steady small decision. What they buy is now passive.
    ///
    /// ACCOUNT LEVEL, NOT CHARACTER LEVEL. The characters table has a Level
    /// column that nothing in the server ever writes during play; the level
    /// that moves is the account's. More to the point, characters in this game
    /// are semi-disposable - they age through phases and are bred and replaced -
    /// so hanging permanent progression on one would be a trap the first time a
    /// player's investment aged out.
    ///
    /// FIVE BRANCHES, TWENTY LEVELS, RISING COST. A season pays about a hundred
    /// points and all five branches cost 250, so the choice is which two to
    /// take deep, and it is a real one every season.
    /// </summary>
    public static class SkillTreeRegistry
    {
        public const int BranchLootRarity = 0;
        public const int BranchWorldBossDamage = 1;
        public const int BranchCritChance = 2;
        public const int BranchCritDamage = 3;
        public const int BranchXpGain = 4;

        public const int BranchCount = 5;
        public const int MaxLevel = 20;

        /// <summary>
        /// Tenths of a percent per level, so the small branches can be smaller
        /// than one percent a step without the table growing decimals.
        ///
        /// The sizes are not uniform, and that is the whole design: each branch
        /// is scaled to what it actually moves.
        ///
        ///   LOOT RARITY +1.0% a level, +20% at cap. Loot luck shifts the
        ///   per-item WEIGHT of a drop rather than the number of rolls, so this
        ///   changes what falls, never how much.
        ///
        ///   WORLD BOSS +2.0% a level, +40% at cap - the most generous, because
        ///   the world boss is its own activity on its own timer. It cannot
        ///   touch a region's pacing no matter how large it gets.
        ///
        ///   CRIT CHANCE +0.4 points a level, +8 at cap. DEX gives about 10% to
        ///   a levelled character, so this is not quite a doubling.
        ///
        ///   CRIT DAMAGE +3.0% a level, +60% at cap, taking the multiplier from
        ///   1.5x to 2.1x.
        ///
        ///   XP GAIN +0.4% a level, +8% at cap. The smallest on purpose: it is
        ///   the only branch that shortens the season directly, so it is the
        ///   one where a large number would quietly undo the curve.
        ///
        /// Both crit branches maxed - a hundred points, a whole season - is
        /// about +14% damage over time. Tens of percent for a season's
        /// investment is the brief; the four active skills were +90% for
        /// clicking, which is what this exists instead of.
        /// </summary>
        private static readonly int[] TenthsOfPercentPerLevel =
        {
            10,   // Loot rarity
            20,   // World boss damage
            4,    // Crit chance (percentage POINTS, not a multiplier)
            30,   // Crit damage
            4,    // XP gain
        };

        public static string GetName(int branchId) => branchId switch
        {
            BranchLootRarity => "Fortune",
            BranchWorldBossDamage => "Giantslayer",
            BranchCritChance => "Precision",
            BranchCritDamage => "Cruelty",
            BranchXpGain => "Insight",
            _ => "Unknown",
        };

        public static string GetBlurb(int branchId) => branchId switch
        {
            BranchLootRarity => "Better rarity on what drops. Not more loot - better loot.",
            BranchWorldBossDamage => "Every blow against a world boss lands harder.",
            BranchCritChance => "More of your hits are critical ones.",
            BranchCritDamage => "Your critical hits take a larger bite.",
            BranchXpGain => "Levels arrive sooner, which is the slowest part of a season.",
            _ => "Unknown",
        };

        public static bool IsValidBranch(int branchId) => branchId >= 0 && branchId < BranchCount;

        /// <summary>The branch's total bonus at this level, in tenths of a percent.</summary>
        public static int GetBonusTenthsOfPercent(int branchId, int level)
        {
            if (!IsValidBranch(branchId) || level <= 0) return 0;
            if (level > MaxLevel) level = MaxLevel;
            return TenthsOfPercentPerLevel[branchId] * level;
        }

        /// <summary>The same, as a percentage, for callers doing float maths.</summary>
        public static float GetBonusPercent(int branchId, int level)
            => GetBonusTenthsOfPercent(branchId, level) / 10f;

        /// <summary>
        /// What the NEXT level costs, in skill points. Five levels at each
        /// price, so a branch runs 1-1-1-1-1-2-2-2-2-2-3... and a full branch
        /// is fifty points.
        ///
        /// Rising rather than flat because a flat price makes the tree a
        /// formality: a hundred points would buy a hundred levels wherever the
        /// player liked, and the last one would be as cheap as the first.
        /// </summary>
        public static int GetUpgradeCost(int currentLevel)
        {
            if (currentLevel < 0 || currentLevel >= MaxLevel) return 0;
            return (currentLevel / 5) + 1;
        }

        /// <summary>Total points to take one branch from nothing to the cap.</summary>
        public static int TotalCostForFullBranch()
        {
            int total = 0;
            for (int level = 0; level < MaxLevel; level++) total += GetUpgradeCost(level);
            return total;
        }
    }
}

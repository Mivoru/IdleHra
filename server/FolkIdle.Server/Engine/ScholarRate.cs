using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Scholar, the Insight crown: +25% to the RATE a character earns at,
    /// online and away alike.
    /// </summary>
    /// <remarks>
    /// Modul: ONE RULE ON BOTH PATHS, owner decision 2026-09-30. Scholar used
    /// to be offline-only - "everything earned while away comes in a quarter
    /// faster" - which it did by inflating the offline window's seconds. That
    /// broke the rule that an hour away pays what an hour watched pays, and it
    /// also made a Scholar eat a quarter more food and fight a quarter longer
    /// than it had actually been gone.
    ///
    /// It is a reward multiplier now, applied to the same streams on both
    /// paths, never to time:
    ///
    ///   each kill, harvest and craft completion pays for 1 + Binomial(1, b)
    ///   completions (RewardUnits), offline drawing Binomial(n, b) for the
    ///   window's n - the same expectation, drawn once;
    ///
    ///   village production and Town Hall gold accrue at (1000 + b permille)
    ///   of their hourly rate (BonusPermille), exactly, on both paths.
    ///
    /// BOUNDED: a crown has one level (SkillTreeRegistry.CrownMaxLevel), so the
    /// multiplier is at most 1.25x. PowerCeilingTests lists it in the yield
    /// ledger.
    /// </remarks>
    public static class ScholarRate
    {
        /// <summary>The bonus in permille (250 = +25%), 0 without the crown.</summary>
        public static int BonusPermille(byte skillScholar)
            => skillScholar > 0
                ? SkillTreeRegistry.GetBonusTenthsOfPercent(SkillTreeRegistry.CrownScholar, skillScholar)
                : 0;

        /// <summary>The largest multiplier the crown can reach - for the power ledger.</summary>
        public static double MaxMultiplier
            => 1.0 + SkillTreeRegistry.GetBonusTenthsOfPercent(SkillTreeRegistry.CrownScholar, SkillTreeRegistry.CrownMaxLevel) / 1000.0;

        /// <summary>
        /// How many completions' worth of reward <paramref name="units"/>
        /// completions pay: each one pays one more with probability b.
        /// </summary>
        public static long RewardUnits(Random rng, long units, byte skillScholar)
        {
            int permille = BonusPermille(skillScholar);
            if (permille <= 0 || units <= 0) return units;
            if (units == 1) return rng.NextDouble() * 1000.0 < permille ? 2 : 1;

            int n = (int)Math.Min(units, int.MaxValue);
            return units + OfflineSimulationEngine.SampleBinomial(rng, n, permille / 1000.0);
        }
    }
}

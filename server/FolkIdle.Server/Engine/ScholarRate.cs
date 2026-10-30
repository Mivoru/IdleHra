using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The reward rate: up to +25% to the RATE a character earns at, online and
    /// away alike. Once the Insight crown Scholar; since 2026-10-30 the Hearth
    /// half's Homebody and Long Absence (HearthTreeRegistry).
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
    /// BOUNDED: Homebody and Long Absence at their maximum sum to 250 permille,
    /// the old crown's +25% exactly, so the multiplier is at most 1.25x.
    /// PowerCeilingTests lists it in the yield ledger.
    ///
    /// Modul: THE CROWN'S SLOT WAS EMPTY FOR A WEEK (2026-10-30). The War switch
    /// refunded every old node, so payload.Skill_Scholar (slot 19) read 0 for
    /// everyone and every caller here paid nothing extra. Callers now pass the
    /// permille BonusPermille reads off the Hearth ranks.
    /// </remarks>
    public static class ScholarRate
    {
        /// <summary>The bonus in permille (250 = +25%) the payload's Hearth ranks give.</summary>
        public static int BonusPermille(in TickStatePayload payload)
            => HearthTreeRegistry.Resolve(in payload).RewardRatePermille;

        /// <summary>The largest bonus the Hearth nodes can reach, in permille.</summary>
        public static int MaxPermille
        {
            get
            {
                int total = 0;
                foreach (var n in HearthTreeRegistry.All())
                    if (n.Effect == HearthEffect.OfflineRatePct) total += n.ValueAt(n.MaxLevel);
                return total;
            }
        }

        /// <summary>The largest multiplier the reward rate can reach - for the power ledger.</summary>
        public static double MaxMultiplier => 1.0 + MaxPermille / 1000.0;

        /// <summary>
        /// How many completions' worth of reward <paramref name="units"/>
        /// completions pay: each one pays one more with probability b.
        /// </summary>
        public static long RewardUnits(Random rng, long units, int permille)
        {
            if (permille <= 0 || units <= 0) return units;
            if (units == 1) return rng.NextDouble() * 1000.0 < permille ? 2 : 1;

            int n = (int)Math.Min(units, int.MaxValue);
            return units + OfflineSimulationEngine.SampleBinomial(rng, n, permille / 1000.0);
        }
    }
}

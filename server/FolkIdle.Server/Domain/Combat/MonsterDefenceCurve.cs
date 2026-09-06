using System;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// What a monster's armour and dodge are worth, derived from where it sits
    /// in its region rather than hand-authored per monster.
    ///
    /// Modul: THE CANON SHIPPED WITH PLACEHOLDER DEFENCES, 2026-09-06.
    ///
    /// Reported as "I deal 73,500 damage to all monsters, I thought it should
    /// scale". It should, and it could not:
    ///
    ///   * Armour was `10 * regionTier` on all twenty-five canonical monsters,
    ///     and CombatDamageModel.MonsterArmourHalvingConstant is `30 *
    ///     regionTier`. The tier is on BOTH sides of `A / (K + A)`, so it
    ///     cancels: every monster in the game, Field Mouse to Malakor, mitigated
    ///     exactly 25.0%. The stat was authored, validated at start-up and read
    ///     on every swing, and was mathematically incapable of telling one
    ///     monster from another.
    ///   * DodgeRating was ZERO on all twenty-five. The sixty-eight legacy
    ///     monsters they replaced had varied values; the canon that shipped over
    ///     them did not. `HitChance` was therefore pinned at its 0.95 ceiling
    ///     for everybody, forever, and DEX bought nothing.
    ///
    /// So the four regulars of a region differed from each other, and from the
    /// boss, in HP and attack power and in nothing else.
    ///
    /// DERIVED, NOT AUTHORED. ContentRegistry.Initialize overwrites the
    /// canonical monsters' Armor and DodgeRating with these values, so there is
    /// one source of truth and the flat-content defect cannot return one careless
    /// merge later. Legacy monsters (ids 1-90) keep their own JSON values.
    ///
    /// THE IDENTITY, in one line: the deeper regulars are hard to HIT and the
    /// boss is hard to HURT. Giving the boss both read worse and measured worse -
    /// it carries five times the HP already, so stacking evasion on top made the
    /// fight eleven times a regular's. Armour alone keeps it at about 5.3x, which
    /// is a boss.
    ///
    /// Measured, seconds to kill on ARRIVAL gear (the attention-span test prints
    /// this table):
    ///
    ///   region 1    52   82  117  169   boss 894
    ///   region 5    77   96  120  152   boss 810
    ///
    /// A player's damage against the deepest regular of a location is now about
    /// a third of what it is against the first one. It used to be identical.
    /// </summary>
    public static class MonsterDefenceCurve
    {
        /// <summary>
        /// Fraction of THIS REGION's armour-halving constant that each rank
        /// carries. Rank 0 is the region's weakest regular, rank 3 its
        /// strongest, rank 4 the boss.
        ///
        /// Mitigation is `A / (K + A)`, so these fractions produce, in order:
        /// 3%, 10%, 14%, 17%, 29%.
        ///
        /// THE SPREAD RUNS DOWNWARD FROM THE OLD FLAT 25%, deliberately, and
        /// that is forced rather than chosen. Test_Content_EveryMonsterDies
        /// InsideTheAttentionSpan measures the STRONGEST regular of a region
        /// against a character in ARRIVAL gear - the weakest player who will
        /// ever meet it - and caps that fight at 180 seconds. Making the
        /// strongest regular tankier than it already was blows straight through
        /// that: the first attempt put region 1's fourth monster at 246s.
        ///
        /// So the easy monsters got easier instead. The player still sees their
        /// damage fall as they walk deeper into a location, which is the point,
        /// and nothing in the region became a wall for the person who just
        /// arrived.
        /// </summary>
        private static readonly double[] ArmourFractionOfHalvingConstant =
        {
            0.033, 0.107, 0.156, 0.200, 0.414,
        };

        /// <summary>
        /// The chance a region-appropriate player's swing CONNECTS with each
        /// rank. Read forwards: the weakest regular is a training dummy, the
        /// strongest regular is genuinely evasive, and the boss is easy to hit
        /// and hard to hurt.
        /// </summary>
        private static readonly double[] TargetHitChance =
        {
            0.95, 0.93, 0.89, 0.84, 0.92,
        };

        /// <summary>
        /// What `AccuracyRating` a player who levelled through this region
        /// actually has, which is what dodge has to be priced against.
        ///
        /// StatsCalculator sets `AccuracyRating = dex`, RaceAttributeGrowth
        /// gives a Human +2 DEX a level, and ProgressionRateTests models a
        /// region as twenty levels starting at 1, 21, 41, 61, 81. So a Human
        /// arrives at region N with roughly `40 * (N - 1)` accuracy, and
        /// `HitChance` is `(100 + accuracy) / (100 + dodge)`.
        ///
        /// A race that grows DEX faster (Vila, +4 a level) lands more of its
        /// swings than a Human here, and one that grows it slower (Draugr, +1)
        /// lands fewer. That is the point: dodge is what finally makes accuracy
        /// a stat with a consequence, and it is what makes a player who walks
        /// into a region under-levelled feel it.
        /// </summary>
        public static int AccuracyBaselineFor(int regionTier)
        {
            int tier = regionTier < 1 ? 1 : regionTier;
            return 100 + 40 * (tier - 1);
        }

        /// <summary>Rank of a canonical monster within its region: 0-3 regulars, 4 boss.</summary>
        public static int RankWithinRegion(int monsterId, int firstCanonicalId, int monstersPerRegion)
        {
            if (monstersPerRegion <= 0) return 0;
            int offset = monsterId - firstCanonicalId;
            if (offset < 0) return 0;
            return offset % monstersPerRegion;
        }

        public static int ArmorFor(int regionTier, int rankWithinRegion, int armourHalvingConstant)
        {
            int rank = Math.Clamp(rankWithinRegion, 0, ArmourFractionOfHalvingConstant.Length - 1);
            int armour = (int)Math.Round(armourHalvingConstant * ArmourFractionOfHalvingConstant[rank]);
            // Never zero: an armour of 0 short-circuits Mitigate entirely and
            // would make the weakest regular of region 1 the only monster in the
            // game with no armour term at all.
            return armour < 1 ? 1 : armour;
        }

        public static int DodgeFor(int regionTier, int rankWithinRegion)
        {
            int rank = Math.Clamp(rankWithinRegion, 0, TargetHitChance.Length - 1);
            double numerator = AccuracyBaselineFor(regionTier);
            int dodge = (int)Math.Round(numerator / TargetHitChance[rank]) - 100;
            return dodge < 0 ? 0 : dodge;
        }
    }
}

using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>How well one spear landed. Ordered: a higher value is a better hit.</summary>
    public enum SpearClass
    {
        None = 0,
        Glance = 1,
        Plate = 2,
        Seam = 3,
    }

    /// <summary>What one strike is worth, and what it breaks. See <see cref="WorldBossStrikeRules.Price"/>.</summary>
    public readonly record struct StrikePrice(double Multiplier, double PlateMultiplier, double Auto, double Played, int? BreakPlate);

    /// <summary>One spear's outcome: which plate it struck, and how well.</summary>
    public readonly record struct SpearLanding(int Seq, int Plate, SpearClass Class, bool IsCounter);

    /// <summary>
    /// Every number in the shield wheel, and the pure functions that turn
    /// landings into a damage multiplier. Spec:
    /// docs/superpowers/specs/2026-09-24-world-boss-minigame-design.md, section 3.
    /// </summary>
    /// <remarks>
    /// Modul: THE SPEC OWNS THESE NUMBERS. The plan puts a phone playtest gate
    /// after practice ships precisely so they can be retuned - in the spec
    /// first, then here. WorldBossStrikeLedgerTests asserts every one of them,
    /// including the simulated means that justify the class values: under free
    /// aim every landing hits SOME plate, so the brief's Plate 0.6 / Glance 0.15
    /// paid a random tapper about 1.85x of a 2.0x cap (spec 3.1).
    /// </remarks>
    public static class WorldBossStrikeRules
    {
        public const int CountdownMs = 3_000;
        /// <summary>Play time after the countdown, including frozen interrupt time.</summary>
        /// <remarks>24 s since the 2026-09-25 playtest (was 20): the longer freezes below would otherwise have cut the spinning time.</remarks>
        public const int MaxPlayMs = 24_000;
        public const int Spears = 5;
        /// <summary>Tap to landing: the spear hits whatever part of the ring is at the impact point this much later.</summary>
        public const int FlightMs = 120;
        /// <summary>The client locks the throw zone this long after a throw.</summary>
        public const int MinReloadMs = 350;
        /// <summary>Display and touch latency, given as benefit of the doubt either side of a tap.</summary>
        public const int ToleranceMs = 35;
        /// <summary>The tolerance window is sampled at this step.</summary>
        public const int ToleranceStepMs = 5;

        public const int PlateCount = WorldBossEngine.PlateCount;
        public const double PlateDegrees = 360.0 / PlateCount;
        /// <summary>The Seam band, centred on the plate. 8, by the owner at the playtest gate (spec 3.1): 12 let a random tapper earn 1.53x.</summary>
        public const double SeamDegrees = 8.0;
        /// <summary>The Glance band on each side of every border between plates.</summary>
        public const double RivetDegrees = 3.0;

        // Class values (spec 3.1). Plate is 0 by the owner's decision at the playtest gate: only a seam
        public const double SeamValue = 1.0;
        // counts toward skill, and a Plate hit still breaks a plate and still counts for P.
        public const double PlateValue = 0.0;
        public const double GlanceValue = 0.0;
        public const double NoneValue = 0.0;

        /// <summary>The score is the mean of the best this-many spears, so one mistake is forgiven.</summary>
        public const int BestOf = 4;
        /// <summary>The score at which the multiplier reaches the cap.</summary>
        public const double SaturationScore = 0.90;
        public const double Floor = 1.0;
        public const double Cap = 2.0;
        public const double WeakPlateMultiplier = WorldBossEngine.WeakPlateDamageMultiplier;

        // The schedule (spec 3 and 3.4).
        public const int MinSpeedDegPerSec = 90;
        public const int MaxSpeedDegPerSec = 210;
        public const int MinSegmentMs = 700;
        public const int MaxSegmentMs = 2_200;
        public const int EnragedMinSpeedDegPerSec = 120;
        public const int EnragedMaxSpeedDegPerSec = 260;
        public const int EnragedMinSegmentMs = 500;
        public const int EnragedMaxSegmentMs = 1_600;
        /// <remarks>ResponseCloseMs + 1,400, so a late correct read still leaves the counter window it had before the playtest.</remarks>
        public const int InterruptMs = 3_400;
        public const int ReactionFloorMs = 150;
        /// <remarks>Was 1,000: on the phone the owner could not read the tell and press in time (spec 3.5).</remarks>
        public const int ResponseCloseMs = 2_000;
        public const int EnragedResponseCloseMs = 1_700;
        public const int FirstTellAtLeastMs = 3_000;
        public const int TellGapAtLeastMs = 5_000;
        public const int LastInterruptEndsByMs = MaxPlayMs - 2_000;
        /// <summary>A boss at or below this share of its health issues the enraged schedule (spec 3.4).</summary>
        public const double EnrageHpFraction = 0.25;

        /// <summary>
        /// The most one strike can multiply A x G by: Cap x WeakPlateMultiplier,
        /// which also bounds the auto-strike floor. Stated so the ledger can
        /// hold the Giantslayer-scaled total under it.
        /// </summary>
        public const double MaxPlayedMultiplier = Cap * WeakPlateMultiplier;

        /// <summary>
        /// The ledger's ceiling for one whole strike factor, G x played: the
        /// 20% Giantslayer cap times 6.0 is 7.2, so 8 leaves room without
        /// letting a new lever double it silently.
        /// </summary>
        public const double WorldBossMaxStrikeFactor = 8.0;

        /// <summary>
        /// Whether a challenge issued now gets the enraged schedule: at or
        /// below a quarter of the boss's health, decided once at issue and
        /// written into the schedule (spec 3.4). Exactly 25% is enraged.
        /// </summary>
        public static bool IsEnraged(long currentHp, long maxHp) =>
            maxHp > 0 && currentHp * 4 <= maxHp;

        public static double ValueOf(SpearClass spearClass) => spearClass switch
        {
            SpearClass.Seam => SeamValue,
            SpearClass.Plate => PlateValue,
            SpearClass.Glance => GlanceValue,
            _ => NoneValue,
        };

        /// <summary>The plate under a ring-local angle, 0-4.</summary>
        public static int PlateOf(double ringAngleDeg)
        {
            double a = Normalize(ringAngleDeg);
            int plate = (int)(a / PlateDegrees);
            return Math.Clamp(plate, 0, PlateCount - 1);
        }

        /// <summary>
        /// The class of a landing at <paramref name="offsetInPlateDeg"/>
        /// degrees into its plate: rivet bands [0, 3) and (69, 72] are a
        /// Glance, |offset - 36| &lt;= 6 is a Seam, and the rest is a Plate hit.
        /// </summary>
        public static SpearClass Classify(double offsetInPlateDeg)
        {
            if (offsetInPlateDeg < RivetDegrees || offsetInPlateDeg > PlateDegrees - RivetDegrees) return SpearClass.Glance;
            if (Math.Abs(offsetInPlateDeg - PlateDegrees / 2.0) <= SeamDegrees / 2.0) return SpearClass.Seam;
            return SpearClass.Plate;
        }

        public static double OffsetInPlate(double ringAngleDeg)
        {
            double a = Normalize(ringAngleDeg);
            return a - PlateOf(a) * PlateDegrees;
        }

        /// <summary>
        /// s: the mean class value of the best <see cref="BestOf"/> of the
        /// <see cref="Spears"/> spears. Spears not thrown or lost count as None.
        /// </summary>
        public static double Score(IReadOnlyList<SpearLanding> landings)
        {
            var values = new double[Spears];
            for (int i = 0; i < landings.Count && i < Spears; i++) values[i] = ValueOf(landings[i].Class);
            Array.Sort(values);
            Array.Reverse(values);
            double sum = 0;
            for (int i = 0; i < BestOf; i++) sum += values[i];
            return sum / BestOf;
        }

        /// <summary>M = min(Cap, Floor + (Cap - Floor) x s / SaturationScore), in [Floor, Cap].</summary>
        public static double Multiplier(double score)
        {
            if (double.IsNaN(score) || score <= 0) return Floor;
            return Math.Min(Cap, Floor + (Cap - Floor) * score / SaturationScore);
        }

        private static bool Struck(SpearLanding landing) =>
            landing.Class == SpearClass.Plate || landing.Class == SpearClass.Seam;

        /// <summary>
        /// P: the mean over spears that reached Plate class or better of (3.0
        /// if that plate is weak, else 1.0). 1.0 when no spear reached Plate.
        /// </summary>
        public static double PlateMultiplier(IReadOnlyList<SpearLanding> landings, int weakPlate)
        {
            double sum = 0;
            int count = 0;
            foreach (var landing in landings)
            {
                if (!Struck(landing)) continue;
                sum += landing.Plate == weakPlate ? WeakPlateMultiplier : 1.0;
                count++;
            }
            return count == 0 ? 1.0 : sum / count;
        }

        /// <summary>
        /// Auto: what auto-striking the best plate this attempt struck would
        /// have paid. 1.0 for a Glance-only or empty attempt (spec 3.2).
        /// </summary>
        public static double AutoFloor(IReadOnlyList<SpearLanding> landings, int weakPlate)
        {
            double best = 1.0;
            foreach (var landing in landings)
            {
                if (Struck(landing) && landing.Plate == weakPlate) best = WeakPlateMultiplier;
            }
            return best;
        }

        /// <summary>
        /// played = max(M x P, Auto): a played attempt is never worth less than
        /// auto-striking the same plate (owner decision, spec 3.2).
        /// </summary>
        public static double Played(IReadOnlyList<SpearLanding> landings, int weakPlate)
        {
            double m = Multiplier(Score(landings));
            return Math.Max(m * PlateMultiplier(landings, weakPlate), AutoFloor(landings, weakPlate));
        }

        /// <summary>
        /// The one plate this attempt breaks for everyone: the first spear, in
        /// tap order, that lands Plate or better on a non-weak, unbroken plate.
        /// Null when there is none (spec 3.3).
        /// </summary>
        public static int? BreakTarget(IReadOnlyList<SpearLanding> landingsInTapOrder, int weakPlate, int brokenMask)
        {
            foreach (var landing in landingsInTapOrder)
            {
                if (!Struck(landing)) continue;
                if (landing.Plate == weakPlate) continue;
                if ((brokenMask & (1 << landing.Plate)) != 0) continue;
                return landing.Plate;
            }
            return null;
        }

        /// <summary>
        /// True exactly when the broken mask covers all four non-weak plates:
        /// the board has solved itself by elimination (spec 3.3).
        /// </summary>
        public static bool RevealByElimination(int brokenMask, int weakPlate)
        {
            int allButWeak = ((1 << PlateCount) - 1) & ~(1 << weakPlate);
            return (brokenMask & allButWeak) == allButWeak;
        }

        /// <summary>
        /// Prices one strike on the locked row (spec 3.2 and 3.3.1): P, the
        /// auto-strike floor, played = max(M x P, Auto), and the one plate it
        /// breaks - never the last one standing. <paramref name="multiplier"/>
        /// is clamped to [Floor, Cap] whatever the caller passed.
        /// </summary>
        public static StrikePrice Price(IReadOnlyList<SpearLanding> landingsInTapOrder, int weakPlate, double multiplier, int brokenMask)
        {
            double m = double.IsNaN(multiplier) ? Floor : Math.Clamp(multiplier, Floor, Cap);
            double p = PlateMultiplier(landingsInTapOrder, weakPlate);
            double auto = AutoFloor(landingsInTapOrder, weakPlate);
            int? target = BreakTarget(landingsInTapOrder, weakPlate, brokenMask);
            if (target is int plate && WeakPlateDraw.WouldBreakTheLast(brokenMask, plate)) target = null;
            return new StrikePrice(m, p, auto, Math.Max(m * p, auto), target);
        }

        public static double Normalize(double angleDeg)
        {
            double a = angleDeg % 360.0;
            if (a < 0) a += 360.0;
            if (a >= 360.0) a -= 360.0;
            return a;
        }
    }
}

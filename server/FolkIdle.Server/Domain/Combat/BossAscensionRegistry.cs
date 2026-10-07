using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    public enum AscensionModifierKind : byte
    {
        /// <summary>
        /// The boss is stronger: health AND attack grow geometrically per step
        /// (see <see cref="BossAscensionRegistry.ModifiersFor"/>). Every step
        /// carries this; it is the kind of the steps with no new time limit.
        /// </summary>
        BossStrength = 8,
        /// <summary>Time limit: the kill must land within Value percent of the region's Swift limit (BossChallenge.Swift). The step ALSO grows the boss.</summary>
        TimeLimitPctOfSwift = 4,
    }

    /// <summary>
    /// What one step is: every step grows the boss; a step whose Kind is
    /// TimeLimitPctOfSwift also tightens the clock to Value percent of Swift.
    /// The words players read are region-specific (the multipliers are), so they
    /// come from <see cref="BossAscensionRegistry.SummaryFor"/>, not from here.
    /// </summary>
    public sealed record AscensionStepDefinition(int Step, AscensionModifierKind Kind, int Value);

    /// <summary>
    /// The cumulative modifiers of a step, flattened so the tick and the
    /// projection read one shape. AttackPct and BossHpPct are the TOTAL percent
    /// over the cleared boss at this step (not per-step increments). A plain struct:
    /// it is built on the tick thread for an Ascension fight, so no allocation.
    /// </summary>
    public readonly record struct AscensionModifiers(
        int AttackPct, int BossHpPct, int TimeLimitPctOfSwift)
    {
        public bool IsNone => AttackPct == 0 && BossHpPct == 0 && TimeLimitPctOfSwift == 0;
    }

    /// <summary>
    /// Task 87 (owner decision 2026-09-30): the Boss Ascension ladder. Every
    /// region boss can be fought again at ten steps; each step is the step
    /// below it PLUS one more modifier, and the first time a step is cleared it
    /// pays a title, and at steps 5 and 10 a bound frame. Cosmetics and titles
    /// ONLY: no gold, no diamonds, no gear and no chest (a chest is a
    /// tradeable cosmetic, which is gold by the back door), so PowerCeilingTests
    /// has no lever to add and the ladder cannot become part of the economy.
    ///
    /// Modul: THE LADDER IS <see cref="Steps"/>. Every number a step means lives
    /// in that one table; the tick, the projection, the client's ladder and the
    /// tests all read it through <see cref="ModifiersFor"/>, so a retune is one
    /// line here and BossChallengeCalibrationTests says whether it still holds.
    /// Kinds reuse what boss challenges already ask - Swift's time limit and
    /// Starved's empty bowl - plus the two knobs the boss wall itself is built
    /// from (its attack and its hit points) and the larder's bite cooldown.
    /// "One fewer larder slot" was the owner's example and is NOT a kind: the
    /// projection stocks the region's best fish in every slot, so a missing
    /// slot changes nothing it can measure, and a step that cannot be asserted
    /// harder does not belong on a calibrated ladder. Slowing the bite is the
    /// same idea (less sustain) in a unit the projection has.
    ///
    /// SLUGS AND IDS ARE STABLE STRINGS, NEVER POSITIONS
    /// (`ascension_r3_s7`, `frame_ascent_r3_s5`): see TitleRegistry.
    /// </summary>
    public static class BossAscensionRegistry
    {
        public const int MaxStep = 10;

        public static int FirstRegion => RaceUnlockRegistry.FirstRegion;
        public static int LastRegion => RaceUnlockRegistry.LastRegion;

        /// <summary>Title stem per region boss (Alpha Wolf, Shadow Lynx, Magma Wyrm, Frost Titan, Malakor).</summary>
        public static readonly string[] TitleStemByRegion = { "Wolfbane", "Lynxbane", "Wyrmbane", "Titanbane", "Scourge of Malakor" };

        public static readonly string[] Roman = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

        /// <summary>Steps that also pay a bound frame, on top of their title.</summary>
        public static readonly int[] FrameSteps = { 5, 10 };

        // ---- THE LADDER: step 1..10 ----------------------------------------
        // Modul: 2026-10-07, THE LADDER IS GEOMETRIC AND ENDS ON A BOSS TWO REGIONS AHEAD.
        //
        // It was cumulative +45% attack / +20% health over ten steps (15% on
        // steps 1, 5, 9; 10% on 3 and 7) and the level-78 players walked through
        // it: A10 was a Malakor-wall cousin of the boss they had already beaten.
        // Owner decision 2026-10-07: step 10 is the boss TWO regions ahead.
        //
        //   health(r, s) = base * (B(r+2).Hp / B(r).Hp) ^ (s / 10)
        //   attack(r, s) = base * (B(r+2).Atk / B(r).Atk) ^ (s / 10)
        //
        // every step grows BOTH, geometrically, so the ladder has no flat stretch
        // and no step that only moves one number. The step-10 ratios are computed
        // from the live boss stats (so a boss retune moves the ladder with it) and
        // printed by BossAscensionTests.WhatTheLadderIsPrinted.
        //
        // Nothing sits beyond Malakor, so Frost Titan and Malakor are extrapolated
        // at ExtrapolatedRegionRatio. The time-limit steps are kept (200% -> 120%
        // of Swift on steps 2, 4, 6, 8, 10).
        public static readonly IReadOnlyList<AscensionStepDefinition> Steps = new[]
        {
            new AscensionStepDefinition(1, AscensionModifierKind.BossStrength, 0),
            new AscensionStepDefinition(2, AscensionModifierKind.TimeLimitPctOfSwift, 200),
            new AscensionStepDefinition(3, AscensionModifierKind.BossStrength, 0),
            new AscensionStepDefinition(4, AscensionModifierKind.TimeLimitPctOfSwift, 175),
            new AscensionStepDefinition(5, AscensionModifierKind.BossStrength, 0),
            new AscensionStepDefinition(6, AscensionModifierKind.TimeLimitPctOfSwift, 155),
            new AscensionStepDefinition(7, AscensionModifierKind.BossStrength, 0),
            new AscensionStepDefinition(8, AscensionModifierKind.TimeLimitPctOfSwift, 140),
            new AscensionStepDefinition(9, AscensionModifierKind.BossStrength, 0),
            new AscensionStepDefinition(10, AscensionModifierKind.TimeLimitPctOfSwift, 120),
        };

        /// <summary>How many regions ahead step 10 lands.</summary>
        public const int RegionsAhead = 2;

        /// <summary>
        /// The per-region growth of (health, attack) beyond the last boss, for the
        /// two bosses with nothing two regions ahead of them. The geometric mean
        /// of the region 2->3, 3->4 and 4->5 ratios. Region 1 -> 2 is left out on
        /// purpose: it is the opening's own jump (x3.3 health, x12.5 attack from
        /// the first boss to the second) and every later step is a steady x2.6
        /// health / x4.2 attack, which is the slope the extrapolation should
        /// continue.
        /// </summary>
        public static (double Hp, double Attack) ExtrapolatedRegionRatio()
        {
            int first = FirstRegion + 1, last = LastRegion;
            double hpFirst = BossHp(first), atkFirst = BossAttack(first);
            double hpLast = BossHp(last), atkLast = BossAttack(last);
            if (hpFirst <= 0 || atkFirst <= 0 || hpLast <= 0 || atkLast <= 0) return (1.0, 1.0);
            double n = last - first;
            return (Math.Pow(hpLast / hpFirst, 1.0 / n), Math.Pow(atkLast / atkFirst, 1.0 / n));
        }

        private static double BossHp(int region) => ContentRegistry.GetScaledMonsterMaxHp(RaceUnlockRegistry.GetRegionBossMonsterId(region));

        private static double BossAttack(int region) => ContentRegistry.GetScaledMonsterAttackPower(RaceUnlockRegistry.GetRegionBossMonsterId(region));

        /// <summary>
        /// The (health, attack) multiplier at step 10 for a region's boss: the boss
        /// <see cref="RegionsAhead"/> regions ahead, over this one, on base stats.
        /// </summary>
        // Modul: MALAKOR'S LADDER IS MEASURED AGAINST PLAYERS, NOT EXTRAPOLATED
        // FROM MONSTERS (owner, 2026-10-07 evening). "Two regions ahead" put
        // Malakor A10 at x17.4 attack, and the owner beat it the same day with
        // no rebirth, partial inheritance and mixed gear - because the larder
        // heals a SHARE of the bar, a strong character's headroom grows far
        // faster than the monster tables do. AscensionCalibrationHarness loads
        // that real character from a production copy and finds the largest
        // attack multiplier it still beats (health rising as attack^0.68, the
        // split the old ladder had): about x19 on the post-2026-10-07 base. A
        // maxed endgame profile built on the same character (the better of its
        // gear and Transcendent region-5 Legendary in every stat, inheritance
        // 20/20, twelve renowned rebirths) beats about x34.
        //
        // The owner's targets: that character clears roughly A2-A3, the maxed
        // one A5-A7 with effort, and A10 needs about twice the maxed power. So
        // A3 sits at the real headroom, every later step is x1.2 (the maxed
        // profile's x34 lands between A6 x32.8 and A7 x39.4), and A10 is x68.
        // Steps 1-2 climb geometrically from 1 to A3. Re-run the harness after
        // any change to what makes a character strong; these numbers are only
        // as true as the last measurement.
        internal const double MalakorHeadroomAtStepThree = 19.0;
        internal const double MalakorStepGrowth = 1.2;
        internal const double MalakorHpExponent = 0.68;

        /// <summary>Malakor's attack multiplier at an Ascension step (1 = unchanged at step 0).</summary>
        internal static double MalakorAttackMultiplier(int step)
        {
            if (step <= 0) return 1.0;
            return step <= 3
                ? Math.Pow(MalakorHeadroomAtStepThree, step / 3.0)
                : MalakorHeadroomAtStepThree * Math.Pow(MalakorStepGrowth, step - 3);
        }

        public static (double Hp, double Attack) StepTenRatio(int region)
        {
            if (!IsValidRegion(region)) return (1.0, 1.0);
            if (region == LastRegion)
            {
                double attack = MalakorAttackMultiplier(MaxStep);
                return (Math.Pow(attack, MalakorHpExponent), attack);
            }
            double hp0 = BossHp(region), atk0 = BossAttack(region);
            if (hp0 <= 0 || atk0 <= 0) return (1.0, 1.0);

            int target = region + RegionsAhead;
            double hpT, atkT;
            if (target <= LastRegion)
            {
                hpT = BossHp(target);
                atkT = BossAttack(target);
            }
            else
            {
                var g = ExtrapolatedRegionRatio();
                int beyond = target - LastRegion;
                hpT = BossHp(LastRegion) * Math.Pow(g.Hp, beyond);
                atkT = BossAttack(LastRegion) * Math.Pow(g.Attack, beyond);
            }
            if (hpT <= 0 || atkT <= 0) return (1.0, 1.0);
            return (hpT / hp0, atkT / atk0);
        }

        public static string TitleSlug(int region, int step) => $"ascension_r{region}_s{step}";

        public static string FrameId(int region, int step) => $"frame_ascent_r{region}_s{step}";

        public static string? RewardFrameIdFor(int region, int step)
            => Array.IndexOf(FrameSteps, step) >= 0 ? FrameId(region, step) : null;

        private static string Stem(int region)
            => TitleStemByRegion[Math.Clamp(region - RaceUnlockRegistry.FirstRegion, 0, TitleStemByRegion.Length - 1)];

        public static string TitleName(int region, int step) => $"{Stem(region)} {Roman[step]}";

        public static string FrameName(int region, int step) => $"{Stem(region)} {(step >= MaxStep ? "Crown" : "Laurel")}";

        public static AscensionStepDefinition Step(int step) => Steps[step - 1];

        public static bool IsValidRegion(int region) => region >= RaceUnlockRegistry.FirstRegion && region <= RaceUnlockRegistry.LastRegion;

        public static bool IsValidStep(int step) => step >= 1 && step <= MaxStep;

        /// <summary>
        /// The cumulative modifiers at a step for a region's boss: health and attack
        /// at ratio^(step/10), as TOTAL percent over the cleared boss, and the
        /// tightest time limit in force. Step 0 (or a bad region) is none.
        /// </summary>
        /// <summary>
        /// Calibration seam (AscensionCalibrationHarness): when set, every armed
        /// step fights with these modifiers instead. Thread-static, so a harness
        /// driving the tick on its own thread cannot leak into a parallel test.
        /// </summary>
        [ThreadStatic] internal static AscensionModifiers? CalibrationOverride;

        public static AscensionModifiers ModifiersFor(int region, int step)
        {
            if (CalibrationOverride is { } calibration) return calibration;

            int s = Math.Min(step, MaxStep);
            if (s <= 0 || !IsValidRegion(region)) return default;

            int hpPct, atkPct;
            if (region == LastRegion)
            {
                double attack = MalakorAttackMultiplier(s);
                atkPct = (int)Math.Round((attack - 1.0) * 100.0);
                hpPct = (int)Math.Round((Math.Pow(attack, MalakorHpExponent) - 1.0) * 100.0);
            }
            else
            {
                var ratio = StepTenRatio(region);
                hpPct = (int)Math.Round((Math.Pow(ratio.Hp, s / (double)MaxStep) - 1.0) * 100.0);
                atkPct = (int)Math.Round((Math.Pow(ratio.Attack, s / (double)MaxStep) - 1.0) * 100.0);
            }

            int limit = 0;
            for (int i = 1; i <= s; i++)
            {
                var d = Steps[i - 1];
                // A later time limit REPLACES an earlier one; it only ever tightens.
                if (d.Kind == AscensionModifierKind.TimeLimitPctOfSwift) limit = limit == 0 ? d.Value : Math.Min(limit, d.Value);
            }
            return new AscensionModifiers(atkPct, hpPct, limit);
        }

        /// <summary>"x2.1" - a total percent over base, said as a multiplier.</summary>
        private static string Times(int totalPct)
        {
            double m = (100 + totalPct) / 100.0;
            return m >= 10
                ? "x" + m.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                : "x" + m.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The one sentence a step is, in the real cumulative numbers. The old text
        /// ("The boss hits 15% harder.") described a per-step increment and so said
        /// nothing true about how strong the boss had become.
        /// </summary>
        public static string SummaryFor(int region, int step)
        {
            var m = ModifiersFor(region, step);
            string strength = $"The boss has {Times(m.BossHpPct)} health and {Times(m.AttackPct)} attack.";
            var def = Step(step);
            if (def.Kind != AscensionModifierKind.TimeLimitPctOfSwift) return strength;
            string clock = step == 2
                ? $"Time limit: {def.Value}% of the boss's Swift time."
                : $"Time limit tightens to {def.Value}% of Swift.";
            return $"{strength} {clock}";
        }

        /// <summary>
        /// Every modifier in force at a step, as the sentences the client shows -
        /// the server owns the words so the client keeps no copy of the ladder.
        /// </summary>
        public static string[] DescribeEffects(int region, int step)
        {
            var m = ModifiersFor(region, step);
            var lines = new System.Collections.Generic.List<string>();
            if (m.AttackPct > 0) lines.Add($"Boss attack {Times(m.AttackPct)}");
            if (m.BossHpPct > 0) lines.Add($"Boss health {Times(m.BossHpPct)}");
            if (m.TimeLimitPctOfSwift > 0) lines.Add($"Kill it within {TimeLimitSecondsFor(region, step)} s");
            return lines.ToArray();
        }

        /// <summary>The step's kill-time limit in seconds for a region, or 0 for none.</summary>
        // Modul: 2026-10-07 - THE CLOCK SCALES WITH THE BOSS'S HEALTH.
        //
        // "N% of the boss's Swift time" was a fixed number of seconds per region
        // while the boss was a few percent tougher per step. With the geometric
        // ladder the boss is up to x8.7 the health at A10, so the same seconds
        // became arithmetically unreachable: measured with the benchmark's best
        // gear (Q14, region 5, level 100) Malakor A10 dies in 888 s against a
        // 172 s limit, and A5 against 251 s needs 338 s. A step nobody can clear
        // is a broken step, not a hard one. The Swift time of a boss with x6.9
        // the health IS x6.9 longer at the same damage, so the limit is
        // Swift * pct * (health multiplier at that step): the TIGHTENING (200% ->
        // 120%) stays exactly as the owner specified, the absolute seconds follow
        // the boss.
        public static int TimeLimitSecondsFor(int region, int step)
        {
            var m = ModifiersFor(region, step);
            int pct = m.TimeLimitPctOfSwift;
            if (pct <= 0) return 0;
            double hpMultiplier = 1.0 + m.BossHpPct / 100.0;
            return (int)Math.Ceiling(BossChallengeRegistry.TimeLimitSecondsFor(region) * pct / 100.0 * hpMultiplier);
        }

        // ---- the packed cache the tick reads ------------------------------
        // Four bits a region, five regions, one int: highest step cleared (0-10).
        public static int HighestStepOf(int packed, int region)
            => IsValidRegion(region) ? (packed >> ((region - RaceUnlockRegistry.FirstRegion) * 4)) & 0xF : 0;

        public static int WithHighestStep(int packed, int region, int step)
        {
            if (!IsValidRegion(region)) return packed;
            int shift = (region - RaceUnlockRegistry.FirstRegion) * 4;
            int current = (packed >> shift) & 0xF;
            if (step <= current) return packed;
            return (packed & ~(0xF << shift)) | (Math.Clamp(step, 0, MaxStep) << shift);
        }
    }

    /// <summary>
    /// The arithmetic the tick and the projection share. Percent maths in long,
    /// so a late-region boss (millions of attack power) cannot overflow it.
    /// </summary>
    public static class BossAscensionRules
    {
        public static long ScaleBossHp(long hp, in AscensionModifiers m)
            => m.BossHpPct == 0 ? hp : hp * (100L + m.BossHpPct) / 100L;

        public static long ScaleBossAttack(long attack, in AscensionModifiers m)
            => m.AttackPct == 0 ? attack : attack * (100L + m.AttackPct) / 100L;
    }
}

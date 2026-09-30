using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    public enum AscensionModifierKind : byte
    {
        /// <summary>The boss hits harder: +Value percent attack power.</summary>
        BossAttackPct = 1,
        /// <summary>The boss is tougher: +Value percent hit points.</summary>
        BossHpPct = 2,
        /// <summary>Time limit: the kill must land within Value percent of the region's Swift limit (BossChallenge.Swift).</summary>
        TimeLimitPctOfSwift = 4,
    }

    /// <summary>What one step ADDS to the steps below it.</summary>
    public sealed record AscensionStepDefinition(int Step, AscensionModifierKind Kind, int Value, string Summary);

    /// <summary>
    /// The cumulative modifiers of a step (every step up to and including it),
    /// flattened so the tick and the projection read one shape. A plain struct:
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

        // ---- THE LADDER: step 1..10, each ADDING one modifier -------------
        public static readonly IReadOnlyList<AscensionStepDefinition> Steps = new[]
        {
            new AscensionStepDefinition(1, AscensionModifierKind.BossAttackPct, 15, "The boss hits 15% harder."),
            new AscensionStepDefinition(2, AscensionModifierKind.TimeLimitPctOfSwift, 200, "Time limit: 200% of the boss's Swift time."),
            new AscensionStepDefinition(3, AscensionModifierKind.BossHpPct, 10, "The boss has 10% more health."),
            new AscensionStepDefinition(4, AscensionModifierKind.TimeLimitPctOfSwift, 175, "Time limit tightens to 175% of Swift."),
            new AscensionStepDefinition(5, AscensionModifierKind.BossAttackPct, 15, "The boss hits another 15% harder."),
            new AscensionStepDefinition(6, AscensionModifierKind.TimeLimitPctOfSwift, 155, "Time limit tightens to 155% of Swift."),
            new AscensionStepDefinition(7, AscensionModifierKind.BossHpPct, 10, "The boss has another 10% more health."),
            new AscensionStepDefinition(8, AscensionModifierKind.TimeLimitPctOfSwift, 140, "Time limit tightens to 140% of Swift."),
            new AscensionStepDefinition(9, AscensionModifierKind.BossAttackPct, 15, "The boss hits another 15% harder."),
            new AscensionStepDefinition(10, AscensionModifierKind.TimeLimitPctOfSwift, 120, "Time limit tightens to 120% of Swift."),
        };

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

        /// <summary>Every step up to and including <paramref name="step"/>, folded into one set of modifiers.</summary>
        public static AscensionModifiers ModifiersFor(int step)
        {
            int attack = 0, hp = 0, limit = 0;
            for (int s = 1; s <= Math.Min(step, MaxStep); s++)
            {
                var m = Steps[s - 1];
                switch (m.Kind)
                {
                    case AscensionModifierKind.BossAttackPct: attack += m.Value; break;
                    case AscensionModifierKind.BossHpPct: hp += m.Value; break;
                    // A later time limit REPLACES an earlier one; it only ever tightens.
                    case AscensionModifierKind.TimeLimitPctOfSwift: limit = limit == 0 ? m.Value : Math.Min(limit, m.Value); break;
                }
            }
            return new AscensionModifiers(attack, hp, limit);
        }

        /// <summary>
        /// Every modifier in force at a step, as the sentences the client shows -
        /// the server owns the words so the client keeps no copy of the ladder.
        /// </summary>
        public static string[] DescribeEffects(int region, int step)
        {
            var m = ModifiersFor(step);
            var lines = new System.Collections.Generic.List<string>();
            if (m.AttackPct > 0) lines.Add($"Boss attack +{m.AttackPct}%");
            if (m.BossHpPct > 0) lines.Add($"Boss health +{m.BossHpPct}%");
            if (m.TimeLimitPctOfSwift > 0) lines.Add($"Kill it within {TimeLimitSecondsFor(region, step)} s");
            return lines.ToArray();
        }

        /// <summary>The step's kill-time limit in seconds for a region, or 0 for none.</summary>
        public static int TimeLimitSecondsFor(int region, int step)
        {
            int pct = ModifiersFor(step).TimeLimitPctOfSwift;
            if (pct <= 0) return 0;
            return (int)Math.Ceiling(BossChallengeRegistry.TimeLimitSecondsFor(region) * pct / 100.0);
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

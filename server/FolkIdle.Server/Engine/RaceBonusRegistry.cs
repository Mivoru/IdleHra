using System.Collections.Generic;
using System.Globalization;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What each race does, as numbers AND as the sentences a player reads.
    ///
    /// Modul: 2026-10-29. Every race effect used to be a literal inside
    /// StatsCalculator or RaceMasteryResolver, and nothing told the player any
    /// of it - not the Inn, not the Wiki, not the mastery panel. A bonus nobody
    /// can see cannot inform a choice, and it was also how three of them sat
    /// broken for months (the 0.15 attack speeds, the Draugr lifesteal a
    /// hundredth of itself): no player could notice an effect they were never
    /// told about. The simulation reads these constants and Describe() builds
    /// its text from the SAME constants, so the page cannot drift from the
    /// fight. GET /api/v1/races serves Describe().
    /// </summary>
    public static class RaceBonusRegistry
    {
        // --- innate, always on for a character of the race ------------------
        public const float HumanGoldPct = 5f;

        // Modul: +15 dodge, was +10 and a "+20% ranged damage" (TASK_BOARD 124,
        // owner 2026-10-29). The ranged bonus multiplied FlatRangedDamage, a
        // stat combat stopped reading in the 2026-09-06 attribute rework, so it
        // did nothing. Replaced by +5 dodge rather than by damage: a Fairy is
        // already the fastest killer in region 5 (-23% seconds per kill against
        // a Human, measured) and the most fragile in region 1 (+36% damage
        // taken per swing), so damage would widen the gap where it leads and
        // dodge narrows it where it trails. Dodge counts from the first point
        // since #200.
        public const float VilaDodgePoints = 15f;
        public const float VilaBaseArmourFactor = 0.7f;

        public const float DraugrBaseMaxHpFactor = 1.25f;
        public const float DraugrBaseArmourFactor = 1.15f;
        public const float DraugrAttackSpeedPoints = -15f;

        public const float KoboldMiningYieldPoints = 30f;
        public const float MoosleuteHarvestYieldPoints = 20f;

        public const float VodnikRegenFactor = 1.15f;
        public const float VodnikCritMitigationPoints = 10f;

        // --- mastery milestones ----------------------------------------------
        public const int HumanXpFirstLevel = 10;
        public const int HumanXpFirstPct = 12;
        public const int HumanVaultLevel = 25;
        public const int HumanVaultSlots = 5;
        public const int HumanXpSecondLevel = 50;
        public const int HumanXpSecondPct = 15;

        public const int VilaSpeedLevel = 10;
        public const float VilaSpeedPoints = 15f;
        public const int VilaCritLevel = 50;
        public const float VilaCritPoints = 8f;

        public const int DraugrLifestealFirstLevel = 10;
        public const float DraugrLifestealFirstPct = 10f;
        public const int DraugrLifestealSecondLevel = 50;
        public const float DraugrLifestealSecondPct = 12f;

        public const int KoboldMiningLevel = 10;
        public const float KoboldMiningPoints = 20f;

        public const int MoosleuteHarvestLevel = 10;
        public const float MoosleuteHarvestPoints = 16f;

        public const int VodnikOfflineLevel = 25;
        public const long VodnikOfflineSeconds = 18L * 3600L;

        public sealed class MasteryLine
        {
            public int Level { get; init; }
            public string Text { get; init; } = string.Empty;
        }

        public sealed class RaceDescription
        {
            public int RaceId { get; init; }
            public string Name { get; init; } = string.Empty;
            public string[] Innate { get; init; } = System.Array.Empty<string>();
            public MasteryLine[] Mastery { get; init; } = System.Array.Empty<MasteryLine>();
            /// <summary>
            /// True when the mastery lines pay the whole account whichever race
            /// is fighting; false when only a character OF this race gets them.
            /// The split is how the simulation reads them, not a design choice
            /// made here: Fairy and Draugr mastery are applied in
            /// StatsCalculator for the active race only, the other four are read
            /// off the account's mastery level wherever they apply.
            /// </summary>
            public bool MasteryAccountWide { get; init; }
        }

        /// <summary>A monster crit's multiplier before Vodyanoy - SimulationEngine.MonsterCritMultiplier.</summary>
        public const float MonsterCritMultiplier = 1.5f;

        private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Pct(float factor) => N((factor - 1f) * 100f);

        /// <summary>The six races, in id order, in the words the client shows.</summary>
        public static IReadOnlyList<RaceDescription> Describe() => new[]
        {
            new RaceDescription
            {
                RaceId = RaceIds.Human, Name = RaceUnlockRegistry.GetRaceName(RaceIds.Human), MasteryAccountWide = true,
                Innate = new[] { $"+{N(HumanGoldPct)}% gold from kills." },
                Mastery = new[]
                {
                    new MasteryLine { Level = HumanXpFirstLevel, Text = $"+{HumanXpFirstPct}% experience." },
                    new MasteryLine { Level = HumanVaultLevel, Text = $"+{HumanVaultSlots} backpack slots." },
                    new MasteryLine { Level = HumanXpSecondLevel, Text = $"+{HumanXpSecondPct}% experience (replaces the +{HumanXpFirstPct}%)." },
                },
            },
            new RaceDescription
            {
                RaceId = RaceIds.Vila, Name = RaceUnlockRegistry.GetRaceName(RaceIds.Vila),
                Innate = new[]
                {
                    $"+{N(VilaDodgePoints)} dodge.",
                    $"{Pct(VilaBaseArmourFactor)}% armour from Vigour and potions (gear armour is not reduced).",
                },
                Mastery = new[]
                {
                    new MasteryLine { Level = VilaSpeedLevel, Text = $"+{N(VilaSpeedPoints)}% attack speed." },
                    new MasteryLine { Level = VilaCritLevel, Text = $"+{N(VilaCritPoints)} crit chance." },
                },
            },
            new RaceDescription
            {
                RaceId = RaceIds.Draugr, Name = RaceUnlockRegistry.GetRaceName(RaceIds.Draugr),
                Innate = new[]
                {
                    $"+{Pct(DraugrBaseMaxHpFactor)}% health from Vigour and potions.",
                    $"+{Pct(DraugrBaseArmourFactor)}% armour from Vigour and potions.",
                    $"{N(DraugrAttackSpeedPoints)}% attack speed - it can cancel the speed Finesse gives, but never makes you slower than the base swing.",
                },
                Mastery = new[]
                {
                    new MasteryLine { Level = DraugrLifestealFirstLevel, Text = $"{N(DraugrLifestealFirstPct)}% lifesteal (at most 1% of your health per hit)." },
                    new MasteryLine { Level = DraugrLifestealSecondLevel, Text = $"{N(DraugrLifestealSecondPct)}% lifesteal (replaces the {N(DraugrLifestealFirstPct)}%)." },
                },
            },
            new RaceDescription
            {
                RaceId = RaceIds.Kobold, Name = RaceUnlockRegistry.GetRaceName(RaceIds.Kobold), MasteryAccountWide = true,
                Innate = new[] { $"+{N(KoboldMiningYieldPoints)}% mining yield." },
                Mastery = new[]
                {
                    new MasteryLine { Level = KoboldMiningLevel, Text = $"+{N(KoboldMiningPoints)}% more mining yield." },
                },
            },
            new RaceDescription
            {
                RaceId = RaceIds.Vodnik, Name = RaceUnlockRegistry.GetRaceName(RaceIds.Vodnik), MasteryAccountWide = true,
                Innate = new[]
                {
                    $"+{Pct(VodnikRegenFactor)}% health regeneration between fights.",
                    $"Monster critical hits deal x{N(MonsterCritMultiplier - VodnikCritMitigationPoints / 100f)} instead of x{N(MonsterCritMultiplier)}.",
                },
                Mastery = new[]
                {
                    new MasteryLine { Level = VodnikOfflineLevel, Text = $"Offline progress keeps running for up to {VodnikOfflineSeconds / 3600} hours." },
                },
            },
            new RaceDescription
            {
                RaceId = RaceIds.Moosleute, Name = RaceUnlockRegistry.GetRaceName(RaceIds.Moosleute), MasteryAccountWide = true,
                Innate = new[] { $"+{N(MoosleuteHarvestYieldPoints)}% yield on woodcutting, fishing and herbs." },
                Mastery = new[]
                {
                    new MasteryLine { Level = MoosleuteHarvestLevel, Text = $"+{N(MoosleuteHarvestPoints)}% more yield on woodcutting, fishing and herbs." },
                },
            },
        };
    }
}

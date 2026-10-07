using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    public enum BossChallenge : byte
    {
        /// <summary>No food eaten during the fight.</summary>
        Starved = 1,
        /// <summary>At or below the region's level cap.</summary>
        Young = 2,
        /// <summary>
        /// Within the region's time limit. Replaced "Humble blade" (a Common
        /// weapon) before it ever shipped: a plain weapon still won every
        /// fight, so it asked nothing (owner, 2026-09-28).
        /// </summary>
        Swift = 3,
    }

    public sealed record BossChallengeDefinition(BossChallenge Id, string Title, string Description);

    /// <summary>
    /// Task 55: three optional conditions on every region boss. Meeting one at a
    /// kill completes it once, for good, and pays a cosmetic chest (owner,
    /// 2026-09-28: a chest by region - Rare for regions 1-2, Epic for 3-4,
    /// Legendary for 5).
    ///
    /// Modul: EVERY NUMBER HERE WAS MEASURED, NOT PICKED. BossChallengeCalibrationTests
    /// projects each challenge with the live boss fight (BossGearBenchmark) and
    /// asserts it can be won with the gear the boss wall is tuned to. What it
    /// found, 2026-09-28, against a boss already beaten once (challenges count
    /// on any kill, not only the first):
    ///
    ///   region   lowest level, fed   unfed   level cap (midpoint to the ref level)
    ///     1              6             20        15
    ///     2             26             54        35
    ///     3             32             72        45
    ///     4             38             85        60
    ///     5             46             97        75
    ///
    /// "Starved" is impossible on a FIRST clear of Malakor at any level, which
    /// is why it counts on repeat kills too.
    ///
    /// "Swift" - a time limit - is set just under what the BEST gear of the
    /// region does at the region's reference level (quality required + 4), and
    /// above what the wall's own required gear does. Measured kill times, same
    /// projection, boss already beaten:
    ///
    ///   region   required gear   best gear at level   limit   next region's gear
    ///     1          82 s              76 s             80 s        34 s
    ///     2         115 s             106 s            110 s        51 s
    ///     3         135 s             117 s            120 s        48 s
    ///     4         127 s             105 s            110 s        46 s
    ///     5         122 s             105 s            110 s          -
    ///
    /// Quality within a region barely moves a boss fight, so in practice this
    /// reads "the region's top gear, or come back stronger". The fight's length
    /// is CombatTargetTickAccumulator, zeroed at the spawn - the same clock the
    /// personal records' fastest boss kill reads.
    /// </summary>
    public static class BossChallengeRegistry
    {
        public static readonly IReadOnlyList<BossChallenge> All =
            new[] { BossChallenge.Starved, BossChallenge.Young, BossChallenge.Swift };

        private static readonly int[] LevelCapByRegion = { 15, 35, 45, 60, 75 };

        // Modul: 2026-10-07 balance pass: every region boss has 1.3x its old base health, and a
        // kill takes proportionally longer, so Swift scales with it. Old { 80, 110, 120, 110, 110 }
        // -> new { 104, 143, 156, 143, 143 } (x1.3). Measured with the same projection after the
        // change: the region's best gear at level takes 99 / 135 / 152 / 136 / 130 s (all under the
        // limit), the wall's required gear takes more than every limit (calibration test).
        private static readonly int[] TimeLimitSecondsByRegion = { 104, 143, 156, 143, 143 };

        /// <summary>How many quality tiers above the wall's requirement "the region's best gear" is.</summary>
        public const int SwiftCalibrationQualityStep = 4;

        private static readonly int[] RewardChestByRegion =
        {
            CosmeticRegistry.Rare, CosmeticRegistry.Rare, CosmeticRegistry.Epic, CosmeticRegistry.Epic, CosmeticRegistry.Legendary,
        };

        private static int Index(int region)
            => Math.Clamp(region - RaceUnlockRegistry.FirstRegion, 0, LevelCapByRegion.Length - 1);

        public static int LevelCapFor(int region) => LevelCapByRegion[Index(region)];

        public static int TimeLimitSecondsFor(int region) => TimeLimitSecondsByRegion[Index(region)];

        public static int RewardChestRarityFor(int region) => RewardChestByRegion[Index(region)];

        public static BossChallengeDefinition Describe(BossChallenge challenge, int region) => challenge switch
        {
            BossChallenge.Starved => new(challenge, "Starved", "Win without eating a single bite during the fight."),
            BossChallenge.Young => new(challenge, "Young blood", $"Win at level {LevelCapFor(region)} or lower."),
            BossChallenge.Swift => new(challenge, "Swift", $"Win in {TimeLimitSecondsFor(region)} seconds or less."),
            _ => throw new ArgumentOutOfRangeException(nameof(challenge)),
        };

        /// <summary>
        /// Which challenges a kill met. Pure, so the rules are tested without a
        /// tick: the level at the kill, whether any food was eaten during the
        /// fight, and how long the fight took in tenths of a second.
        /// </summary>
        public static List<BossChallenge> Met(int region, int level, bool ateDuringFight, int fightTenths)
        {
            var met = new List<BossChallenge>(3);
            if (!ateDuringFight) met.Add(BossChallenge.Starved);
            if (level <= LevelCapFor(region)) met.Add(BossChallenge.Young);
            if (fightTenths > 0 && fightTenths <= TimeLimitSecondsFor(region) * 10) met.Add(BossChallenge.Swift);
            return met;
        }
    }
}

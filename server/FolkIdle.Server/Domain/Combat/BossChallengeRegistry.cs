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
        /// <summary>With a weapon of Common rarity or plainer - or none.</summary>
        Humble = 3,
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
    /// is why it counts on repeat kills too. "Humble" is the gentle one: a Normal
    /// weapon still wins every fight at the region's reference level, because
    /// the armour carries it - so it is a choice to make, not a wall to climb.
    /// </summary>
    public static class BossChallengeRegistry
    {
        public static readonly IReadOnlyList<BossChallenge> All =
            new[] { BossChallenge.Starved, BossChallenge.Young, BossChallenge.Humble };

        private static readonly int[] LevelCapByRegion = { 15, 35, 45, 60, 75 };

        /// <summary>The plainest weapon the Humble challenge allows: Common.</summary>
        public const int HumbleMaxWeaponTier = RarityTier.Common;

        private static readonly int[] RewardChestByRegion =
        {
            CosmeticRegistry.Rare, CosmeticRegistry.Rare, CosmeticRegistry.Epic, CosmeticRegistry.Epic, CosmeticRegistry.Legendary,
        };

        private static int Index(int region)
            => Math.Clamp(region - RaceUnlockRegistry.FirstRegion, 0, LevelCapByRegion.Length - 1);

        public static int LevelCapFor(int region) => LevelCapByRegion[Index(region)];

        public static int RewardChestRarityFor(int region) => RewardChestByRegion[Index(region)];

        public static BossChallengeDefinition Describe(BossChallenge challenge, int region) => challenge switch
        {
            BossChallenge.Starved => new(challenge, "Starved", "Win without eating a single bite during the fight."),
            BossChallenge.Young => new(challenge, "Young blood", $"Win at level {LevelCapFor(region)} or lower."),
            BossChallenge.Humble => new(challenge, "Humble blade", "Win with a Common weapon or plainer - or none at all."),
            _ => throw new ArgumentOutOfRangeException(nameof(challenge)),
        };

        /// <summary>
        /// Which challenges a kill met. Pure, so the rules are tested without a
        /// tick: the level at the kill, whether any food was eaten during the
        /// fight, and the worn weapon's quality tier (0 for no weapon).
        /// </summary>
        public static List<BossChallenge> Met(int region, int level, bool ateDuringFight, int weaponQualityTier)
        {
            var met = new List<BossChallenge>(3);
            if (!ateDuringFight) met.Add(BossChallenge.Starved);
            if (level <= LevelCapFor(region)) met.Add(BossChallenge.Young);
            if (weaponQualityTier <= HumbleMaxWeaponTier) met.Add(BossChallenge.Humble);
            return met;
        }
    }
}

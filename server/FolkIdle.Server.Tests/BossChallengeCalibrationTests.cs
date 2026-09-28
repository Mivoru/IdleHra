using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 55: what each boss challenge costs, measured with the live boss
    /// projection (BossGearBenchmark) against the reference character the boss
    /// wall is tuned to - its region's gear at the required quality tier.
    /// </summary>
    public class BossChallengeCalibrationTests
    {
        private readonly ITestOutputHelper _o;

        public BossChallengeCalibrationTests(ITestOutputHelper o)
        {
            _o = o;
            ContentRegistry.Initialize();
        }

        private static int BossOf(int region) => RaceUnlockRegistry.GetRegionBossMonsterId(region);

        private static ReferenceLoadout Gear(int region, int level)
            => new(level, region, BossFirstClearRules.RequiredQualityTierFor(region), BossFirstClearRules.RequiredAffixRarityFor(region));

        private static int LowestWinningLevel(int region, bool firstClear, bool withFood, int weaponTier = 0)
        {
            for (int level = 1; level <= 150; level++)
            {
                var gear = Gear(region, level);
                if (BossGearBenchmark.ProjectChallenge(BossOf(region), in gear, firstClear, withFood, weaponTier).PlayerWins)
                {
                    return level;
                }
            }
            return -1;
        }

        private static int LowestWinningWeaponTier(int region, int level, bool firstClear)
        {
            var gear = Gear(region, level);
            for (int tier = 1; tier <= 14; tier++)
            {
                if (BossGearBenchmark.ProjectChallenge(BossOf(region), in gear, firstClear, withFood: true, tier).PlayerWins)
                {
                    return tier;
                }
            }
            return -1;
        }

        /// <summary>
        /// Every challenge can be won with the gear the boss wall is tuned to,
        /// on a boss already beaten once - measured, and asserted, so a retune
        /// of the wall or the larder that makes one impossible fails here
        /// rather than in front of a player.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EveryChallengeCanBeWon(int region)
        {
            int cap = BossChallengeRegistry.LevelCapFor(region);
            int reference = BossGearBenchmark.ReferenceLevelForRegion(region);

            int fedFloor = LowestWinningLevel(region, firstClear: false, withFood: true);
            Assert.InRange(fedFloor, 1, cap);

            // A real challenge, not a free one: the cap sits below the level a
            // player normally meets this boss at.
            Assert.True(cap < reference, $"region {region}: a cap of {cap} at a reference level of {reference} asks nothing");

            int unfedFloor = LowestWinningLevel(region, firstClear: false, withFood: false);
            Assert.InRange(unfedFloor, 1, 120);

            var gear = Gear(region, reference);
            Assert.True(
                BossGearBenchmark.ProjectChallenge(BossOf(region), in gear, firstClear: false, withFood: true, BossChallengeRegistry.HumbleMaxWeaponTier).PlayerWins,
                $"region {region}: a Common weapon loses at the reference level");
        }

        [Fact]
        public void WhatEachChallengeCosts()
        {
            for (int region = 1; region <= 5; region++)
            {
                int reference = BossGearBenchmark.ReferenceLevelForRegion(region);
                foreach (bool first in new[] { false, true })
                {
                    _o.WriteLine(
                        $"region {region} {(first ? "first clear" : "cleared   ")}: ref level {reference}, required Q{BossFirstClearRules.RequiredQualityTierFor(region)} | " +
                        $"lowest level fed {LowestWinningLevel(region, first, true),4}, unfed {LowestWinningLevel(region, first, false),4} | " +
                        $"weakest weapon tier at ref level {LowestWinningWeaponTier(region, reference, first),3}");
                }
            }
        }
    }
}

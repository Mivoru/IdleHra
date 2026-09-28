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

        private static int LowestWinningLevel(int region, bool firstClear, bool withFood)
        {
            for (int level = 1; level <= 150; level++)
            {
                var gear = Gear(region, level);
                if (BossGearBenchmark.ProjectChallenge(BossOf(region), in gear, firstClear, withFood).PlayerWins)
                {
                    return level;
                }
            }
            return -1;
        }

        /// <summary>Seconds to kill the (already beaten) boss at the region's reference level, at a quality step above the wall's requirement.</summary>
        private static double KillSeconds(int region, int qualityStep)
        {
            int quality = Math.Clamp(BossFirstClearRules.RequiredQualityTierFor(region) + qualityStep, 1, 14);
            var gear = new ReferenceLoadout(BossGearBenchmark.ReferenceLevelForRegion(region), region, quality, BossFirstClearRules.RequiredAffixRarityFor(region));
            var fight = BossGearBenchmark.ProjectChallenge(BossOf(region), in gear, firstClear: false, withFood: true);
            return fight.PlayerWins ? fight.SecondsToKillBoss : double.PositiveInfinity;
        }

        /// <summary>
        /// Every challenge can be won with gear this region offers, on a boss
        /// already beaten once - measured, and asserted, so a retune of the
        /// wall, the larder or the damage model that makes one impossible (or
        /// free) fails here rather than in front of a player.
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

            // Swift: the region's best gear at its level makes the limit; the
            // wall's own required gear does not.
            double limit = BossChallengeRegistry.TimeLimitSecondsFor(region);
            double best = KillSeconds(region, BossChallengeRegistry.SwiftCalibrationQualityStep);
            double required = KillSeconds(region, 0);
            Assert.True(best <= limit, $"region {region}: the region's best gear takes {best:F0} s against a {limit} s limit");
            Assert.True(required > limit, $"region {region}: the required gear already makes {limit} s ({required:F0} s) - the limit asks nothing");
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
                        $"lowest level fed {LowestWinningLevel(region, first, true),4}, unfed {LowestWinningLevel(region, first, false),4}");
                }
                _o.WriteLine(
                    $"region {region} Swift: limit {BossChallengeRegistry.TimeLimitSecondsFor(region)} s | required gear {KillSeconds(region, 0):F0} s, " +
                    $"best at level {KillSeconds(region, BossChallengeRegistry.SwiftCalibrationQualityStep):F0} s");
            }
        }
    }
}

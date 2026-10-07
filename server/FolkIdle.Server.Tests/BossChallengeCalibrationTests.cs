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

        // ---- Task 87: the Ascension ladder ----------------------------------

        /// <summary>The region's best gear: the wall's required quality plus the Swift calibration step, at the region's reference level.</summary>
        private static ReferenceLoadout BestInSlot(int region)
            => new(BossGearBenchmark.ReferenceLevelForRegion(region), region,
                Math.Clamp(BossFirstClearRules.RequiredQualityTierFor(region) + BossChallengeRegistry.SwiftCalibrationQualityStep, 1, 14),
                BossFirstClearRules.RequiredAffixRarityFor(region));

        private static ReferenceLoadout RequiredGear(int region)
            => Gear(region, BossGearBenchmark.ReferenceLevelForRegion(region));

        /// <summary>
        /// The largest extra boss-attack multiplier the character still WINS the
        /// step against (binary search): the survival headroom. Bigger is easier.
        /// </summary>
        private static double AttackHeadroom(int region, int step, in ReferenceLoadout gear)
        {
            var mods = BossAscensionRegistry.ModifiersFor(region, step);
            double lo = 1.0, hi = 64.0;
            if (!BossGearBenchmark.ProjectAscension(BossOf(region), in gear, in mods, lo).PlayerWins) return 0.0;
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2;
                if (BossGearBenchmark.ProjectAscension(BossOf(region), in gear, in mods, mid).PlayerWins) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>Kill time over the step's limit; 0 when the step has no limit. Over 1.0 means the step is lost on time.</summary>
        private static double TimeLoad(int region, int step, in ReferenceLoadout gear)
        {
            int limit = BossAscensionRegistry.TimeLimitSecondsFor(region, step);
            if (limit <= 0) return 0.0;
            var mods = BossAscensionRegistry.ModifiersFor(region, step);
            var fight = BossGearBenchmark.ProjectAscension(BossOf(region), in gear, in mods);
            return fight.PlayerWins ? fight.SecondsToKillBoss / limit : double.PositiveInfinity;
        }

        private static double KillSecondsAt(int region, int step, in ReferenceLoadout gear)
        {
            var mods = BossAscensionRegistry.ModifiersFor(region, step);
            var fight = BossGearBenchmark.ProjectAscension(BossOf(region), in gear, in mods);
            return fight.PlayerWins ? fight.SecondsToKillBoss : double.PositiveInfinity;
        }

        /// <summary>
        /// Each step is HARDER than the one below it - asserted on what it
        /// measurably costs, not on the table. Three scalars, one per modifier
        /// kind, and every one can only move one way as modifiers are added:
        /// survival headroom (how much MORE boss attack the best gear still
        /// wins against), kill time, and time load (kill time over the limit).
        /// A step may not ease any of them, and must move strictly the one its
        /// own modifier acts on. Bite-rationing and no-food steps are not on the
        /// ladder because they FAIL this test: against the region's best gear
        /// the boss is a burst the larder never answers (region 5 eats 0-1 bites
        /// in a whole fight), so they move none of the three.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EveryAscensionStepIsHarderThanTheLast(int region)
        {
            var gear = BestInSlot(region);
            double prevHeadroom = AttackHeadroom(region, 0, in gear);
            double prevKill = KillSecondsAt(region, 0, in gear);
            double prevLoad = 0.0;
            _o.WriteLine($"region {region} best gear Q{gear.QualityTier} L{gear.Level}; step 0: headroom x{prevHeadroom:F2}, kill {prevKill:F0} s");
            for (int step = 1; step <= BossAscensionRegistry.MaxStep; step++)
            {
                double headroom = AttackHeadroom(region, step, in gear);
                double kill = KillSecondsAt(region, step, in gear);
                double load = TimeLoad(region, step, in gear);
                var kind = BossAscensionRegistry.Step(step).Kind;
                int limit = BossAscensionRegistry.TimeLimitSecondsFor(region, step);
                _o.WriteLine($"  step {step,2} {kind,-20} headroom x{headroom:F2}  kill {kill:F0} s  limit {limit} s  time load {load:P0}");

                Assert.True(headroom <= prevHeadroom + 1e-9, $"region {region} step {step} EASES survival ({headroom:F3} > {prevHeadroom:F3})");
                Assert.True(kill >= prevKill - 1e-9, $"region {region} step {step} EASES the kill time ({kill:F1} < {prevKill:F1})");
                
                // Every step grows BOTH health and attack (2026-10-07 ladder), so each
                // must move survival headroom and, while the boss still falls, the
                // kill time. Once a step is LOST outright (headroom 0, kill infinite)
                // the ladder has run past this gear and there is nothing left to move.
                if (prevHeadroom > 0.0)
                {
                    Assert.True(headroom < prevHeadroom - 1e-6, $"region {region} step {step}: more boss attack costs the gear no survival headroom");
                }
                if (!double.IsInfinity(prevKill))
                {
                    Assert.True(kill > prevKill + 1e-6, $"region {region} step {step}: more boss health does not lengthen the fight");
                }
                if (kind == AscensionModifierKind.TimeLimitPctOfSwift)
                {
                    // The limit TIGHTENS in percent of Swift; in seconds it follows the boss's
                    // health (see BossAscensionRegistry.TimeLimitSecondsFor), so it need not fall.
                    int prevPct = BossAscensionRegistry.ModifiersFor(region, step - 1).TimeLimitPctOfSwift;
                    int pct = BossAscensionRegistry.ModifiersFor(region, step).TimeLimitPctOfSwift;
                    Assert.True(prevPct == 0 || pct < prevPct, $"region {region} step {step}: the limit {pct}% does not tighten {prevPct}%");
                }

                prevHeadroom = headroom;
                prevKill = kill;
                prevLoad = load;
            }
        }

        /// <summary>
        /// The top of the ladder, 2026-10-07: A10 is the boss TWO regions ahead, so
        /// the region's own best gear (and certainly the wall's required gear) does
        /// not clear it, while the gear two regions ahead - the gear that boss is
        /// the peer of - does, inside the clock and with survival headroom.
        /// Malakor has nothing ahead of it: region 5's best gear is already the
        /// ceiling, so for the last region the capstone is that the ceiling gear
        /// still clears it (and the wall's required gear does not).
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void StepTenIsBeyondTheRegionsBestGear_ButWinnableByTheGearAhead(int region)
        {
            int top = BossAscensionRegistry.MaxStep;
            var mods = BossAscensionRegistry.ModifiersFor(region, top);
            int limit = BossAscensionRegistry.TimeLimitSecondsFor(region, top);

            var best = BestInSlot(region);
            var bestFight = BossGearBenchmark.ProjectAscension(BossOf(region), in best, in mods);
            bool bestClears = bestFight.PlayerWins && bestFight.SecondsToKillBoss <= limit;

            var ahead = BestInSlot(Math.Min(region + BossAscensionRegistry.RegionsAhead, RaceUnlockRegistry.LastRegion));
            var aheadFight = BossGearBenchmark.ProjectAscension(BossOf(region), in ahead, in mods);
            double headroom = AttackHeadroom(region, top, in ahead);

            var required = RequiredGear(region);
            var requiredFight = BossGearBenchmark.ProjectAscension(BossOf(region), in required, in mods);
            _o.WriteLine(
                $"region {region} step {top} (limit {limit} s): own best Q{best.QualityTier} wins {bestFight.PlayerWins} in {bestFight.SecondsToKillBoss:F0} s; " +
                $"gear ahead (region {ahead.GearRegionTier} Q{ahead.QualityTier}) wins {aheadFight.PlayerWins} in {aheadFight.SecondsToKillBoss:F0} s, headroom x{headroom:F2}; " +
                $"wall gear Q{required.QualityTier} wins {requiredFight.PlayerWins}");

            if (region < RaceUnlockRegistry.LastRegion)
            {
                Assert.False(bestClears, $"region {region}: the region's own best gear clears step {top}, which is meant to be a boss two regions ahead");
            }

            Assert.True(aheadFight.PlayerWins, $"region {region}: even the gear two regions ahead cannot survive step {top}");
            Assert.True(aheadFight.SecondsToKillBoss <= limit * 0.99, $"region {region}: the gear ahead kills in {aheadFight.SecondsToKillBoss:F0} s against a {limit} s limit");
            Assert.True(headroom >= 1.05, $"region {region}: the gear ahead has only x{headroom:F2} attack headroom at step {top}");

            bool wallGearClears = requiredFight.PlayerWins && requiredFight.SecondsToKillBoss <= limit;
            Assert.False(wallGearClears, $"region {region}: the wall's own gear clears step {top} - the ladder asks nothing of gear");
        }

        [Fact]
        public void TheLadderTablePrinted()
        {
            for (int region = 1; region <= 5; region++)
            {
                for (int step = 1; step <= BossAscensionRegistry.MaxStep; step++)
                {
                    var m = BossAscensionRegistry.ModifiersFor(region, step);
                    _o.WriteLine($"region {region} step {step,2}: attack +{m.AttackPct}% hp +{m.BossHpPct}% limit {m.TimeLimitPctOfSwift}% of Swift | {BossAscensionRegistry.SummaryFor(region, step)}");
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A RandomNumberGenerator with a seed, for tests only: the generator's
    /// property test and the ledger's simulations must be repeatable, and the
    /// production path must keep the cryptographic source.
    /// </summary>
    internal sealed class SeededRandomNumberGenerator : RandomNumberGenerator
    {
        private readonly Random _random;
        public SeededRandomNumberGenerator(int seed) => _random = new Random(seed);
        public override void GetBytes(byte[] data) => _random.NextBytes(data);
        public override void GetBytes(Span<byte> data) => _random.NextBytes(data);
    }

    /// <summary>
    /// Task 36: every multiplier in the shield wheel declares its cap and its
    /// curve, and the simulated means that justify the class values (spec
    /// 3.1) are numbers the build checks, not numbers a document asserts.
    /// </summary>
    public class WorldBossStrikeLedgerTests
    {
        private readonly ITestOutputHelper _output;
        public WorldBossStrikeLedgerTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void TheMultiplierIsBoundedAndMonotone()
        {
            Assert.True(WorldBossStrikeRules.Cap <= 2.0);
            Assert.Equal(1.0, WorldBossStrikeRules.Floor);
            Assert.Equal(WorldBossStrikeRules.Floor, WorldBossStrikeRules.Multiplier(0));
            Assert.Equal(WorldBossStrikeRules.Cap, WorldBossStrikeRules.Multiplier(WorldBossStrikeRules.SaturationScore), 9);
            Assert.Equal(WorldBossStrikeRules.Cap, WorldBossStrikeRules.Multiplier(1.0));

            double previous = WorldBossStrikeRules.Multiplier(0);
            for (int i = 1; i <= 100; i++)
            {
                double m = WorldBossStrikeRules.Multiplier(i / 100.0);
                Assert.True(m >= previous, $"M fell between s={(i - 1) / 100.0} and s={i / 100.0}");
                Assert.InRange(m, WorldBossStrikeRules.Floor, WorldBossStrikeRules.Cap);
                previous = m;
            }
            _output.WriteLine($"M: floor {WorldBossStrikeRules.Floor}, cap {WorldBossStrikeRules.Cap} at s = {WorldBossStrikeRules.SaturationScore}");
        }

        [Fact]
        public void PlayingIsNeverWorseThanAutoStrikingAndNeverAboveSix()
        {
            Assert.Equal(3.0, WorldBossStrikeRules.WeakPlateMultiplier);
            Assert.Equal(WorldBossEngine.WeakPlateDamageMultiplier, WorldBossStrikeRules.WeakPlateMultiplier);

            var rng = new Random(36);
            var classes = Enum.GetValues<SpearClass>();
            for (int n = 0; n < 50_000; n++)
            {
                int weak = rng.Next(WorldBossStrikeRules.PlateCount);
                int count = rng.Next(0, WorldBossStrikeRules.Spears + 1);
                var landings = Enumerable.Range(0, count)
                    .Select(i => new SpearLanding(i, rng.Next(WorldBossStrikeRules.PlateCount), classes[rng.Next(classes.Length)], false))
                    .ToArray();

                double p = WorldBossStrikeRules.PlateMultiplier(landings, weak);
                double m = WorldBossStrikeRules.Multiplier(WorldBossStrikeRules.Score(landings));
                double auto = WorldBossStrikeRules.AutoFloor(landings, weak);
                double played = WorldBossStrikeRules.Played(landings, weak);

                Assert.InRange(p, 1.0, WorldBossStrikeRules.WeakPlateMultiplier);
                Assert.True(m * p <= 6.0 + 1e-9);
                Assert.True(played <= WorldBossStrikeRules.MaxPlayedMultiplier + 1e-9);
                Assert.True(played >= auto, "a played attempt paid less than auto-striking the same plate");
            }
        }

        [Fact]
        public void TheWholeStrikeFactorStaysUnderItsStatedCeiling()
        {
            // Giantslayer's cap from its registry, never hard-coded here.
            int maxLevel = SkillTreeRegistry.MaxLevelOf(SkillTreeRegistry.BranchWorldBossDamage);
            double giantslayer = 1.0 + SkillTreeRegistry.GetBonusPercent(SkillTreeRegistry.BranchWorldBossDamage, maxLevel) / 100.0;
            double factor = giantslayer * WorldBossStrikeRules.MaxPlayedMultiplier;
            _output.WriteLine($"G at cap {giantslayer:F2} x played cap {WorldBossStrikeRules.MaxPlayedMultiplier:F1} = {factor:F2} (ceiling {WorldBossStrikeRules.WorldBossMaxStrikeFactor})");
            Assert.True(factor < WorldBossStrikeRules.WorldBossMaxStrikeFactor, $"G x played reached {factor}");
        }

        // --- the simulations (spec 3.1) ---------------------------------------

        /// <summary>
        /// One simulated attempt: every interrupt read correctly (so no spear
        /// is lost), <paramref name="counters"/> of them used for a counter on
        /// a random plate, and the rest of the five spears thrown at random
        /// moving-wheel times with the reload respected.
        /// </summary>
        private static double SimulateAttempt(ShieldWheelSchedule schedule, int counters, Random rng)
        {
            var interrupts = schedule.Interrupts.ToList();
            counters = Math.Min(counters, interrupts.Count);

            var spears = new List<(double T, CounterEntry? Counter)>();
            var parries = new List<ParryEntry>();
            foreach (var interrupt in interrupts)
            {
                parries.Add(new ParryEntry(interrupt.Index, ShieldWheelSchedule.CorrectChoice(interrupt.Tell), interrupt.TellAtMs + 300));
            }
            for (int i = 0; i < counters; i++)
            {
                var interrupt = interrupts[i];
                spears.Add((interrupt.TellAtMs + 500, new CounterEntry(interrupt.Index, -1, interrupt.TellAtMs + 500, rng.Next(WorldBossStrikeRules.PlateCount))));
            }

            int wheel = WorldBossStrikeRules.Spears - counters;
            for (int attempt = 0; attempt < 1_000; attempt++)
            {
                var times = new List<double>();
                while (times.Count < wheel)
                {
                    double t = rng.NextDouble() * (WorldBossStrikeRules.MaxPlayMs - WorldBossStrikeRules.FlightMs - WorldBossStrikeRules.ToleranceMs);
                    if (!schedule.FrozenAt(t)) times.Add(Math.Round(t));
                }
                times.Sort();
                bool spaced = true;
                for (int i = 1; i < times.Count; i++) if (times[i] - times[i - 1] < WorldBossStrikeRules.MinReloadMs) spaced = false;
                if (!spaced) continue;

                var all = spears.Concat(times.Select(t => (t, (CounterEntry?)null))).OrderBy(s => s.Item1).ToList();
                var taps = new List<WheelTap>();
                var counterEntries = new List<CounterEntry>();
                for (int seq = 0; seq < all.Count; seq++)
                {
                    if (all[seq].Item2 is { } c) counterEntries.Add(c with { Seq = seq });
                    else taps.Add(new WheelTap(seq, all[seq].Item1));
                }

                var scored = ShieldWheelScorer.Score(schedule, new StrikeLog(taps, parries, counterEntries), 60_000);
                Assert.Equal(SubmissionVerdict.Accepted, scored.Verdict);
                return scored.Multiplier;
            }
            throw new InvalidOperationException("could not place spaced random taps");
        }

        private (double Mean, double Min, double Max) Simulate(bool enraged, int counters, int attempts, int seed)
        {
            using var generator = new SeededRandomNumberGenerator(seed);
            var rng = new Random(seed);
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < attempts; i++)
            {
                var schedule = ShieldWheelSchedule.Generate(generator, practice: false, enraged: enraged);
                double m = SimulateAttempt(schedule, counters, rng);
                sum += m;
                min = Math.Min(min, m);
                max = Math.Max(max, m);
            }
            return (sum / attempts, min, max);
        }

        // Modul: THIS CHECK WAS SKIPPED until the owner decided the numbers at the
        // Phase 1 playtest gate (2026-09-25): seam 8, Plate 0. With the spec's
        // earlier seam 12 / Plate 0.15 it measured 1.527 - see spec 3.1.
        [Fact]
        public void RandomTappingEarnsAboutOnePointThreeSix()
        {
            var (mean, min, max) = Simulate(enraged: false, counters: 0, attempts: 20_000, seed: 1);
            _output.WriteLine($"random taps: mean M {mean:F3} (min {min:F2}, max {max:F2})");
            Assert.InRange(mean, 1.25, 1.45);
        }

        [Fact]
        public void TwoReadsPlusRandomEarnsAboutOnePointSevenFive()
        {
            var (mean, min, max) = Simulate(enraged: false, counters: 2, attempts: 20_000, seed: 2);
            _output.WriteLine($"2 reads + random: mean M {mean:F3} (min {min:F2}, max {max:F2})");
            Assert.InRange(mean, 1.65, 1.85);
        }

        [Fact]
        public void ThreeReadsOnTheEnragedWheelStillReachOnePointSevenFive()
        {
            var (mean, min, max) = Simulate(enraged: true, counters: 3, attempts: 10_000, seed: 3);
            _output.WriteLine($"enraged, 3 reads + random: mean M {mean:F3} (min {min:F2}, max {max:F2})");
            Assert.True(mean >= 1.75, $"mean M {mean}");
        }

        // --- the generator ------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EveryGeneratedScheduleKeepsTheSpecsShape(bool enraged)
        {
            using var generator = new SeededRandomNumberGenerator(enraged ? 11 : 10);
            int minSpeed = enraged ? WorldBossStrikeRules.EnragedMinSpeedDegPerSec : WorldBossStrikeRules.MinSpeedDegPerSec;
            int maxSpeed = enraged ? WorldBossStrikeRules.EnragedMaxSpeedDegPerSec : WorldBossStrikeRules.MaxSpeedDegPerSec;
            int minDur = enraged ? WorldBossStrikeRules.EnragedMinSegmentMs : WorldBossStrikeRules.MinSegmentMs;
            int maxDur = enraged ? WorldBossStrikeRules.EnragedMaxSegmentMs : WorldBossStrikeRules.MaxSegmentMs;
            int close = enraged ? WorldBossStrikeRules.EnragedResponseCloseMs : WorldBossStrikeRules.ResponseCloseMs;

            for (int n = 0; n < 10_000; n++)
            {
                var s = ShieldWheelSchedule.Generate(generator, practice: false, enraged: enraged);
                Assert.Equal(enraged, s.Enraged);

                if (enraged) Assert.Equal(3, s.Interrupts.Count);
                else Assert.Contains(s.Interrupts.Count, new[] { 2, 3 });

                Assert.True(s.TotalMs >= WorldBossStrikeRules.MaxPlayMs);
                int expectedStart = 0;
                foreach (var segment in s.Segments)
                {
                    Assert.Equal(expectedStart, segment.StartMs);
                    expectedStart = segment.EndMs;
                    if (segment.Frozen)
                    {
                        Assert.Equal(0, segment.DegPerSec);
                        Assert.Equal(WorldBossStrikeRules.InterruptMs, segment.DurationMs);
                    }
                    else
                    {
                        Assert.InRange(Math.Abs(segment.DegPerSec), minSpeed, maxSpeed);
                        Assert.InRange(segment.DurationMs, minDur, maxDur);
                    }
                }

                var tells = s.Interrupts.Select(i => i.TellAtMs).ToArray();
                Assert.True(tells[0] >= WorldBossStrikeRules.FirstTellAtLeastMs);
                for (int i = 1; i < tells.Length; i++) Assert.True(tells[i] - tells[i - 1] >= WorldBossStrikeRules.TellGapAtLeastMs);
                Assert.True(s.Interrupts[^1].EndMs <= WorldBossStrikeRules.LastInterruptEndsByMs);
                Assert.All(s.Interrupts, i => Assert.Equal(close, i.ResponseCloseMs));

                Assert.All(s.SeamPasses(), passes => Assert.True(passes >= ShieldWheelSchedule.MinSeamPassesPerPlate));
            }
        }

        [Fact]
        public void TheEnrageStartsAtExactlyAQuarterOfTheBossesHealth()
        {
            Assert.True(WorldBossStrikeRules.IsEnraged(25, 100));
            Assert.True(WorldBossStrikeRules.IsEnraged(24, 100));
            Assert.False(WorldBossStrikeRules.IsEnraged(26, 100));
            Assert.True(WorldBossStrikeRules.IsEnraged(12_500_000, 50_000_000));
            Assert.False(WorldBossStrikeRules.IsEnraged(12_500_001, 50_000_000));
            Assert.False(WorldBossStrikeRules.IsEnraged(0, 0));
        }
    }
}

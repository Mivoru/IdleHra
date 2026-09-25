using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 36: the shield wheel scorer against one hand-built schedule
    /// (Fixtures/shield_wheel_cases.json, which the client's shieldWheel.test.ts
    /// reads too), plus the parry mappings, every shape refusal and the board
    /// rules. Pure: no database, no clock.
    /// </summary>
    public class ShieldWheelScorerTests
    {
        internal static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        internal sealed class FixtureFile
        {
            public ScheduleDto Schedule { get; set; } = new();
            public List<AngleDto> Angles { get; set; } = new();
            public List<CaseDto> Cases { get; set; } = new();
        }

        internal sealed class ScheduleDto
        {
            public double StartAngleDeg { get; set; }
            public bool Practice { get; set; }
            public bool Enraged { get; set; }
            public List<ShieldWheelSegment> Segments { get; set; } = new();
            public List<ShieldWheelInterrupt> Interrupts { get; set; } = new();
            public ShieldWheelSchedule ToSchedule() => new(StartAngleDeg, Segments, Interrupts, Practice, Enraged);
        }

        internal sealed class AngleDto
        {
            public double TMs { get; set; }
            public double Angle { get; set; }
            public int Plate { get; set; }
            public bool Frozen { get; set; }
        }

        internal sealed class CaseDto
        {
            public string Name { get; set; } = string.Empty;
            public List<WheelTap> Taps { get; set; } = new();
            public List<ParryEntry> Parries { get; set; } = new();
            public List<CounterEntry> Counters { get; set; } = new();
            public double ReceivedAfterIssueMs { get; set; }
            public ExpectDto Expect { get; set; } = new();
        }

        internal sealed class ExpectDto
        {
            public SubmissionVerdict Verdict { get; set; }
            public int SpearsLost { get; set; }
            public double? Multiplier { get; set; }
            public List<int>? Suspicions { get; set; }
            public int? RefusalDetail { get; set; }
            public List<SpearLanding> Landings { get; set; } = new();
        }

        internal static FixtureFile LoadFixture()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "shield_wheel_cases.json");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Fixtures", "shield_wheel_cases.json"))) dir = dir.Parent;
                Assert.NotNull(dir);
                path = Path.Combine(dir!.FullName, "Fixtures", "shield_wheel_cases.json");
            }
            return JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(path), Json)!;
        }

        private static readonly FixtureFile Fixture = LoadFixture();
        private static ShieldWheelSchedule Schedule => Fixture.Schedule.ToSchedule();

        public static IEnumerable<object[]> CaseNames() => LoadFixture().Cases.Select(c => new object[] { c.Name });

        [Fact]
        public void TheScheduleIsWhereTheFixtureSaysItIs()
        {
            foreach (var point in Fixture.Angles)
            {
                Assert.True(Math.Abs(Schedule.AngleAt(point.TMs) - point.Angle) < 1e-6, $"angle at {point.TMs}: {Schedule.AngleAt(point.TMs)} not {point.Angle}");
                Assert.Equal(point.Plate, Schedule.PlateAt(point.TMs));
                Assert.Equal(point.Frozen, Schedule.FrozenAt(point.TMs));
            }
        }

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void EveryFixtureCaseScoresAsWorkedOutByHand(string name)
        {
            var c = Fixture.Cases.Single(x => x.Name == name);
            var scored = ShieldWheelScorer.Score(Schedule, new StrikeLog(c.Taps, c.Parries, c.Counters), c.ReceivedAfterIssueMs);

            Assert.Equal(c.Expect.Verdict, scored.Verdict);
            if (c.Expect.RefusalDetail is int detail) Assert.Equal((StrikeSuspicion)detail, scored.RefusalDetail);
            if (c.Expect.Verdict == SubmissionVerdict.Refused) return;

            Assert.Equal(c.Expect.SpearsLost, scored.SpearsLost);
            Assert.Equal(c.Expect.Landings, scored.Landings);
            if (c.Expect.Multiplier is double m) Assert.Equal(m, scored.Multiplier, 9);
            if (c.Expect.Suspicions is { } expected)
            {
                Assert.Equal(expected.OrderBy(x => x), scored.Suspicions.Select(s => (int)s).OrderBy(x => x));
            }
        }

        [Fact]
        public void EveryWrongMappingLosesASpearAndOpensNoCounter()
        {
            int wrongPairs = 0;
            foreach (ParryTell tell in Enum.GetValues<ParryTell>())
            {
                foreach (ParryChoice choice in Enum.GetValues<ParryChoice>())
                {
                    if (choice == ShieldWheelSchedule.CorrectChoice(tell)) continue;
                    wrongPairs++;
                    var schedule = Schedule with
                    {
                        Interrupts = Schedule.Interrupts.Select(i => i.Index == 0 ? i with { Tell = tell } : i).ToList()
                    };

                    var noCounter = ShieldWheelScorer.Score(schedule, new StrikeLog(
                        new[] { new WheelTap(0, 280), new WheelTap(1, 6500) },
                        new[] { new ParryEntry(0, choice, 4300) },
                        Array.Empty<CounterEntry>()), 9600);
                    Assert.Equal(SubmissionVerdict.Accepted, noCounter.Verdict);
                    Assert.Equal(1, noCounter.SpearsLost);

                    // And a counter on a wrong read opens nothing: it is a shape refusal.
                    var withCounter = ShieldWheelScorer.Score(schedule, new StrikeLog(
                        new[] { new WheelTap(0, 280) },
                        new[] { new ParryEntry(0, choice, 4300) },
                        new[] { new CounterEntry(0, 1, 4900, 2) }), 8000);
                    Assert.Equal(SubmissionVerdict.Refused, withCounter.Verdict);
                }
            }
            Assert.Equal(6, wrongPairs);
        }

        [Fact]
        public void AnInterruptThatIsNeverReachedCostsNothing()
        {
            var scored = ShieldWheelScorer.Score(Schedule, new StrikeLog(
                new[] { new WheelTap(0, 280), new WheelTap(1, 1080), new WheelTap(2, 1880), new WheelTap(3, 2840), new WheelTap(4, 3320) },
                Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 7000);
            Assert.Equal(0, scored.SpearsLost);
        }

        [Fact]
        public void SprayingTheWheelEarnsLittle()
        {
            // Evenly spaced taps with no aim, all before the first interrupt
            // (a fifth spear after an unanswered interrupt is one an honest
            // client no longer holds, and the scorer refuses it).
            var taps = new[] { 500.0, 1300, 2100, 2900, 3700 }.Select((t, i) => new WheelTap(i, t)).ToArray();
            var scored = ShieldWheelScorer.Score(Schedule, new StrikeLog(taps, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 7000);
            Assert.Equal(SubmissionVerdict.Accepted, scored.Verdict);
            Assert.InRange(scored.Multiplier, 1.0, 1.6);
        }

        [Fact]
        public void TwoCorrectReadsPlusRandomTapsClearsOnePointFive()
        {
            // Two guaranteed Seams from counters, three unaimed taps. Two seams
            // alone make s = 0.5 and M = 1 + 0.5/0.9 = 1.56 - with Plate worth 0
            // (owner, 2026-09-25) the random taps add nothing unless one lands
            // a seam, so the floor of this case is 1.5. The MEAN over random
            // taps is the ledger's TwoReadsPlusRandomEarnsAboutOnePointSevenFive.
            var taps = new[] { new WheelTap(0, 500), new WheelTap(1, 1500), new WheelTap(3, 7500) };
            var parries = new[] { new ParryEntry(0, ParryChoice.DodgeRight, 4300), new ParryEntry(1, ParryChoice.Block, 10700) };
            var counters = new[] { new CounterEntry(0, 2, 4900, 1), new CounterEntry(1, 4, 11200, 3) };
            var scored = ShieldWheelScorer.Score(Schedule, new StrikeLog(taps, parries, counters), 15000);
            Assert.Equal(SubmissionVerdict.Accepted, scored.Verdict);
            Assert.True(scored.Multiplier >= 1.5, $"M = {scored.Multiplier}");
        }

        public static IEnumerable<object[]> ShapeCases()
        {
            var ok = new[] { new WheelTap(0, 280), new WheelTap(1, 1080) };
            yield return new object[] { "six taps", new StrikeLog(Enumerable.Range(0, 6).Select(i => new WheelTap(i, 280 + 400 * i)).ToArray(), Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "non-increasing times", new StrikeLog(new[] { new WheelTap(0, 1080), new WheelTap(1, 280) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "duplicate Seq", new StrikeLog(new[] { new WheelTap(0, 280), new WheelTap(0, 1080) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "negative time", new StrikeLog(new[] { new WheelTap(0, -5) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "past MaxPlayMs", new StrikeLog(new[] { new WheelTap(0, WorldBossStrikeRules.MaxPlayMs + 1) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 90000.0 };
            yield return new object[] { "two wheel taps 100 ms apart", new StrikeLog(new[] { new WheelTap(0, 280), new WheelTap(1, 380) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "unknown interrupt", new StrikeLog(ok, new[] { new ParryEntry(7, ParryChoice.Block, 4300) }, Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "plate 7", new StrikeLog(ok, new[] { new ParryEntry(0, ParryChoice.DodgeRight, 4300) }, new[] { new CounterEntry(0, 2, 4900, 7) }), 9000.0 };
            yield return new object[] { "NaN", new StrikeLog(new[] { new WheelTap(0, double.NaN) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "1e300", new StrikeLog(new[] { new WheelTap(0, 1e300) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>()), 9000.0 };
            yield return new object[] { "an undefined choice", new StrikeLog(ok, new[] { new ParryEntry(0, (ParryChoice)9, 4300) }, Array.Empty<CounterEntry>()), 9000.0 };
        }

        [Theory]
        [MemberData(nameof(ShapeCases))]
        public void EveryShapeErrorIsARefusalAndNeverAnException(string name, StrikeLog log, double received)
        {
            var scored = ShieldWheelScorer.Score(Schedule, log, received);
            Assert.True(scored.Verdict == SubmissionVerdict.Refused, $"{name} was accepted");
            Assert.Equal(StrikeSuspicion.ShapeRefusal, scored.RefusalDetail);
            Assert.Equal(WorldBossStrikeRules.Floor, scored.Multiplier);
        }

        [Fact]
        public void ALogThatArrivesBeforeItCouldHaveBeenPlayedIsRefusedByTheWallClock()
        {
            var log = new StrikeLog(new[] { new WheelTap(0, 280), new WheelTap(1, 5500) }, Array.Empty<ParryEntry>(), Array.Empty<CounterEntry>());
            // CountdownMs + last tap - 50 = 8450: anything sooner cannot be honest.
            Assert.Equal(StrikeSuspicion.WallClock, ShieldWheelScorer.Score(Schedule, log, 8449).RefusalDetail);
            Assert.Equal(SubmissionVerdict.Accepted, ShieldWheelScorer.Score(Schedule, log, 8450).Verdict);
        }

        [Fact]
        public void PrecisionIsFlaggedButNeverLowersTheScore()
        {
            var perfect = Fixture.Cases.First(c => c.Name.StartsWith("perfect"));
            var scored = ShieldWheelScorer.Score(Schedule, new StrikeLog(perfect.Taps, perfect.Parries, perfect.Counters), perfect.ReceivedAfterIssueMs);
            Assert.Contains(StrikeSuspicion.PreciseWheel, scored.Suspicions);
            Assert.Equal(SubmissionVerdict.Accepted, scored.Verdict);
            Assert.Equal(WorldBossStrikeRules.Cap, scored.Multiplier);
        }

        // --- the board ---------------------------------------------------------

        private static SpearLanding L(int seq, int plate, SpearClass c) => new(seq, plate, c, false);

        [Fact]
        public void TheBreakIsTheFirstNonWeakUnbrokenPlateHitInTapOrder()
        {
            const int weak = 2;
            Assert.Equal(3, WorldBossStrikeRules.BreakTarget(new[] { L(0, 3, SpearClass.Plate), L(1, 4, SpearClass.Seam) }, weak, 0));
            // Weak and broken plates are skipped; a Glance never breaks anything.
            Assert.Equal(4, WorldBossStrikeRules.BreakTarget(new[] { L(0, 1, SpearClass.Glance), L(1, weak, SpearClass.Seam), L(2, 3, SpearClass.Seam), L(3, 4, SpearClass.Plate) }, weak, 1 << 3));
            Assert.Null(WorldBossStrikeRules.BreakTarget(new[] { L(0, 1, SpearClass.Glance), L(1, 0, SpearClass.Glance) }, weak, 0));
            Assert.Null(WorldBossStrikeRules.BreakTarget(new[] { L(0, weak, SpearClass.Seam) }, weak, 0));
        }

        [Fact]
        public void TheWeakPlateIsRevealedOnlyByElimination()
        {
            const int weak = 1;
            int allOthers = (1 << 0) | (1 << 2) | (1 << 3) | (1 << 4);
            Assert.True(WorldBossStrikeRules.RevealByElimination(allOthers, weak));
            Assert.True(WorldBossStrikeRules.RevealByElimination(allOthers | (1 << weak), weak));
            Assert.False(WorldBossStrikeRules.RevealByElimination(allOthers & ~(1 << 3), weak));
            Assert.False(WorldBossStrikeRules.RevealByElimination(0, weak));
        }

        [Fact]
        public void ThePlateMultiplierIsTheMeanOverPlateOrBetterHits()
        {
            const int weak = 0;
            Assert.Equal(3.0, WorldBossStrikeRules.PlateMultiplier(new[] { L(0, weak, SpearClass.Seam), L(1, weak, SpearClass.Plate) }, weak));
            Assert.Equal(1.0, WorldBossStrikeRules.PlateMultiplier(Array.Empty<SpearLanding>(), weak));
            Assert.Equal(1.0, WorldBossStrikeRules.PlateMultiplier(new[] { L(0, weak, SpearClass.Glance) }, weak));
            Assert.Equal(2.0, WorldBossStrikeRules.PlateMultiplier(new[] { L(0, weak, SpearClass.Seam), L(1, 3, SpearClass.Plate) }, weak));
        }

        [Fact]
        public void TheAutoStrikeFloorProtectsAPoorRunThatFoundTheWeakPlate()
        {
            const int weak = 4;
            // Auto: the best plate struck at Plate or better, 1.0 otherwise.
            Assert.Equal(3.0, WorldBossStrikeRules.AutoFloor(new[] { L(0, 1, SpearClass.Plate), L(1, weak, SpearClass.Plate) }, weak));
            Assert.Equal(1.0, WorldBossStrikeRules.AutoFloor(new[] { L(0, weak, SpearClass.Glance) }, weak));
            Assert.Equal(1.0, WorldBossStrikeRules.AutoFloor(Array.Empty<SpearLanding>(), weak));

            // A poor run: one Plate hit on the weak plate, four Plate hits elsewhere.
            // Plate hits score 0 toward skill, so M = 1.0 and M x P = 1.0 x (1+1+1+1+3)/5 = 1.4 < 3.0.
            var poor = new[] { L(0, 0, SpearClass.Plate), L(1, 1, SpearClass.Plate), L(2, weak, SpearClass.Plate), L(3, 2, SpearClass.Plate), L(4, 3, SpearClass.Plate) };
            Assert.True(WorldBossStrikeRules.Played(poor, weak) >= 3.0);

            // A strong run pays M x P when that is larger than the floor.
            var strong = new[] { L(0, weak, SpearClass.Seam), L(1, weak, SpearClass.Seam), L(2, weak, SpearClass.Seam), L(3, weak, SpearClass.Seam) };
            Assert.Equal(6.0, WorldBossStrikeRules.Played(strong, weak), 9);
        }
    }
}

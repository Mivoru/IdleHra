using System;
using System.IO;
using System.Linq;
using FolkIdle.Server.Domain.Progression;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE AGE CURVE, AND WHY IT IS ONE OBJECT NOW.
    ///
    /// The thresholds used to be four bare literals in SimulationEngine
    /// .ProcessAgeSlot and the same four again in OfflineSimulationEngine, with
    /// a comment in the second saying it "mirrors the exact thresholds" of the
    /// first - which is a promise a comment cannot keep. Two copies of one
    /// truth is this codebase's dominant bug class, so the copy is gone and
    /// this file fails if it comes back.
    ///
    /// The numbers themselves were retuned at the same time. A fielded
    /// character reached Old - a permanent -20% to damage, health and attack
    /// speed - after THREE HOURS, and the only way back was to breed a
    /// successor. Breeding gated on a character level column that nothing in
    /// the server ever wrote, so the recovery half of that loop had never once
    /// worked and every account on the live box had been decaying into it.
    /// </summary>
    public class AgePhaseCurveTests
    {
        [Theory]
        [InlineData(0L, 0)]
        [InlineData(35_999L, 0)]
        [InlineData(36_000L, 1)]
        [InlineData(1_439_999L, 1)]
        [InlineData(1_440_000L, 2)]
        [InlineData(2_879_999L, 2)]
        [InlineData(2_880_000L, 3)]
        [InlineData(long.MaxValue, 3)]
        public void PhaseForLandsOnTheDocumentedBoundaries(long ticks, int expected)
        {
            Assert.Equal(expected, AgePhaseCurve.PhaseFor(ticks));
        }

        [Fact]
        public void OneHourIsThirtySixThousandTicksAtTenHertz()
        {
            // The live tick is 100 ms and increments AgeTicks by one, so this
            // constant is the bridge between "ticks" and anything a player or a
            // designer can reason about. Every other number here is stated in
            // hours against it.
            Assert.Equal(36_000L, AgePhaseCurve.TicksPerHour);
            Assert.Equal(1L * AgePhaseCurve.TicksPerHour, AgePhaseCurve.ChildEndTicks);
            Assert.Equal(40L * AgePhaseCurve.TicksPerHour, AgePhaseCurve.AdultEndTicks);
            Assert.Equal(80L * AgePhaseCurve.TicksPerHour, AgePhaseCurve.SeniorEndTicks);
        }

        [Theory]
        [InlineData(0, 1.0f)]
        [InlineData(1, 1.0f)]
        [InlineData(2, 0.95f)]
        [InlineData(3, 0.90f)]
        public void PenaltiesAreTheStretchedOnes(int phase, float expected)
        {
            Assert.Equal(expected, AgePhaseCurve.PenaltyMultiplier(phase));
        }

        [Fact]
        public void AnUnknownPhaseIsNotAPenalty()
        {
            // Defensive: the phase arrives from a payload and a database column,
            // and a value outside 0-3 would otherwise multiply a character's
            // whole stat line by zero.
            Assert.Equal(1.0f, AgePhaseCurve.PenaltyMultiplier(-1));
            Assert.Equal(1.0f, AgePhaseCurve.PenaltyMultiplier(9));
        }

        /// <summary>
        /// THE GUARD THAT MATTERS. Both aging paths must go through the curve,
        /// and neither may carry a threshold of its own.
        /// </summary>
        [Fact]
        public void NeitherAgingPathCarriesItsOwnThresholds()
        {
            string live = File.ReadAllText(LocateSource("Domain", "Combat", "SimulationEngine.cs"));
            string offline = File.ReadAllText(LocateSource("Engine", "OfflineSimulationEngine.cs"));

            // The PHASE BOUNDARIES only, old and new. A bare 36000 is not on
            // this list on purpose: it is "one hour in ticks" and the Town Hall
            // divides its gold accumulator by it, legitimately and with nothing
            // to do with aging. Guarding that number would force an unrelated
            // feature to import the age curve to keep a test quiet, which is
            // how a guard turns into a liability.
            foreach (string boundary in new[] { "72000", "108000", "1_440_000", "2_880_000" })
            {
                Assert.DoesNotContain(boundary, live);
                Assert.DoesNotContain(boundary, offline);
            }

            Assert.Contains("AgePhaseCurve.PhaseFor", live);
            Assert.Contains("AgePhaseCurve.PhaseFor", offline);
        }

        /// <summary>
        /// And the stat penalty is the curve's too - StatsCalculator carried
        /// its own 0.9f / 0.8f pair, which is a third copy of the same design
        /// decision in a third file.
        /// </summary>
        [Fact]
        public void TheStatPenaltyComesFromTheCurve()
        {
            string stats = File.ReadAllText(LocateSource("Engine", "StatsCalculator.cs"));
            Assert.Contains("AgePhaseCurve.PenaltyMultiplier", stats);
        }

        /// <summary>
        /// The reporting player's own character, as it stood in the live
        /// database when this was written: 2,137,634 ticks, about 59 hours
        /// fielded, sitting at a permanent -20%. It becomes a -5% Senior, and
        /// it does so with no migration - ProcessAgeSlot recomputes the phase
        /// from AgeTicks on every tick, so the curve is the only input.
        /// </summary>
        [Fact]
        public void TheLiveAccountThatReportedThisGetsItsPowerBack()
        {
            Assert.Equal(3, LegacyPhaseFor(2_137_634L));
            Assert.Equal(2, AgePhaseCurve.PhaseFor(2_137_634L));
            Assert.True(AgePhaseCurve.PenaltyMultiplier(2) > 0.80f);
        }

        /// <summary>
        /// A GRANTED ADULT MUST DERIVE AS AN ADULT.
        ///
        /// CharacterGrantEngine wrote `AgePhase = 1, AgeTicks = 0` - two fields
        /// that disagreed, on a phase that ProcessAgeSlot recomputes from the
        /// ticks on every single tick. So a new account's founder, and the
        /// male/female pair a region boss grants, were demoted to CHILD the
        /// instant they were fielded and could not breed for an hour, with
        /// nothing on any screen saying why.
        ///
        /// The stored AgeTicks is the durable half; the phase beside it is a
        /// cache. This asserts the two cannot disagree again.
        /// </summary>
        [Fact]
        public void AGrantedAdultIsStillAnAdultOnTheNextTick()
        {
            Assert.Equal(AgePhaseCurve.Adult, AgePhaseCurve.PhaseFor(AgePhaseCurve.ChildEndTicks));

            string grant = File.ReadAllText(LocateSource("Engine", "CharacterGrantEngine.cs"));
            Assert.Contains("AgeTicks = AgePhaseCurve.ChildEndTicks", grant);
            Assert.DoesNotContain("AgeTicks = 0L,", grant);

            string fixture = File.ReadAllText(LocateSource("Models", "DevFixtureSeeder.cs"));
            Assert.Contains("AgeTicks = AgePhaseCurve.ChildEndTicks", fixture);
        }

        /// <summary>
        /// And a BRED child genuinely starts at zero - it is supposed to grow up.
        /// </summary>
        [Fact]
        public void ANewbornStillStartsAsAChild()
        {
            Assert.Equal(AgePhaseCurve.Child, AgePhaseCurve.PhaseFor(0));

            string breeding = File.ReadAllText(LocateSource("Engine", "BreedingEngine.cs"));
            Assert.Contains("AgePhase = 0,", breeding);
        }

        private static int LegacyPhaseFor(long ageTicks)
            => ageTicks >= 108_000L ? 3 : ageTicks >= 72_000L ? 2 : ageTicks >= 36_000L ? 1 : 0;

        private static string LocateSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName, "FolkIdle.Server" }.Concat(parts).ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(string.Join("/", parts) + " not found from " + AppContext.BaseDirectory);
        }
    }
}

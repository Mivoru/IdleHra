using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The village as a gene pool: who turns up, how often, and how many fit.
    ///
    /// This is the only thing that moves a bloodline meaningfully - inheritance
    /// takes one parent's value and mutation drifts at +0.15 a generation, so
    /// outside blood is the road and the village is where it comes from.
    /// </summary>
    public class VillagerArrivalTests
    {
        private const int Hour = 3600;
        private const int Day = 24 * Hour;
        private const long SeasonSeconds = 90L * Day;

        private readonly ITestOutputHelper _output;

        public VillagerArrivalTests(ITestOutputHelper output) => _output = output;

        // --- the interval -------------------------------------------------------

        [Fact]
        public void ABetterInnBringsPeopleSooner()
        {
            int poor = VillagerArrivalRules.IntervalSecondsFor(0);
            int good = VillagerArrivalRules.IntervalSecondsFor(6);

            Assert.Equal(48 * Hour, poor);
            Assert.Equal(36 * Hour, good);
            Assert.True(good < poor);
        }

        [Fact]
        public void TheIntervalNeverFallsBelowItsFloor()
        {
            foreach (int innLevel in new[] { 12, 20, 100, 10_000 })
            {
                Assert.Equal(
                    VillagerArrivalRules.MinimumIntervalSeconds,
                    VillagerArrivalRules.IntervalSecondsFor(innLevel));
            }
        }

        [Fact]
        public void ANegativeInnLevelIsTreatedAsNone()
        {
            Assert.Equal(
                VillagerArrivalRules.IntervalSecondsFor(0),
                VillagerArrivalRules.IntervalSecondsFor(-5));
        }

        /// <summary>
        /// A season has to offer enough rolls of the dice that hunting a good
        /// villager is possible, without making one free.
        /// </summary>
        [Fact]
        public void ASeasonOffersEnoughRollsToHuntWith()
        {
            int poor = (int)(SeasonSeconds / VillagerArrivalRules.IntervalSecondsFor(0));
            int good = (int)(SeasonSeconds / VillagerArrivalRules.IntervalSecondsFor(20));

            _output.WriteLine($"arrivals in a 90-day season: Inn 0 -> {poor}, Inn 20 -> {good}");
            Assert.InRange(poor, 40, 50);
            Assert.InRange(good, 80, 100);
        }

        // --- the cap --------------------------------------------------------------

        [Fact]
        public void TheVillageGrowsWithTheInnAndThenStops()
        {
            Assert.Equal(6, VillagerArrivalRules.PopulationCapFor(0));
            Assert.Equal(11, VillagerArrivalRules.PopulationCapFor(5));
            Assert.Equal(VillagerArrivalRules.AbsolutePopulationCap, VillagerArrivalRules.PopulationCapFor(50));
        }

        // --- the arrival tick -------------------------------------------------------

        [Fact]
        public void NobodyArrivesBeforeTheIntervalIsUp()
        {
            var (arrivals, consumed) = VillagerArrivalRules.ArrivalsSince(47 * Hour, innLevel: 0, currentPopulation: 0);
            Assert.Equal(0, arrivals);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void OneArrivesPerInterval()
        {
            var (one, _) = VillagerArrivalRules.ArrivalsSince(48 * Hour, 0, 0);
            var (three, _) = VillagerArrivalRules.ArrivalsSince(3 * 48 * Hour, 0, 0);

            Assert.Equal(1, one);
            Assert.Equal(3, three);
        }

        /// <summary>
        /// The remainder has to carry forward. Consuming the whole elapsed
        /// window instead would quietly lengthen every interval by up to one
        /// tick's worth - over ninety days, a village several people short.
        /// </summary>
        [Fact]
        public void ThePartialIntervalIsNotThrownAway()
        {
            var (arrivals, consumed) = VillagerArrivalRules.ArrivalsSince(50 * Hour, 0, 0);

            Assert.Equal(1, arrivals);
            Assert.Equal(48 * Hour, consumed);
        }

        [Fact]
        public void ArrivalsStopAtTheCapRatherThanOverfilling()
        {
            int cap = VillagerArrivalRules.PopulationCapFor(0);
            var (arrivals, _) = VillagerArrivalRules.ArrivalsSince(365L * Day, 0, currentPopulation: cap - 2);

            Assert.Equal(2, arrivals);
        }

        /// <summary>
        /// A FULL VILLAGE STOPS THE CLOCK. Banking arrivals while full would
        /// mean dismissing one villager instantly conjures the dozen you
        /// "missed" - which turns the decision the cap exists to create into a
        /// formality.
        /// </summary>
        [Fact]
        public void AFullVillageBanksNothing()
        {
            int cap = VillagerArrivalRules.PopulationCapFor(3);
            var (arrivals, consumed) = VillagerArrivalRules.ArrivalsSince(365L * Day, 3, currentPopulation: cap);

            Assert.Equal(0, arrivals);
            // The whole window is consumed, so freeing a slot starts a fresh wait.
            Assert.Equal(365L * Day, consumed);
        }

        [Fact]
        public void NoTimeMeansNoArrivals()
        {
            Assert.Equal((0, 0L), VillagerArrivalRules.ArrivalsSince(0, 5, 0));
            Assert.Equal((0, 0L), VillagerArrivalRules.ArrivalsSince(-100, 5, 0));
        }

        // --- recruitment ---------------------------------------------------------------

        [Fact]
        public void TheFirstRecruitIsTheCheapestAndEachCostsMore()
        {
            long previous = 0;
            for (int n = 0; n < 12; n++)
            {
                long cost = VillagerArrivalRules.RecruitCostGold(n);
                Assert.True(cost > previous, $"recruitment {n} did not cost more than {n - 1}");
                previous = cost;
            }

            Assert.Equal(VillagerArrivalRules.RecruitBaseGold, VillagerArrivalRules.RecruitCostGold(0));
            _output.WriteLine($"recruit 0/3/6/9: {VillagerArrivalRules.RecruitCostGold(0):N0}"
                + $" / {VillagerArrivalRules.RecruitCostGold(3):N0}"
                + $" / {VillagerArrivalRules.RecruitCostGold(6):N0}"
                + $" / {VillagerArrivalRules.RecruitCostGold(9):N0}");
        }

        /// <summary>
        /// 1.6^n passes long.MaxValue around the ninetieth recruitment, and a
        /// wrapped price is a free one. Nobody will recruit ninety times in a
        /// season - which is exactly why this would never be found in play.
        /// </summary>
        [Fact]
        public void AnAbsurdRecruitCountDoesNotWrapIntoBeingFree()
        {
            long previous = 0;
            foreach (int n in new[] { 50, 80, 100, 500, 5000 })
            {
                long cost = VillagerArrivalRules.RecruitCostGold(n);
                Assert.True(cost > 0, $"recruitment {n} cost {cost}");
                Assert.True(cost >= previous, "the price went backwards");
                previous = cost;
            }
        }

        [Fact]
        public void RecruitingIsRefusedWhenFullOrTooPoorAndSaysWhich()
        {
            int cap = VillagerArrivalRules.PopulationCapFor(2);

            string? full = VillagerArrivalRules.RecruitBlockedReason(2, cap, 999_999_999, 0);
            Assert.NotNull(full);
            Assert.Contains("full", full);

            string? broke = VillagerArrivalRules.RecruitBlockedReason(2, 0, 10, 0);
            Assert.NotNull(broke);
            Assert.Contains("gold", broke);

            Assert.Null(VillagerArrivalRules.RecruitBlockedReason(2, 0, 999_999_999, 0));
        }

        // --- the two-phase climb, end to end ------------------------------------------------

        /// <summary>
        /// The property the whole village-as-gene-pool design rests on: however
        /// good the Inn, village blood tops out at twenty, and everything above
        /// that is generations of selection. Without this the village would
        /// short-circuit the veteran axis entirely.
        /// </summary>
        [Fact]
        public void HoweverGoodTheInnTheVillageStopsAtTwenty()
        {
            var rng = new Random(20260808);
            int best = 0;

            for (int innLevel = 0; innLevel <= 60; innLevel += 4)
            {
                for (int i = 0; i < 500; i++)
                {
                    foreach (int value in BreedingAptitudes.RollVillager(innLevel, rng))
                    {
                        if (value > best) best = value;
                        Assert.InRange(value, 2, BreedingAptitudes.VillagerCeiling);
                    }
                }
            }

            _output.WriteLine($"best villager aptitude seen across all Inn levels: {best}");
            Assert.Equal(BreedingAptitudes.VillagerCeiling, best);
        }
    }
}

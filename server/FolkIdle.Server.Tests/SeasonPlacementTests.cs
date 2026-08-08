using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// What finishing a season in a given place is worth.
    ///
    /// The rollover already paid for what a player ACCUMULATED - gold, levels,
    /// gear. This pays for where they FINISHED, which is a different thing and
    /// the only reason a leaderboard is worth looking at twice.
    /// </summary>
    public class SeasonPlacementTests
    {
        private readonly ITestOutputHelper _output;

        public SeasonPlacementTests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(1, 2000)]
        [InlineData(2, 1200)]
        [InlineData(3, 1200)]
        [InlineData(4, 600)]
        [InlineData(10, 600)]
        [InlineData(11, 250)]
        [InlineData(50, 250)]
        [InlineData(51, 100)]
        [InlineData(100, 100)]
        [InlineData(101, 25)]
        [InlineData(9999, 25)]
        public void TheBandsPayWhatTheySay(int rank, int expected)
        {
            Assert.Equal(expected, SeasonPlacementRewards.DiamondsForRank(rank));
        }

        /// <summary>
        /// The property that matters more than any single number: finishing
        /// higher is never worth less. A table edited by hand is exactly where
        /// an inversion hides, and an inversion here would mean a player who
        /// climbed the board was punished for it.
        /// </summary>
        [Fact]
        public void FinishingHigherIsNeverWorthLess()
        {
            int previous = int.MaxValue;
            for (int rank = 1; rank <= 500; rank++)
            {
                int reward = SeasonPlacementRewards.DiamondsForRank(rank);
                Assert.True(reward <= previous, $"rank {rank} pays more than rank {rank - 1}");
                previous = reward;
            }
        }

        /// <summary>
        /// Everyone who finished the season on the board gets something. A
        /// ladder that pays only the top three tells everyone else their
        /// season did not count.
        /// </summary>
        [Fact]
        public void EveryRankedPlayerGetsSomething()
        {
            Assert.True(SeasonPlacementRewards.DiamondsForRank(100_000) > 0);
        }

        /// <summary>
        /// Unranked is not last place. A quarantined account, or one that
        /// never played, did not finish a season - and paying the
        /// participation band for it would make the reward automatic.
        /// </summary>
        [Fact]
        public void UnrankedPaysNothing()
        {
            Assert.Equal(0, SeasonPlacementRewards.DiamondsForRank(0));
            Assert.Equal(0, SeasonPlacementRewards.DiamondsForRank(-1));
        }

        [Fact]
        public void EveryBandHasAName()
        {
            foreach (int rank in new[] { 1, 2, 4, 20, 75, 500 })
            {
                string band = SeasonPlacementRewards.BandNameForRank(rank);
                _output.WriteLine($"#{rank,-5} {SeasonPlacementRewards.DiamondsForRank(rank),5} diamonds  {band}");
                Assert.False(string.IsNullOrWhiteSpace(band));
            }

            Assert.Equal(string.Empty, SeasonPlacementRewards.BandNameForRank(0));
        }

        /// <summary>
        /// A sanity check against the economy rather than against itself: the
        /// top prize has to be worth a season. Inheritance levels cost
        /// hundreds of diamonds each, so 2,000 is a few levels of permanent
        /// progress - enough that placing changes the next season, which is
        /// the whole point of paying in the currency that survives the
        /// rollover.
        /// </summary>
        [Fact]
        public void TheTopPrizeIsWorthASeason()
        {
            Assert.InRange(SeasonPlacementRewards.DiamondsForRank(1), 1000, 5000);
        }
    }
}

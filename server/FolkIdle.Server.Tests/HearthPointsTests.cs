using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The craft-point rate (owner decision D2, 2026-10-30): one point per ten
    /// summed gathering mastery levels, derived from mastery and never stored.
    /// </summary>
    public class HearthPointsTests
    {
        [Theory]
        // The three production accounts measured on 2026-10-30.
        [InlineData(367, 375, 190, 0, 93)]
        [InlineData(349, 288, 256, 0, 89)]
        [InlineData(250, 235, 316, 0, 80)]
        // A new account, and the rounding edge.
        [InlineData(0, 0, 0, 0, 0)]
        [InlineData(3, 3, 3, 0, 0)]
        [InlineData(4, 3, 3, 0, 1)]
        // Herbalism counts once it can move.
        [InlineData(0, 0, 0, 20, 2)]
        public void OnePointPerTenSummedGatheringLevels(int wood, int mine, int fish, int herb, int expected)
        {
            Assert.Equal(expected, HearthPoints.Earned(wood, mine, fish, herb));
        }

        [Fact]
        public void ThePayloadOverloadReadsTheFourMasteryFields()
        {
            var payload = new TickStatePayload
            {
                WoodcuttingMasteryLevel = 367,
                MiningMasteryLevel = 375,
                FishingMasteryLevel = 190,
                HerbalismMasteryLevel = 8,
            };
            Assert.Equal(94, HearthPoints.Earned(in payload));
        }

        [Fact]
        public void ANegativeOrHugeLevelCannotBreakTheCount()
        {
            Assert.Equal(0, HearthPoints.Earned(-50, -50, 0, 0));
            Assert.True(HearthPoints.Earned(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue) > 0);
        }
    }
}

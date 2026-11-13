using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The Ancestral Boons (LegacyPerkResolver, 2026-11-13): what Legacy Shards
    /// buy. Pure arithmetic, so it runs without a database.
    /// </summary>
    public class AncestralBoonsTests
    {
        private readonly ITestOutputHelper _output;

        public AncestralBoonsTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void EachBoonStopsAtItsCap_AndTheReadIsClamped()
        {
            Assert.Equal(25, LegacyPerkResolver.MaxRankFor(LegacyPerkResolver.XpMultiplierBitOffset));
            Assert.Equal(25, LegacyPerkResolver.MaxRankFor(LegacyPerkResolver.GoldDropRateBitOffset));
            Assert.Equal(10, LegacyPerkResolver.MaxRankFor(LegacyPerkResolver.CombatSpeedBitOffset));

            // A rank written past the cap (the old rules allowed 50) pays the cap.
            long perks = LegacyPerkResolver.SetPerkRank(0L, LegacyPerkResolver.CombatSpeedBitOffset, 40);
            Assert.Equal(10, LegacyPerkResolver.GetCombatSpeedBonusPct(perks));

            Assert.Equal(int.MaxValue, LegacyPerkResolver.CalculatePerkRankCost(LegacyPerkResolver.XpMultiplierBitOffset, 25));
            Assert.Equal(int.MaxValue, LegacyPerkResolver.CalculatePerkRankCost(LegacyPerkResolver.CombatSpeedBitOffset, 10));
        }

        [Fact]
        public void PricesRiseWithRank()
        {
            foreach (int offset in new[] { LegacyPerkResolver.XpMultiplierBitOffset, LegacyPerkResolver.GoldDropRateBitOffset, LegacyPerkResolver.CombatSpeedBitOffset })
            {
                int previous = 0;
                for (int r = 0; r < LegacyPerkResolver.MaxRankFor(offset); r++)
                {
                    int cost = LegacyPerkResolver.CalculatePerkRankCost(offset, r);
                    Assert.True(cost > previous, $"offset {offset} rank {r}: {cost} <= {previous}");
                    previous = cost;
                }
            }
            Assert.Equal(200, LegacyPerkResolver.CalculatePerkRankCost(LegacyPerkResolver.XpMultiplierBitOffset, 0));
            Assert.Equal(500, LegacyPerkResolver.CalculatePerkRankCost(LegacyPerkResolver.CombatSpeedBitOffset, 0));
        }

        /// <summary>
        /// THE DESIGN NUMBER: maxing all three costs about twenty level-90
        /// rebirths, and the banked balances measured on production (40k-55k)
        /// buy a good share but not everything.
        /// </summary>
        [Fact]
        public void MaxingEveryBoonCostsAboutTwentyLevelNinetyRebirths()
        {
            long total = LegacyPerkResolver.TotalCostToMax(LegacyPerkResolver.XpMultiplierBitOffset)
                + LegacyPerkResolver.TotalCostToMax(LegacyPerkResolver.GoldDropRateBitOffset)
                + LegacyPerkResolver.TotalCostToMax(LegacyPerkResolver.CombatSpeedBitOffset);
            double rebirths = (double)total / RebirthRules.ShardsForLevel(90);
            _output.WriteLine($"all boons: {total} shards = {rebirths:0.0} level-90 rebirths");

            Assert.InRange(total, 70_000L, 80_000L);
            Assert.True(total > 55_455L, "the largest production balance must not max everything at once");
        }
    }
}

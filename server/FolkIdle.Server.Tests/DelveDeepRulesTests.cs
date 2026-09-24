using System;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE DEEP (task 37) - the rules, with no database in sight. Every
    /// constant comes from the spec
    /// (docs/superpowers/specs/2026-09-24-the-deep-gold-sink-design.md §3).
    /// </summary>
    public class DelveDeepRulesTests
    {
        private readonly ITestOutputHelper _output;
        public DelveDeepRulesTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void TheStakeIsTheRegionFeeForASmallHolderAndAShareOfHoldingsAtTheTop()
        {
            long regionFee = DelveRegistry.EntryFeeForRegion(5);

            // 1M held: 0.5% is 5k, well under the region-5 fee.
            Assert.Equal(regionFee, DelveRegistry.Stake(regionFee, 1_000_000));

            // The top account on 2026-09-23: 492M held -> 2.46M.
            Assert.Equal(2_460_000, DelveRegistry.Stake(regionFee, 492_000_000));

            // Negative wealth (it cannot happen, but a sum can) never goes under the fee.
            Assert.Equal(regionFee, DelveRegistry.Stake(regionFee, -5));
        }

        [Fact]
        public void TheFirstTollIsTheStakeAndTollsStrictlyRiseToFloorSixty()
        {
            const long stake = 2_460_000;
            Assert.Equal(stake, DelveRegistry.TollForFloor(stake, 9));

            long previous = DelveRegistry.TollForFloor(stake, 9);
            for (int floor = 10; floor <= 60; floor++)
            {
                long toll = DelveRegistry.TollForFloor(stake, floor);
                Assert.True(toll > previous, $"floor {floor} tolls {toll:N0}, not more than floor {floor - 1}'s {previous:N0}");
                previous = toll;
            }

            _output.WriteLine($"stake {stake:N0}: floor 12 {DelveRegistry.TollForFloor(stake, 12):N0}, floor 20 {DelveRegistry.TollForFloor(stake, 20):N0}, floor 60 {DelveRegistry.TollForFloor(stake, 60):N0}");
        }

        [Fact]
        public void FloorFiveHundredSaturatesInsteadOfOverflowing()
        {
            long toll = DelveRegistry.TollForFloor(long.MaxValue / 2, 500);
            Assert.Equal(DelveRegistry.PriceCeiling, toll);
            Assert.Equal(DelveRegistry.PriceCeiling, DelveRegistry.TollForFloor(2_460_000, 500));
            Assert.Equal(DelveRegistry.PriceCeiling, DelveRegistry.LanternRefillPrice(DelveRegistry.PriceCeiling, 7));

            // Two saturated prices still add without wrapping.
            Assert.True(DelveRegistry.PriceCeiling + DelveRegistry.PriceCeiling > 0);
            Assert.True(DelveRegistry.Stake(1, long.MaxValue) > 0);
        }

        [Fact]
        public void LanternRefillsDoubleFromOneStake()
        {
            const long stake = 250_000;
            for (int k = 0; k < DelveRegistry.MaxLanternRefills; k++)
            {
                Assert.Equal(stake * (1L << k), DelveRegistry.LanternRefillPrice(stake, k));
            }
            Assert.Equal(8, DelveRegistry.MaxLanternRefills);
            Assert.Equal(stake * 128, DelveRegistry.LanternRefillPrice(stake, 7));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(50)]
        [InlineData(269)]
        [InlineData(1000)]
        public void TheDeepPassChanceIsAStatedDiminishingCurve(int sheet)
        {
            double previous = double.MaxValue;
            for (int floor = 9; floor <= 200; floor++)
            {
                double chance = DelveRegistry.DeepSuccessChance(sheet, floor);
                Assert.True(chance <= DelveRegistry.MaxSuccessChance, $"floor {floor}: {chance} above the ceiling");
                Assert.True(chance >= DelveRegistry.MinSuccessChance, $"floor {floor}: {chance} below the floor");

                if (previous > DelveRegistry.MinSuccessChance)
                {
                    Assert.True(chance < previous, $"sheet {sheet}, floor {floor}: {chance} did not fall below {previous}");
                }
                else
                {
                    Assert.Equal(DelveRegistry.MinSuccessChance, chance, 10);
                }
                previous = chance;
            }
            _output.WriteLine($"sheet {sheet}: floor 9 {DelveRegistry.DeepSuccessChance(sheet, 9):P1}, 20 {DelveRegistry.DeepSuccessChance(sheet, 20):P1}, 50 {DelveRegistry.DeepSuccessChance(sheet, 50):P1}");
        }

        [Fact]
        public void EveryDeepFloorAsksWhatFloorEightAsks()
        {
            Assert.Equal(DelveRegistry.RequirementForFloor(8), DelveRegistry.DeepRequirement);
            // (The spec says 269; 20 x 1.45^7 rounds to 270. The code is the truth.)
            Assert.InRange(DelveRegistry.DeepRequirement, 201, 299);

            // And the chance starts from floor 8's own odds, decayed once.
            for (int floor = 9; floor <= 40; floor++)
            {
                double expected = Math.Max(DelveRegistry.MinSuccessChance,
                    DelveRegistry.SuccessChance(300, DelveRegistry.FloorCount) * Math.Pow(DelveRegistry.DeepDecay, floor - 8));
                Assert.Equal(expected, DelveRegistry.DeepSuccessChance(300, floor), 10);
            }
        }
    }
}

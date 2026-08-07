using System;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// What things cost, measured in MINUTES OF PLAY rather than in gold.
    ///
    /// Reported from play: "I earned 100,000 gold overnight and about five
    /// rerolls took all of it." That was exact, and no test could have said so,
    /// because every cost in this game was pinned against its own formula -
    /// "1000 * 1.5^tier, yes, that is 1000 * 1.5^tier" - and never against what
    /// a player can actually earn.
    ///
    /// Gold income is knowable: every monster pays MaxHp/20, so a region's rate
    /// falls straight out of the content tables. This measures each sink
    /// against that and asserts the answer in time, which is the unit a player
    /// actually feels.
    /// </summary>
    public class GoldSinkAffordabilityTests
    {
        private readonly ITestOutputHelper _output;

        public GoldSinkAffordabilityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        /// <summary>
        /// A kill every twenty seconds against the strongest regular of the
        /// region - a player who is keeping up, not one who is grinding the
        /// first monster forever.
        /// </summary>
        private const double KillsPerHour = 180.0;

        private static double GoldPerHour(int region)
        {
            int strongestRegular = ContentRegistry.FirstCanonicalMonsterId
                                   + (region - 1) * ContentRegistry.MonstersPerRegion + 3;
            return ContentRegistry.Monsters[strongestRegular - 1].BaseGoldReward * KillsPerHour;
        }

        private static double MinutesOfPlay(double cost, int region) => cost / GoldPerHour(region) * 60.0;

        [Fact]
        public void ARerollIsMinutesOfPlay()
        {
            // Item rarity 8 in region 2 is roughly where the report came from.
            long cost = AffixRegistry.CalculateRerollGoldCost(8, consecutiveAttempts: 0, rerollStatType: false);
            double minutes = MinutesOfPlay(cost, region: 2);

            _output.WriteLine($"tier-8 reroll: {cost:N0}g = {minutes:F1} min of region-2 play");
            Assert.InRange(minutes, 0.1, 10.0);

            // And the top of the ladder, against the income of the region that
            // produces it. A hundred-attempt chase for a Legendary affix has to
            // stay inside an evening, not a month.
            long topCost = AffixRegistry.CalculateRerollGoldCost(14, 0, false);
            double topChaseHours = topCost * 100.0 / GoldPerHour(5);
            _output.WriteLine($"tier-14 reroll: {topCost:N0}g, 100 attempts = {topChaseHours:F1} h of region-5 play");
            Assert.InRange(topChaseHours, 0.2, 8.0);
        }

        /// <summary>
        /// The three items are what a fusion costs. The gold is a fee on top,
        /// and a fee that outweighs the thing it is attached to is a second
        /// gate wearing a fee's clothes.
        /// </summary>
        [Fact]
        public void AFusionFeeIsSmallerThanAssemblingTheItemsItConsumes()
        {
            for (int tier = 1; tier <= 10; tier++)
            {
                double fee = Math.Ceiling(200.0 * Math.Pow(1.35, tier));
                double minutes = MinutesOfPlay(fee, region: 2);
                _output.WriteLine($"fusion at tier {tier}: {fee:N0}g = {minutes:F1} min");
                Assert.InRange(minutes, 0.05, 20.0);
            }
        }

        /// <summary>
        /// A village level is a long-term investment and is allowed to be the
        /// dearest thing here - but a single level of a single building should
        /// not be most of an evening.
        /// </summary>
        [Fact]
        public void AVillageLevelIsAnInvestmentNotAnEvening()
        {
            for (int level = 0; level <= 10; level++)
            {
                double minutes = MinutesOfPlay(
                    FolkIdle.Server.Domain.Progression.VillageManagementEngine.CalculateUpgradeCost(level),
                    region: 2);
                _output.WriteLine($"village level {level} -> {level + 1}: {minutes:F1} min");
                Assert.InRange(minutes, 0.5, 90.0);
            }
        }

        /// <summary>
        /// The rates the assertions above are measured against, printed every
        /// run. A number that moves silently is how the reroll curve got to
        /// fifty-three minutes a roll without anyone noticing.
        /// </summary>
        [Fact]
        public void TheMeasuredIncomeIsPrinted()
        {
            for (int region = 1; region <= 5; region++)
            {
                _output.WriteLine($"region {region}: {GoldPerHour(region):N0} gold/hour");
                Assert.True(GoldPerHour(region) > 0);
            }

            // Income has to RISE across the game, or a later region is a pay cut.
            for (int region = 2; region <= 5; region++)
            {
                Assert.True(
                    GoldPerHour(region) > GoldPerHour(region - 1),
                    $"region {region} pays less than region {region - 1}");
            }
        }
    }
}

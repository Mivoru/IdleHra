using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Aptitude milestones (owner 2026-11-13): 30/40/50 points unlock a second
    /// bonus, read by the helpers the live tick, the offline window and the
    /// stat sheet share.
    /// </summary>
    public class AptitudeMilestonesTests
    {
        public AptitudeMilestonesTests()
        {
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Character(byte apt)
            => new()
            {
                PlayerId = 990_101L,
                CurrentLevel = 60,
                STR = 80, DEX = 60, CON = 60, LCK = 30,
                Aptitude_Strength = apt, Aptitude_Skill = apt, Aptitude_Endurance = apt, Aptitude_Fortune = apt,
            };

        [Theory]
        [InlineData(29, 0f, 0, 0f, 0)]
        [InlineData(30, 0.10f, 5, 3f, 5)]
        [InlineData(40, 0.20f, 10, 6f, 10)]
        [InlineData(50, 0.30f, 15, 10f, 15)]
        public void EachThresholdUnlocksItsStep(int apt, float crit, int yield, float block, int gold)
        {
            Assert.Equal(crit, AptitudeMilestones.CritDamageBonus(apt), 3);
            Assert.Equal(yield, AptitudeMilestones.GatherYieldPct(apt));
            Assert.Equal(block, AptitudeMilestones.BlockPct(apt), 3);
            Assert.Equal(gold, AptitudeMilestones.GoldPct(apt));
        }

        [Fact]
        public void TheLiveHelpersPayTheMilestones()
        {
            var low = Character(29);
            var high = Character(50);
            var lowStats = SimulationEngine.LiveCombatStats(in low);
            var highStats = SimulationEngine.LiveCombatStats(in high);

            // Strength: crit damage. Endurance: block.
            Assert.Equal(0.30f, SimulationEngine.LiveCritMultiplier(in high, in highStats) - SimulationEngine.LiveCritMultiplier(in low, in lowStats), 3);
            Assert.Equal(10f, highStats.BlockStrengthPct - lowStats.BlockStrengthPct, 3);

            // Fortune: gold from kills, as its own factor.
            var lowGold = CombatGoldReward.FactorsFor(in low, lowStats.GoldAcquisitionMultiplierPct);
            var highGold = CombatGoldReward.FactorsFor(in high, highStats.GoldAcquisitionMultiplierPct);
            Assert.Equal(1.0f, lowGold.Aptitude, 3);
            Assert.Equal(1.15f, highGold.Aptitude, 3);
        }

        [Fact]
        public void TheListNamesEveryAptitudeAtEveryThreshold()
        {
            Assert.Equal(4 * AptitudeMilestones.Thresholds.Length, AptitudeMilestones.All.Count);
        }
    }
}

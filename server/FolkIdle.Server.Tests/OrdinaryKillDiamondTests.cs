using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Owner decision 2026-09-28: an ordinary kill pays 1 diamond at 0.01%, not
    /// 0.05%. At one kill a second the old rate paid ~300 a week against the
    /// Delve's calibrated 60.
    /// </summary>
    public class OrdinaryKillDiamondTests
    {
        [Fact]
        public void TheChanceIsOneInTenThousand()
        {
            Assert.Equal(0.0001, SimulationEngine.OrdinaryKillDiamondChance);
            Assert.True(SimulationEngine.OrdinaryKillPaysDiamond(0.0));
            Assert.True(SimulationEngine.OrdinaryKillPaysDiamond(0.000099));
            Assert.False(SimulationEngine.OrdinaryKillPaysDiamond(0.0001));
            Assert.False(SimulationEngine.OrdinaryKillPaysDiamond(0.0004));
        }

        [Fact]
        public void AKillASecondForAWeekPaysAboutTheDelvesWeeklyCeiling()
        {
            // A seeded run of a week of kills at one a second.
            var rng = new Random(20260928);
            const int killsPerWeek = 7 * 24 * 3600;
            int diamonds = 0;
            for (int i = 0; i < killsPerWeek; i++)
            {
                if (SimulationEngine.OrdinaryKillPaysDiamond(rng.NextDouble())) diamonds++;
            }

            // Expected 60.5; the band is about +-4 standard deviations.
            Assert.InRange(diamonds, 30, 95);
            Assert.InRange(killsPerWeek * SimulationEngine.OrdinaryKillDiamondChance,
                DelveRegistry.MaxDiamondsPerWeek * 0.9, DelveRegistry.MaxDiamondsPerWeek * 1.1);
        }
    }
}

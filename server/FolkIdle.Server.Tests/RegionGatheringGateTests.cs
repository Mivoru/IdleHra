using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The region-2 catch-22 (owner decision 2026-11-14, option A): a felled
    /// region boss opens the next region's gathering, not only its door.
    /// </summary>
    public class RegionGatheringGateTests
    {
        [Fact]
        public void RegionBossKill_OpensNextRegionsGathering()
        {
            var payload = new TickStatePayload { HighestLocationReached = 1, HighestUnlockedRegion = 1 };

            SimulationEngine.ApplyKillProgression(ref payload, RaceUnlockRegistry.GetRegionBossMonsterId(1));

            Assert.Equal(2, payload.HighestUnlockedRegion);
            Assert.Equal(2, payload.HighestLocationReached);
        }

        [Fact]
        public void OrdinaryKill_DoesNotOpenTheNextRegion()
        {
            var payload = new TickStatePayload { HighestLocationReached = 1, HighestUnlockedRegion = 1 };

            SimulationEngine.ApplyKillProgression(ref payload, ContentRegistry.FirstCanonicalMonsterId);

            Assert.Equal(1, payload.HighestLocationReached);
        }

        [Fact]
        public void LastRegionBoss_OpensNoSixthLocation()
        {
            var payload = new TickStatePayload { HighestLocationReached = 5, HighestUnlockedRegion = 5 };

            SimulationEngine.ApplyKillProgression(ref payload, RaceUnlockRegistry.GetRegionBossMonsterId(5));

            Assert.Equal(5, payload.HighestLocationReached);
        }

        [Theory]
        [InlineData(1, 1, 1)]
        [InlineData(1, 2, 2)]
        [InlineData(3, 2, 3)]
        public void Raise_TakesTheHigherOfReachedAndUnlocked(int reached, int unlocked, int expected)
        {
            Assert.Equal(expected, GatheringReach.Raise(reached, unlocked));
        }
    }
}

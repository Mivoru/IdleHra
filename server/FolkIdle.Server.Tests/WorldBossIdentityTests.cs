using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 105: the world boss is named from monsters.json, not from a string
    /// the screen keeps. Pure, so no fixture.
    /// </summary>
    public class WorldBossIdentityTests
    {
        public WorldBossIdentityTests()
        {
            ContentRegistry.Initialize();
        }

        [Fact]
        public void TheBossIsNamedFromItsMonsterRow()
        {
            Assert.Equal(ContentRegistry.GetMonsterName(WorldBossIdentity.MonsterId), WorldBossIdentity.Name);
            // Owner, 2026-10-13: the world boss is Chernobog. The rewards, titles
            // and lore all name him, so the content row must too.
            Assert.Equal("Chernobog", WorldBossIdentity.Name);
            Assert.Equal(WorldBossIdentity.FallbackName, WorldBossIdentity.Name);
        }

        [Fact]
        public void TheBossIsNotOneOfTheRegionMonsters()
        {
            // The canon is ids 91-115 (five regions of 4+1). A world boss that
            // shared a row with a region monster would rename both at once.
            Assert.True(WorldBossIdentity.MonsterId < 91);
        }
    }
}

using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Tests;

public class SeasonalRotationEngineTests
{
    [Fact]
    public void CalculateLegacyShards_DoesNotDropExactGoldBoundary()
    {
        int shards = SeasonalRotationEngine.CalculateLegacyShards(9999L, 0L, 0L);

        Assert.Equal(50, shards);
    }

    [Fact]
    public void CalculateLegacyShards_CombinesLevelAndInventoryTerms()
    {
        int shards = SeasonalRotationEngine.CalculateLegacyShards(0L, 20L, 2L);

        Assert.Equal(4, shards);
    }
}

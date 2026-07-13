using FolkIdle.Server.Engine;

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

public class ChronoBufferEngineTests
{
    [Fact]
    public void CalculateOfflineBankedSeconds_AppliesLogarithmicDecayAfterFirstDay()
    {
        int banked = ChronoBufferEngine.CalculateOfflineBankedSeconds(86400L + 100L);

        Assert.Equal(86400 + (int)System.Math.Floor(System.Math.Log(101.0) * 1200.0), banked);
    }

    [Fact]
    public void AddBankedSeconds_ClampsAtSevenDays()
    {
        int banked = ChronoBufferEngine.AddBankedSeconds(604000, 1000L);

        Assert.Equal(604800, banked);
    }

    [Fact]
    public void IsValidSpeedMultiplier_AllowsOnlyArchitecturalValues()
    {
        Assert.True(ChronoBufferEngine.IsValidSpeedMultiplier(1.0));
        Assert.True(ChronoBufferEngine.IsValidSpeedMultiplier(2.0));
        Assert.True(ChronoBufferEngine.IsValidSpeedMultiplier(4.0));
        Assert.False(ChronoBufferEngine.IsValidSpeedMultiplier(3.0));
    }
}

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
    // Modul: matches GAME_DESIGN_SPEC.md/GDD exactly - BankedSeconds =
    // max(0, floor(ln(ElapsedSeconds - ThresholdSeconds + 1) * 1200.0)),
    // with no additive OfflineThresholdSeconds term. This assertion was
    // previously written against the buggy implementation (which added a
    // flat 86400 seconds on top of the decayed excess, banking roughly 7x
    // the specified amount) - updated to the corrected formula.
    [Fact]
    public void CalculateOfflineBankedSeconds_AppliesLogarithmicDecayAfterFirstDay()
    {
        int banked = ChronoBufferEngine.CalculateOfflineBankedSeconds(86400L + 100L);

        Assert.Equal((int)System.Math.Floor(System.Math.Log(101.0) * 1200.0), banked);
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

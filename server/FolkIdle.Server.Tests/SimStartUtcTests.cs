using System;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// FOLKIDLE_SIM_START_UTC puts game time zero at one instant for every run of
    /// a validation (night 2026-11-04 compared runs that started 3.5 and 6
    /// game hours apart). Only the parse is tested here: GameClock is
    /// process-wide state the rest of the suite reads.
    /// </summary>
    public class SimStartUtcTests
    {
        [Fact]
        public void AnIsoUtcTimeIsReadAsUtc()
        {
            var t = GameClock.ResolveStartUtc("2026-11-05T07:00:00Z", devToolsEnabled: true);
            Assert.Equal(new DateTime(2026, 11, 5, 7, 0, 0, DateTimeKind.Utc), t);
            Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
        }

        [Fact]
        public void ATimeWithoutAZoneIsTakenAsUtc()
        {
            Assert.Equal(new DateTime(2026, 11, 5, 7, 0, 0, DateTimeKind.Utc),
                GameClock.ResolveStartUtc("2026-11-05 07:00:00", devToolsEnabled: true));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("tomorrow morning")]
        public void UnsetOrUnreadableMeansNoOverride(string? raw)
        {
            Assert.Null(GameClock.ResolveStartUtc(raw, devToolsEnabled: true));
        }

        [Fact]
        public void WithoutDevToolsItIsIgnored()
        {
            // Production: the game clock is the wall clock, whatever the environment says.
            Assert.Null(GameClock.ResolveStartUtc("2026-11-05T07:00:00Z", devToolsEnabled: false));
        }
    }
}

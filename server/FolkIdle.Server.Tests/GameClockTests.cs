using System;
using FolkIdle.Server;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The game clock (2026-11-01). The property production depends on is that
    /// without dev tools it IS the wall clock - 149 call sites switched to it,
    /// and at speed 1 every one of them must read exactly what it read before.
    /// </summary>
    public class GameClockTests
    {
        [Fact]
        public void WithoutConfigurationTheGameClockIsTheWallClock()
        {
            // Nothing in the test host calls Configure, as nothing in production
            // does without FOLKIDLE_TICK_SPEED and dev tools.
            Assert.Equal(1, GameClock.Speed);
            DateTime before = DateTime.UtcNow;
            DateTime game = GameClock.UtcNow;
            DateTime after = DateTime.UtcNow;
            Assert.InRange(game, before, after);
            Assert.InRange(GameClock.UtcNowOffset.UtcDateTime, before, DateTime.UtcNow);
        }

        [Theory]
        [InlineData(1, 60)]
        [InlineData(10, 600)]
        [InlineData(20, 1200)]
        public void AtSpeedNAWallMinuteIsNGameMinutes(int speed, int expectedGameSeconds)
        {
            var anchor = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            const long frequency = 1_000_000; // a stopwatch ticking in microseconds
            DateTime projected = GameClock.Project(anchor, elapsedStopwatchTicks: 60 * frequency, frequency, speed);
            Assert.Equal(anchor.AddSeconds(expectedGameSeconds), projected);
        }

        [Fact]
        public void AnHourAtX20CrossesMidnightTwentyTimesFaster()
        {
            // The point of the clock: a daily key (tickets, refills, the world
            // boss day) moves on in 72 wall minutes at x20, not in 24 hours.
            var anchor = new DateTime(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc);
            const long frequency = 1_000;
            DateTime projected = GameClock.Project(anchor, elapsedStopwatchTicks: 3 * 60 * frequency, frequency, 20);
            Assert.Equal(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), projected);
        }
    }
}

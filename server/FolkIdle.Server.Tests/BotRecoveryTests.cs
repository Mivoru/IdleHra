using System;
using FolkIdle.Bots;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The two ways the val-x20 reference run of night 2026-11-04 lost bots:
    /// a refused fight that stayed refused for the rest of the run, and a
    /// throttled first login that ended the bot.
    /// </summary>
    public class BotRecoveryTests
    {
        private static readonly RefusalConditions Level3 = new(Level: 3, Location: 1, Region: 1, Deaths: 0);

        [Fact]
        public void ARefusalIsACooldown_NotABanUntilTheNextLevel()
        {
            var memory = new RefusalMemory();
            double? cooldown = memory.Mark(91, Level3, nowHours: 0.41);

            Assert.Equal(1.0 / 60, cooldown!.Value, 6);
            Assert.True(memory.IsRefused(91, Level3, 0.41 + 0.5 / 60));
            // eagerkit11 stayed at level 3 for 71 game hours on this refusal.
            Assert.False(memory.IsRefused(91, Level3, 0.41 + 1.01 / 60));
        }

        [Fact]
        public void ARepeatedRefusalWaitsLongerEachTime_UpToTwoGameHours()
        {
            Assert.Equal(1.0 / 60, RefusalMemory.CooldownHours(1), 6);
            Assert.Equal(2.0 / 60, RefusalMemory.CooldownHours(2), 6);
            Assert.Equal(16.0 / 60, RefusalMemory.CooldownHours(5), 6);
            Assert.Equal(2.0, RefusalMemory.CooldownHours(50), 6);
        }

        [Fact]
        public void AfterMaxStrikesTheBotStopsRetrying_UntilSomethingChanges()
        {
            var memory = new RefusalMemory();
            double now = 0;
            for (int strike = 1; strike < RefusalMemory.MaxStrikes; strike++)
            {
                Assert.NotNull(memory.Mark(91, Level3, now));
                now += 3; // past any cooldown
                Assert.False(memory.IsRefused(91, Level3, now));
            }

            Assert.Null(memory.Mark(91, Level3, now));
            Assert.True(memory.IsRefused(91, Level3, now + 1000));

            // A level, a location, a region or a death forgets it.
            Assert.False(memory.IsRefused(91, Level3 with { Deaths = 1 }, now + 1000));
            Assert.Equal(0, memory.Strikes(91));
        }

        [Theory]
        [InlineData(4, 1, 1, 0)]
        [InlineData(3, 2, 1, 0)]
        [InlineData(3, 1, 2, 0)]
        [InlineData(3, 1, 1, 1)]
        public void AnyChangedConditionForgetsTheRefusalAndItsStrikes(int level, int location, int region, int deaths)
        {
            var memory = new RefusalMemory();
            memory.Mark(91, Level3, 0);
            memory.Mark(91, Level3, 0);
            Assert.Equal(2, memory.Strikes(91));

            var changed = new RefusalConditions(level, location, region, deaths);
            Assert.False(memory.IsRefused(91, changed, 0));
            Assert.Equal(1.0 / 60, memory.Mark(91, changed, 0)!.Value, 6);
        }

        [Fact]
        public void AThrottledLoginHonoursRetryAfter()
        {
            // AuthThrottle answers Retry-After: 60.
            Assert.Equal(60, BotClient.LoginBackoff(1, TimeSpan.FromSeconds(60), 0).TotalSeconds, 3);
            Assert.Equal(75, BotClient.LoginBackoff(1, TimeSpan.FromSeconds(60), 1).TotalSeconds, 3);
        }

        [Fact]
        public void WithoutRetryAfterTheLoginBacksOffExponentially_Capped()
        {
            Assert.Equal(1, BotClient.LoginBackoff(1, null, 0).TotalSeconds, 3);
            Assert.Equal(8, BotClient.LoginBackoff(4, null, 0).TotalSeconds, 3);
            Assert.Equal(120, BotClient.LoginBackoff(20, null, 0).TotalSeconds, 3);
        }
    }
}

using FolkIdle.Server.Domain.Shared;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 43, 2d: the tick keeps an absolute schedule, catches up a bounded
    /// debt, and drops (and counts) the rest. Units are milliseconds here;
    /// the engine passes Stopwatch timestamps.
    /// </summary>
    public class TickPacerTests
    {
        private const long Interval = 100;

        [Fact]
        public void AFastTickSleepsUntilTheNextOneIsDue()
        {
            var pacer = new TickPacer(Interval);
            pacer.OnTickStarted(0);
            Assert.Equal(70, pacer.DelayUntilNextTick(30));
            Assert.Equal(0, pacer.CatchUpTicks);
        }

        [Fact]
        public void AnOversleepIsTakenOffTheNextSleepRatherThanLost()
        {
            // The old loop slept "interval minus duration" every tick, so a
            // 15 ms oversleep made the next tick start 15 ms late for good.
            var pacer = new TickPacer(Interval);
            pacer.OnTickStarted(0);
            pacer.DelayUntilNextTick(10);   // sleeps 90, wakes at 115
            pacer.OnTickStarted(115);
            Assert.Equal(75, pacer.DelayUntilNextTick(125)); // due at 200
        }

        [Fact]
        public void AnOverrunIsCaughtUpSoTenSecondsStillRunAHundredTicks()
        {
            var pacer = new TickPacer(Interval);
            long now = 0;
            int ticks = 0;
            while (now < 10_000)
            {
                pacer.OnTickStarted(now);
                ticks++;
                // Every tenth tick takes 250 ms; the rest take 10 ms.
                now += ticks % 10 == 0 ? 250 : 10;
                now += pacer.DelayUntilNextTick(now);
            }
            Assert.Equal(100, ticks);
            Assert.Equal(0, pacer.TicksDropped);
            Assert.True(pacer.CatchUpTicks > 0);
        }

        [Fact]
        public void ALongStallRunsAtMostFiveCatchUpTicksAndCountsTheRest()
        {
            var pacer = new TickPacer(Interval);
            pacer.OnTickStarted(0);
            // A 2 s stall: the ticks due at 100..2000 are owed (20 of them).
            long now = 2_050;
            Assert.Equal(0, pacer.DelayUntilNextTick(now));
            Assert.Equal(20 - TickPacer.MaxCatchUpTicks, pacer.TicksDropped);

            int immediate = 0;
            while (true)
            {
                pacer.OnTickStarted(now);
                now += 1;
                long delay = pacer.DelayUntilNextTick(now);
                if (delay > 0) break;
                immediate++;
            }
            // The first catch-up tick was the one run above; four more follow
            // back to back, then the schedule is met again.
            Assert.Equal(TickPacer.MaxCatchUpTicks - 1, immediate);
            Assert.Equal(20 - TickPacer.MaxCatchUpTicks, pacer.TicksDropped);
        }

        [Fact]
        public void ADeliberatePauseIsNeitherCaughtUpNorCountedAsDropped()
        {
            var pacer = new TickPacer(Interval);
            pacer.OnTickStarted(0);
            pacer.DelayUntilNextTick(10);
            pacer.Reset(); // an era transition held the loop for a minute
            pacer.OnTickStarted(60_000);
            Assert.Equal(90, pacer.DelayUntilNextTick(60_010));
            Assert.Equal(0, pacer.TicksDropped);
        }
    }
}

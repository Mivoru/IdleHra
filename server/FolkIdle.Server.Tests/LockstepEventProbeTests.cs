using System;
using FolkIdle.Server;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>Phase 1c: what wakes a lockstep bot, read off one player's tick.</summary>
    public class LockstepEventProbeTests
    {
        private static TickStatePayload Payload(int level = 10, long deaths = 0, byte halt = ActivityHaltReason.None, long activity = 91)
            => new TickStatePayload { CurrentLevel = level, LifetimeDeaths = deaths, ActivityHaltReason = halt, ActiveActivityId = activity };

        private static SimWakeReason Diff(TickStatePayload before, TickStatePayload after)
            => LockstepEventProbe.Diff(LockstepEventProbe.Capture(in before), in after);

        [Fact]
        public void AnOrdinaryTickWakesNobody()
            => Assert.Equal(SimWakeReason.None, Diff(Payload(), Payload()));

        [Fact]
        public void ALevelUpIsSeen()
            => Assert.Equal(SimWakeReason.LevelUp, Diff(Payload(level: 10), Payload(level: 11)));

        [Fact]
        public void ADeathIsSeenEvenWhenTheFightCarriesOn()
            => Assert.Equal(SimWakeReason.Death, Diff(Payload(deaths: 3), Payload(deaths: 4)));

        [Theory]
        [InlineData(ActivityHaltReason.OutOfFood)]
        [InlineData(ActivityHaltReason.InventoryFull)]
        [InlineData(ActivityHaltReason.Died)]
        public void ANewHaltIsSeen(byte halt)
            => Assert.Equal(SimWakeReason.ActivityHalted, Diff(Payload(), Payload(halt: halt)));

        [Fact]
        public void AHaltThatWasAlreadyThereIsNotSeenAgain()
            => Assert.Equal(SimWakeReason.None, Diff(Payload(halt: ActivityHaltReason.OutOfFood), Payload(halt: ActivityHaltReason.OutOfFood)));

        [Fact]
        public void AnActivityThatStoppedIsSeen()
            => Assert.Equal(SimWakeReason.ActivityHalted, Diff(Payload(activity: 91), Payload(activity: 0)));

        [Fact]
        public void SeveralAtOnceAreAllReported()
            => Assert.Equal(SimWakeReason.LevelUp | SimWakeReason.Death | SimWakeReason.ActivityHalted,
                Diff(Payload(level: 5, deaths: 0), Payload(level: 6, deaths: 1, halt: ActivityHaltReason.Died, activity: 0)));

        [Fact]
        public void NotifyAllWakesForTheKindsTheBotAskedFor()
        {
            var c = new SimConductor(quantumTicks: 1);
            c.Join("a");
            c.Bind("a", 8);
            var prep = c.PrepareTick(1)!.Value;
            c.Yield("a", 10_000, SimWakeReason.Death, 2, 2);
            c.CompleteTick(prep.TickToRun);
            c.NotifyAll(8, SimWakeReason.LevelUp | SimWakeReason.Death, eventTick: 50);
            Assert.Equal(52, c.NextDue);
        }

        [Fact]
        public void TheDailyResetWakesEveryBotThatAskedEvenLoggedOut()
        {
            var c = new SimConductor(quantumTicks: 1);
            c.Join("asks");
            c.Join("ignores");
            var prep = c.PrepareTick(0)!.Value;
            c.Yield("asks", 1_000_000, SimWakeReason.DailyReset, 3, 3);
            c.Yield("ignores", 1_000_000, SimWakeReason.None);
            c.CompleteTick(prep.TickToRun);
            c.NotifyEveryone(SimWakeReason.DailyReset, eventTick: 864_000);
            Assert.Equal(864_003, c.NextDue);

            // Nobody online: the clock jumps straight to the reset wake.
            var next = c.PrepareTick(0)!.Value;
            Assert.Equal(864_003, next.TickToRun);
            Assert.Equal(new[] { new SimWake("asks", SimWakeReason.DailyReset) }, next.Wakes);
        }

        [Fact]
        public void OffLockstepTheGuardsReadTheWallClocks()
        {
            // Nothing in the test host configures lockstep, as nothing in
            // production can: the guards must read exactly what they read before.
            Assert.False(GameClock.IsLockstep);
            long before = Environment.TickCount64;
            long guard = GameClock.MonotonicMs;
            Assert.InRange(guard, before, Environment.TickCount64);
            DateTime wall = DateTime.UtcNow;
            Assert.InRange(GameClock.ReactionUtcNow, wall.AddSeconds(-1), DateTime.UtcNow);
        }
    }
}

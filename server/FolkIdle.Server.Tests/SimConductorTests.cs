using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The lockstep conductor's wake rules (docs/design/LOCKSTEP_SIM.md). Pure
    /// tick arithmetic: no engine, no socket, no clock.
    /// </summary>
    public class SimConductorTests
    {
        private static (long Tick, IReadOnlyList<SimWake> Wakes, bool NeedsBroadcast) Prepare(SimConductor c, int online = 1, Func<long, bool>? lacks = null)
        {
            var prep = c.PrepareTick(online, lacks);
            Assert.NotNull(prep);
            return prep!.Value;
        }

        /// <summary>Runs ticks with nobody waking until <paramref name="untilTick"/> is complete.</summary>
        private static void RunQuietly(SimConductor c, long untilTick, int online = 1)
        {
            while (c.Tick < untilTick)
            {
                var (tick, wakes, _) = Prepare(c, online);
                Assert.Empty(wakes);
                c.CompleteTick(tick);
            }
        }

        [Fact]
        public void WithNoBotsTheWorldHoldsStill()
        {
            var c = new SimConductor();
            Assert.Null(c.PrepareTick(onlineSessions: 0));
            Assert.Equal(0, c.Tick);
        }

        [Fact]
        public void AJoiningBotIsWokenBeforeTheNextTick()
        {
            var c = new SimConductor();
            c.Join("a");
            var (tick, wakes, _) = Prepare(c);
            Assert.Equal(1, tick);
            Assert.Equal(new[] { new SimWake("a", SimWakeReason.Joined) }, wakes);
        }

        [Fact]
        public void TheTickWaitsForEveryWokenBotToYield()
        {
            var c = new SimConductor();
            c.Join("a");
            c.Join("b");
            var (_, wakes, _) = Prepare(c);
            Assert.Equal(2, wakes.Count);
            Assert.Null(c.PrepareTick(1)); // still thinking
            Assert.False(c.WaitForYields(TimeSpan.FromMilliseconds(20)));

            c.Yield("a", 100, SimWakeReason.None);
            Assert.False(c.WaitForYields(TimeSpan.FromMilliseconds(20)));
            c.Leave("b"); // a bot that leaves is no longer waited for
            Assert.True(c.WaitForYields(TimeSpan.FromMilliseconds(20)));
        }

        [Fact]
        public void AYieldingBotSleepsToItsWakeRoundedUpToTheQuantum()
        {
            var c = new SimConductor(quantumTicks: 10);
            c.Join("a");
            var (t1, _, _) = Prepare(c);
            c.Yield("a", 43, SimWakeReason.None);
            Assert.True(c.WaitForYields(TimeSpan.Zero));
            c.CompleteTick(t1);

            RunQuietly(c, 49);
            var (tick, wakes, _) = Prepare(c);
            Assert.Equal(50, tick);
            Assert.Equal(new[] { new SimWake("a", SimWakeReason.Planned) }, wakes);
        }

        [Fact]
        public void AYieldIntoThePastWakesAtTheNextBoundaryNotBefore()
        {
            var c = new SimConductor(quantumTicks: 1);
            c.Join("a");
            var (t1, _, _) = Prepare(c);
            c.Yield("a", 0, SimWakeReason.None);
            c.CompleteTick(t1);
            var (t2, wakes, _) = Prepare(c);
            Assert.Equal(2, t2);
            Assert.Single(wakes);
        }

        [Fact]
        public void AnEventTheBotAskedForWakesItAfterAReactionInsideItsRange()
        {
            for (int seed = 1; seed <= 50; seed++)
            {
                var c = new SimConductor(quantumTicks: 1, seed: seed);
                c.Join("a");
                c.Bind("a", 77);
                var (t1, _, _) = Prepare(c);
                c.Yield("a", 10_000, SimWakeReason.Death, reactionMinTicks: 4, reactionMaxTicks: 12);
                c.CompleteTick(t1);

                c.Notify(77, SimWakeReason.Death, eventTick: 20);
                long due = c.NextDue!.Value;
                Assert.InRange(due, 24, 32);

                RunQuietly(c, due - 1);
                var (tick, wakes, _) = Prepare(c);
                Assert.Equal(due, tick);
                Assert.Equal(new[] { new SimWake("a", SimWakeReason.Death) }, wakes);
            }
        }

        [Fact]
        public void TheReactionIsSampledAcrossTheWholeRange()
        {
            var seen = new HashSet<long>();
            var c = new SimConductor(quantumTicks: 1, seed: 7);
            c.Join("a");
            c.Bind("a", 5);
            var (t1, _, _) = Prepare(c);
            c.CompleteTick(t1);
            for (int i = 0; i < 400; i++)
            {
                c.Yield("a", 1_000_000, SimWakeReason.LevelUp, 4, 12);
                c.Notify(5, SimWakeReason.LevelUp, eventTick: 100);
                seen.Add(c.NextDue!.Value - 100);
            }
            Assert.Equal(Enumerable.Range(4, 9).Select(i => (long)i), seen.OrderBy(x => x));
        }

        [Fact]
        public void AnEventTheBotDidNotAskForChangesNothing()
        {
            var c = new SimConductor(quantumTicks: 1);
            c.Join("a");
            c.Bind("a", 77);
            var (t1, _, _) = Prepare(c);
            c.Yield("a", 500, SimWakeReason.Death);
            c.CompleteTick(t1);
            c.Notify(77, SimWakeReason.LevelUp, eventTick: 20);
            c.Notify(78, SimWakeReason.Death, eventTick: 20); // somebody else's death
            Assert.Equal(500, c.NextDue);
        }

        [Fact]
        public void AnEventLaterThanThePlannedWakeDoesNotPostponeIt()
        {
            var c = new SimConductor(quantumTicks: 1);
            c.Join("a");
            c.Bind("a", 77);
            var (t1, _, _) = Prepare(c);
            c.Yield("a", 30, SimWakeReason.Death, 4, 4);
            c.CompleteTick(t1);
            c.Notify(77, SimWakeReason.Death, eventTick: 100);
            Assert.Equal(30, c.NextDue);
        }

        [Fact]
        public void WithNobodyOnlineTheClockJumpsToTheNextWake()
        {
            var c = new SimConductor(quantumTicks: 10);
            c.Join("a");
            var (t1, _, _) = Prepare(c, online: 0);
            c.Yield("a", 288_000, SimWakeReason.None); // eight game hours away
            c.CompleteTick(t1);

            var (tick, wakes, _) = Prepare(c, online: 0);
            Assert.Equal(288_000, tick);
            Assert.Single(wakes);
            Assert.Equal(288_000 - 2, c.TicksJumped);
        }

        [Fact]
        public void WithSomebodyOnlineEveryTickRuns()
        {
            var c = new SimConductor(quantumTicks: 10);
            c.Join("a");
            var (t1, _, _) = Prepare(c, online: 1);
            c.Yield("a", 288_000, SimWakeReason.None);
            c.CompleteTick(t1);

            var (tick, wakes, _) = Prepare(c, online: 1);
            Assert.Equal(2, tick);
            Assert.Empty(wakes);
            Assert.Equal(0, c.TicksJumped);
        }

        [Fact]
        public void AnOnlineBotWithoutAFreshSnapshotWaitsOneTickAndAsksForABroadcast()
        {
            var c = new SimConductor(quantumTicks: 1);
            c.Join("a");
            c.Bind("a", 9);
            var (t1, _, _) = Prepare(c);
            c.Yield("a", 5, SimWakeReason.None);
            c.CompleteTick(t1);
            RunQuietly(c, 4);

            var (t5, wakes, needsBroadcast) = Prepare(c, lacks: _ => true);
            Assert.Equal(5, t5);
            Assert.Empty(wakes);
            Assert.True(needsBroadcast);
            c.CompleteTick(t5);

            var (t6, wakes6, _) = Prepare(c, lacks: _ => false);
            Assert.Equal(6, t6);
            Assert.Single(wakes6);
        }

        [Fact]
        public void BotsDueTogetherAreWokenInOneFrameInNameOrder()
        {
            var c = new SimConductor(quantumTicks: 10);
            foreach (var bot in new[] { "c", "a", "b" }) c.Join(bot);
            var (t1, first, _) = Prepare(c);
            Assert.Equal(new[] { "a", "b", "c" }, first.Select(w => w.Bot));
            c.Yield("a", 11, SimWakeReason.None); // rounds up to 20
            c.Yield("b", 19, SimWakeReason.None); // rounds up to 20
            c.Yield("c", 21, SimWakeReason.None); // rounds up to 30
            c.CompleteTick(t1);
            RunQuietly(c, 19);
            var (_, wakes, _) = Prepare(c);
            Assert.Equal(new[] { "a", "b" }, wakes.Select(w => w.Bot));
        }

        [Fact]
        public void AnyDueByIsWhatTheEngineBroadcastsFor()
        {
            var c = new SimConductor(quantumTicks: 10);
            c.Join("a");
            var (t1, _, _) = Prepare(c);
            c.Yield("a", 20, SimWakeReason.None);
            c.CompleteTick(t1);
            Assert.False(c.AnyDueBy(19));
            Assert.True(c.AnyDueBy(20));
        }

        [Theory]
        [InlineData(1, 0, 0)]
        [InlineData(10, 1, 10)]
        [InlineData(10, 10, 10)]
        [InlineData(10, 11, 20)]
        [InlineData(7, 15, 21)]
        public void QuantizeRoundsUp(int quantum, long tick, long expected)
            => Assert.Equal(expected, new SimConductor(quantum).QuantizeUp(tick));

        [Fact]
        public void LockstepGameTimeIsTheAnchorPlusATenthOfASecondATick()
        {
            var anchor = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(anchor, GameClock.LockstepTime(anchor, 0));
            Assert.Equal(anchor.AddHours(1), GameClock.LockstepTime(anchor, 36_000));
            Assert.Equal(anchor.AddDays(60), GameClock.LockstepTime(anchor, 60L * 864_000));
        }

        [Theory]
        [InlineData(null, true, false)]
        [InlineData("1", false, false)]
        [InlineData("0", true, false)]
        [InlineData("1", true, true)]
        [InlineData(" 1 ", true, true)]
        public void LockstepNeedsTheFlagAndDevTools(string? flag, bool devTools, bool expected)
            => Assert.Equal(expected, SimLockstep.ResolveEnabled(flag, devTools));

        [Fact]
        public void ControlFramesDriveTheConductor()
        {
            var c = new SimConductor(quantumTicks: 1);
            var joined = new HashSet<string>();
            NetworkBroadcastSystem.HandleSimControlMessage(c, """{"Type":"join","Bot":"a","ReactionMin":2,"ReactionMax":2}""", joined);
            NetworkBroadcastSystem.HandleSimControlMessage(c, """{"Type":"bind","Bot":"a","PlayerId":42}""", joined);
            Assert.Contains("a", joined);

            var (t1, wakes, _) = Prepare(c);
            Assert.Single(wakes);
            NetworkBroadcastSystem.HandleSimControlMessage(c, """{"Type":"yield","Bots":[{"Bot":"a","NextWakeTick":900,"WakeOn":4}]}""", joined);
            Assert.True(c.WaitForYields(TimeSpan.Zero));
            c.CompleteTick(t1);

            c.Notify(42, SimWakeReason.Death, eventTick: 10);
            Assert.Equal(12, c.NextDue); // reaction 2, from the join frame

            NetworkBroadcastSystem.HandleSimControlMessage(c, """{"Type":"leave","Bot":"a"}""", joined);
            Assert.Equal(0, c.ParticipantCount);
            Assert.Empty(joined);
        }

        [Fact]
        public async Task AYieldFromAnotherThreadReleasesTheWaitingTick()
        {
            // Dedicated threads, not the pool: CI runs the suite in parallel and
            // a pooled continuation was once late by more than the wait itself.
            var c = new SimConductor();
            c.Join("a");
            Prepare(c);
            bool released = false;
            var waiter = new System.Threading.Thread(() => released = c.WaitForYields(TimeSpan.FromSeconds(60)));
            waiter.Start();
            var yielder = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(20);
                c.Yield("a", 100, SimWakeReason.None);
            });
            yielder.Start();
            yielder.Join();
            waiter.Join();
            Assert.True(released);
            await Task.CompletedTask;
        }
    }
}

namespace FolkIdle.Server.Tests
{
    /// <summary>Phase 1b: the gameplay crons on the tick (SimCronPump).</summary>
    public class SimCronPumpTests
    {
        [Fact]
        public void ACronRunsAtItsProductionPeriodInTicks()
        {
            var pump = new SimCronPump();
            var ranAt = new List<long>();
            long tick = 0;
            pump.Register("codex", 5000, () => { ranAt.Add(tick); return Task.CompletedTask; });
            for (tick = 1; tick <= 160; tick++) pump.RunDue(tick);
            Assert.Equal(new long[] { 50, 100, 150 }, ranAt);
        }

        [Fact]
        public void RunAtStartRunsOnTheFirstTick()
        {
            var pump = new SimCronPump();
            var ranAt = new List<long>();
            long tick = 0;
            pump.Register("liveops", 60_000, () => { ranAt.Add(tick); return Task.CompletedTask; }, runAtStart: true);
            for (tick = 1; tick <= 1300; tick++) pump.RunDue(tick);
            Assert.Equal(new long[] { 1, 601, 1201 }, ranAt);
        }

        [Fact]
        public void AJumpOverManyPeriodsRunsTheCronOnceOnLanding()
        {
            var pump = new SimCronPump();
            int runs = 0;
            pump.Register("loot", 3000, () => { runs++; return Task.CompletedTask; });
            pump.RunDue(1);
            pump.RunDue(216_000); // six game hours later, nobody online
            Assert.Equal(1, runs);
            pump.RunDue(216_029);
            Assert.Equal(1, runs);
            pump.RunDue(216_030);
            Assert.Equal(2, runs);
        }

        [Fact]
        public void TheTickWaitsForTheCycleToFinish()
        {
            var pump = new SimCronPump();
            bool finished = false;
            pump.Register("slow", 100, async () => { await Task.Delay(50); finished = true; });
            pump.RunDue(1);
            Assert.True(finished);
        }

        [Fact]
        public void AFailingCycleIsCountedAndTheOthersStillRun()
        {
            var pump = new SimCronPump();
            int good = 0;
            pump.Register("bad", 100, () => throw new InvalidOperationException("pool refused"));
            pump.Register("good", 100, () => { good++; return Task.CompletedTask; });
            pump.RunDue(1);
            pump.RunDue(2);
            Assert.Equal(2, good);
            var bad = pump.Describe().Single(d => d.Name == "bad");
            Assert.Equal(2, bad.Failures);
            Assert.Equal(2, bad.Runs);
        }
    }
}

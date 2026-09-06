using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE SECOND LOOT FAULT, 2026-09-06: gathering starved combat loot.
    ///
    /// One worker loop drains two queues. The gathering drain was
    /// `while (queue.TryDequeue(...))`, which terminates only when the producer
    /// pauses - and the producer does not pause: a harvest enqueues one grant
    /// PER ROLL, the roll count is scaled by the codex yield multiplier, and
    /// that multiplier stood at 71.9x on the live account that reported this.
    /// Two characters gathering enqueued several hundred grants a second
    /// against a worker that could write perhaps thirty, each grant paying for
    /// its own scope, transaction and three round trips.
    ///
    /// So the loop never came back round: no equipment was written, the
    /// throughput line above the loop never printed, and the only time drops
    /// appeared was the quiet window after a relogin - which is exactly how the
    /// player reported it ("items only after offline time / relogin").
    ///
    /// Two guarantees are pinned here: a cycle is FINITE whatever the producer
    /// does, and a batch costs one write per material rather than one per roll.
    /// </summary>
    public class GatheringGrantStarvationTests
    {
        private readonly ITestOutputHelper _o;

        public GatheringGrantStarvationTests(ITestOutputHelper o) => _o = o;

        private static GatheredMaterialGrant Grant(long playerId, int itemId, int quantity, long activityId = 7)
            => new() { PlayerId = playerId, ItemId = itemId, Quantity = quantity, ActivityId = activityId };

        [Fact]
        public void ABatchCostsOneWritePerMaterialInsteadOfOnePerRoll()
        {
            var queue = new ConcurrentQueue<GatheredMaterialGrant>();

            // One harvest at the live account's 71.9x codex yield multiplier is
            // ~72 rolls, and each roll is its own grant. Ten seconds of two
            // characters harvesting twice a second is what this looks like.
            const int harvests = 40;
            const int rollsPerHarvest = 72;
            for (int h = 0; h < harvests; h++)
            {
                for (int r = 0; r < rollsPerHarvest; r++)
                {
                    queue.Enqueue(Grant(8, itemId: r % 2 == 0 ? 101 : 102, quantity: 1));
                }
            }

            var totals = new Dictionary<(long PlayerId, int ItemId), CombatLootEngine.GatheringGrantTotal>();
            int taken = CombatLootEngine.CoalesceGatheringGrants(queue, queue.Count, totals);

            _o.WriteLine($"{taken} raw grants -> {totals.Count} writes");

            Assert.Equal(harvests * rollsPerHarvest, taken);
            Assert.Equal(2, totals.Count);
            Assert.Equal(harvests * rollsPerHarvest / 2, totals[(8, 101)].Quantity);
            Assert.Equal(harvests * rollsPerHarvest / 2, totals[(8, 102)].Quantity);
            Assert.Empty(queue);
        }

        [Fact]
        public void ACycleIsFiniteWhileTheProducerKeepsRunning()
        {
            // The producer is faster than the consumer - the live case. The
            // drain must still hand control back, or the loot queue beside it
            // is never looked at again.
            var queue = new ConcurrentQueue<GatheredMaterialGrant>();
            for (int i = 0; i < 500; i++) queue.Enqueue(Grant(8, 101, 1));

            int budget = queue.Count;

            // 200 more arrive mid-drain, as they do at 10 Hz.
            for (int i = 0; i < 200; i++) queue.Enqueue(Grant(8, 101, 1));

            var totals = new Dictionary<(long PlayerId, int ItemId), CombatLootEngine.GatheringGrantTotal>();
            int taken = CombatLootEngine.CoalesceGatheringGrants(queue, budget, totals);

            Assert.Equal(500, taken);
            Assert.Equal(200, queue.Count);
        }

        [Fact]
        public void GrantsAreKeptApartPerPlayerAndMaterial()
        {
            var queue = new ConcurrentQueue<GatheredMaterialGrant>();
            queue.Enqueue(Grant(8, 101, 3, activityId: 11));
            queue.Enqueue(Grant(8, 101, 4, activityId: 12));
            queue.Enqueue(Grant(9, 101, 5, activityId: 13));
            queue.Enqueue(Grant(8, 102, 6, activityId: 14));

            var totals = new Dictionary<(long PlayerId, int ItemId), CombatLootEngine.GatheringGrantTotal>();
            CombatLootEngine.CoalesceGatheringGrants(queue, 4, totals);

            Assert.Equal(3, totals.Count);
            Assert.Equal(7, totals[(8, 101)].Quantity);
            Assert.Equal(5, totals[(9, 101)].Quantity);
            Assert.Equal(6, totals[(8, 102)].Quantity);

            // The feed needs an origin; the most recent one is the honest answer.
            Assert.Equal(12, totals[(8, 101)].ActivityId);
        }

        [Fact]
        public void ASumThatWouldOverflowSaturatesRatherThanGoingNegative()
        {
            var queue = new ConcurrentQueue<GatheredMaterialGrant>();
            queue.Enqueue(Grant(8, 101, int.MaxValue - 1));
            queue.Enqueue(Grant(8, 101, 1000));

            var totals = new Dictionary<(long PlayerId, int ItemId), CombatLootEngine.GatheringGrantTotal>();
            CombatLootEngine.CoalesceGatheringGrants(queue, 2, totals);

            Assert.Equal(int.MaxValue, totals[(8, 101)].Quantity);
        }

        [Fact]
        public void NeitherDrainInTheWorkerLoopIsUnbounded()
        {
            // Modul: the guard for the shape, not the symptom. `while (queue
            // .TryDequeue(out _))` inside the worker's cycle is what let one
            // queue hold the loop forever; a bounded drain is the whole fix, and
            // it is one careless edit away from coming back.
            string source = SourceOf("Engine/CombatLootEngine.cs");
            int executeAt = source.IndexOf("private async Task ExecuteAsync(", System.StringComparison.Ordinal);
            Assert.True(executeAt > 0, "ExecuteAsync not found - did the worker move?");

            int drainAt = source.IndexOf("private async Task DrainGatheringGrantsAsync(", System.StringComparison.Ordinal);
            Assert.True(drainAt > executeAt, "DrainGatheringGrantsAsync not found after ExecuteAsync.");

            string cycle = source.Substring(executeAt, drainAt - executeAt);
            var unbounded = Regex.Matches(cycle, @"while\s*\([A-Za-z_.]*Queue\.TryDequeue");
            _o.WriteLine($"unbounded drains in the worker cycle: {unbounded.Count}");
            Assert.Empty(unbounded);
        }

        private static string SourceOf(string relativePath)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            string full = Path.Combine(dir!.FullName, "server", "FolkIdle.Server", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"{full} not found");
            return File.ReadAllText(full);
        }
    }
}

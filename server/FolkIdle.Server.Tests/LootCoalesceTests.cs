using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// One loot transaction per player and monster a cycle instead of one per
    /// kill (2026-11-03). The batch carries Kills = N, and
    /// ProcessMonsterLootDropAsync rolls N independent kills in its loop - the
    /// path the offline catch-up already uses - so only the folding is new.
    /// </summary>
    public class LootCoalesceTests
    {
        private static CombatLootDropRequest Kill(long player, int monster, int salvageBelow = 0, float luck = 0f, int kills = 0)
            => new CombatLootDropRequest { PlayerId = player, MonsterId = monster, AutoSalvageBelowTier = salvageBelow, LootLuckPct = luck, Kills = kills };

        private static List<CombatLootDropRequest> Coalesce(IEnumerable<CombatLootDropRequest> requests, int? budget = null)
        {
            var queue = new ConcurrentQueue<CombatLootDropRequest>(requests);
            return CombatLootEngine.CoalesceDropRequests(queue, budget ?? queue.Count);
        }

        [Fact]
        public void IdenticalKillsFoldIntoOneRequestCarryingTheirCount()
        {
            var batches = Coalesce(Enumerable.Repeat(Kill(1, 91), 250));
            var batch = Assert.Single(batches);
            Assert.Equal(250, batch.Kills);
            Assert.Equal(1, batch.PlayerId);
            Assert.Equal(91, batch.MonsterId);
        }

        [Fact]
        public void ZeroKillsMeansOneKill()
        {
            // The live tick leaves Kills at 0; reading that as "no kills" would
            // switch off every drop in the game.
            var batches = Coalesce(new[] { Kill(1, 91, kills: 0), Kill(1, 91, kills: 0), Kill(1, 91, kills: 5) });
            Assert.Equal(7, Assert.Single(batches).Kills);
        }

        [Fact]
        public void AnyOtherDifferenceKeepsBatchesApart()
        {
            var batches = Coalesce(new[]
            {
                Kill(1, 91), Kill(2, 91),           // another player
                Kill(1, 92),                        // another monster
                Kill(1, 91, salvageBelow: 3),       // another chest setting
                Kill(1, 91, luck: 5f),              // another luck stat
                Kill(1, 91),
            });
            Assert.Equal(5, batches.Count);
            Assert.Equal(2, batches[0].Kills);
            Assert.All(batches.Skip(1), b => Assert.Equal(1, b.Kills));
        }

        [Fact]
        public void BatchesKeepTheOrderTheirFirstKillArrivedIn()
        {
            var batches = Coalesce(new[] { Kill(3, 91), Kill(1, 91), Kill(3, 91), Kill(2, 91), Kill(1, 91) });
            Assert.Equal(new long[] { 3, 1, 2 }, batches.Select(b => b.PlayerId));
            Assert.Equal(new[] { 2, 2, 1 }, batches.Select(b => b.Kills));
        }

        [Fact]
        public void TheBudgetIsReadOnceAndTheRestWaitsForTheNextCycle()
        {
            var queue = new ConcurrentQueue<CombatLootDropRequest>(Enumerable.Repeat(Kill(1, 91), 10));
            var batches = CombatLootEngine.CoalesceDropRequests(queue, budget: 4);
            Assert.Equal(4, Assert.Single(batches).Kills);
            Assert.Equal(6, queue.Count);
        }

        [Fact]
        public void EveryRequestTakenIsVisitedOnce()
        {
            int visited = 0;
            var queue = new ConcurrentQueue<CombatLootDropRequest>(Enumerable.Repeat(Kill(1, 91), 7));
            CombatLootEngine.CoalesceDropRequests(queue, 7, (in CombatLootDropRequest _) => visited++);
            Assert.Equal(7, visited);
        }

        [Fact]
        public void TheKillCountIsConservedAcrossAnyMix()
        {
            var rng = new System.Random(5);
            var requests = Enumerable.Range(0, 2000).Select(_ => Kill(rng.Next(1, 6), rng.Next(91, 96), kills: rng.Next(0, 3))).ToList();
            long expected = requests.Sum(r => r.Kills <= 0 ? 1 : r.Kills);
            var batches = Coalesce(requests);
            Assert.Equal(expected, batches.Sum(b => (long)b.Kills));
            Assert.True(batches.Count <= 25, $"{batches.Count} batches for 5 players x 5 monsters");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// 2026-11-06: a gathering batch is off the queue once it is coalesced, so
    /// a batch that cannot even be written to the outbox must go back on the
    /// queue whole - before this it was logged and lost.
    /// </summary>
    [Collection("Static loot queues")]
    public class GatheringGrantRequeueTests
    {
        private sealed class RefusingProvider : IServiceProvider, IServiceScopeFactory
        {
            public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? this : null;
            public IServiceScope CreateScope() => throw new InvalidOperationException("XX000 (EMAXCONNSESSION) max clients reached in session mode");
        }

        [Fact]
        public async Task ABatchThatCannotBeReserved_GoesBackOnTheQueueWhole()
        {
            ContentRegistry.Initialize();
            const long playerId = 950_324_001L;
            var engine = new CombatLootEngine(new RefusingProvider(), new PlayerSessionRegistry());
            var grants = new List<KeyValuePair<(long PlayerId, int ItemId), CombatLootEngine.GatheringGrantTotal>>
            {
                new((playerId, 267), new CombatLootEngine.GatheringGrantTotal { ActivityId = 7, Quantity = 50 }),
            };

            var outcome = await engine.GrantGatheredMaterialsAsync(playerId, grants);

            Assert.Equal(CombatLootEngine.GatheringGrantOutcome.Requeued, outcome);
            var back = new List<GatheredMaterialGrant>();
            int n = CombatLootEngine.GatheringGrantQueue.Count;
            for (int i = 0; i < n && CombatLootEngine.GatheringGrantQueue.TryDequeue(out var g); i++)
            {
                if (g.PlayerId == playerId) back.Add(g);
                else CombatLootEngine.GatheringGrantQueue.Enqueue(g);
            }
            var grant = Assert.Single(back);
            Assert.Equal(267, grant.ItemId);
            Assert.Equal(50, grant.Quantity);
            Assert.Equal(7L, grant.ActivityId);
        }
    }
}

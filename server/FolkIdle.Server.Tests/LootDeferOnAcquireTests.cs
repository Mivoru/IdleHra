using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Review of #316 (2026-11-04): a coalesced batch carries every kill of a
    /// player on a monster for the cycle, and the scope and transaction are
    /// acquired before ProcessMonsterLootDropAsync's try. A refused connection
    /// there used to drop the whole batch as "failed". It must stay owed.
    /// </summary>
    public class LootDeferOnAcquireTests
    {
        /// <summary>A provider whose every scope is refused, like the pooler's EMAXCONNSESSION.</summary>
        private sealed class RefusingProvider : IServiceProvider, IServiceScopeFactory
        {
            public int Attempts;
            public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? this : null;
            public IServiceScope CreateScope()
            {
                Attempts++;
                throw new InvalidOperationException("XX000 (EMAXCONNSESSION) max clients reached in session mode");
            }
        }

        private static CombatLootDropRequest Kill(long player, int monster) => new() { PlayerId = player, MonsterId = monster };

        [Fact]
        public async Task ABatchThatGetsNoTransactionIsKeptWhole_AndAbsorbsTheNextCyclesKills()
        {
            var provider = new RefusingProvider();
            var engine = new CombatLootEngine(provider, new PlayerSessionRegistry());
            var queue = new ConcurrentQueue<CombatLootDropRequest>(Enumerable.Repeat(Kill(950_316_001, 91), 40));

            await engine.DrainDropRequestsAsync(queue);

            var kept = Assert.Single(engine.DeferredBatches);
            Assert.Equal(40, kept.Kills);
            Assert.Equal(950_316_001, kept.PlayerId);
            Assert.Empty(queue);
            Assert.Equal(1, provider.Attempts);

            // The outage goes on; new kills of the same key fold into the kept
            // batch instead of becoming a second one.
            for (int i = 0; i < 5; i++) queue.Enqueue(Kill(950_316_001, 91));
            queue.Enqueue(Kill(950_316_001, 92));
            await engine.DrainDropRequestsAsync(queue);

            Assert.Equal(2, engine.DeferredBatchCount);
            Assert.Equal(45, engine.DeferredBatches.Single(b => b.MonsterId == 91).Kills);
            Assert.Equal(1, engine.DeferredBatches.Single(b => b.MonsterId == 92).Kills);
        }

        [Fact]
        public async Task ABatchWhoseBeginTransactionFailsIsKeptWhole()
        {
            // The scope and the DbContext are fine; the connection BeginTransactionAsync
            // opens is refused (nothing listens on port 1). This is the other half of
            // the acquire: the provider test above refuses the scope itself.
            var services = new ServiceCollection();
            services.AddDbContext<FolkIdle.Server.Models.FolkIdleDbContext>(o => Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions
                .UseNpgsql(o, "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"));
            using var provider = services.BuildServiceProvider();
            var engine = new CombatLootEngine(provider, new PlayerSessionRegistry());
            var queue = new ConcurrentQueue<CombatLootDropRequest>(Enumerable.Repeat(Kill(950_316_002, 91), 12));

            await engine.DrainDropRequestsAsync(queue);

            var kept = Assert.Single(engine.DeferredBatches);
            Assert.Equal(12, kept.Kills);
            Assert.Equal(950_316_002, kept.PlayerId);
        }

        [Fact]
        public void CarriedOverBatchesComeFirstAndKeepTheirCounts()
        {
            var queue = new ConcurrentQueue<CombatLootDropRequest>(new[] { Kill(2, 91), Kill(1, 91) });
            var kept = new[] { new CombatLootDropRequest { PlayerId = 1, MonsterId = 91, Kills = 30 } };

            var batches = CombatLootEngine.CoalesceDropRequests(queue, queue.Count, carriedOver: kept);

            Assert.Equal(new long[] { 1, 2 }, batches.Select(b => b.PlayerId));
            Assert.Equal(new[] { 31, 1 }, batches.Select(b => b.Kills));
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A shutdown writes what the loot worker had not reached yet. Both queues
    /// are in memory, and before 2026-11-07 the process exited with them.
    /// </summary>
    [Collection("Postgres collection")]
    public class LootShutdownDrainTests
    {
        private readonly PostgresTestFixture _fixture;

        public LootShutdownDrainTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        [Fact]
        public async Task GatheringGrantsStillQueued_AreWrittenByTheShutdownDrain()
        {
            const long playerId = 8_100_000_201L;
            const int birchLogItemId = 267; // ContentRegistry.GetItemBaseId(267) == "birch_log"

            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            // A running worker that has not reached the grants yet: its first
            // cycle waits three seconds, and the drain stops it first.
            engine.StartCron();
            for (int i = 0; i < 3; i++)
            {
                CombatLootEngine.GatheringGrantQueue.Enqueue(new GatheredMaterialGrant
                {
                    PlayerId = playerId,
                    ActivityId = 7,
                    ItemId = birchLogItemId,
                    Quantity = 10,
                });
            }

            bool drained = await engine.DrainForShutdownAsync(TimeSpan.FromSeconds(20));

            Assert.True(drained);
            Assert.DoesNotContain(CombatLootEngine.GatheringGrantQueue, g => g.PlayerId == playerId);
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            long held = await verify.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "birch_log")
                .Select(c => c.Quantity)
                .SingleOrDefaultAsync();
            Assert.Equal(30L, held);
        }

        [Fact]
        public async Task ADrainWithNothingQueued_ReturnsAtOnce()
        {
            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Never started: the lockstep path, where the pump owns the cycle.
            bool drained = await engine.DrainForShutdownAsync(TimeSpan.FromSeconds(20));

            // Another test in the collection may have left grants for its own
            // player in the static queue; the drain writes those too, so only
            // the result and the bound are asserted.
            Assert.True(drained);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
        }
    }
}

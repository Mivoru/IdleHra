using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 4 of the durable-grant-retry plan: the drain worker itself. Tasks
    /// 1-3 prove the WRITE side (a failed grant lands in pending_grants);
    /// this file proves the READ side - a budgeted cycle applies eligible
    /// rows, backs off and eventually dead-letters the ones that cannot
    /// succeed, and tells a live session its retry landed without a relogin.
    ///
    /// Modul: DrainOneCycleAsync is called directly rather than through
    /// StartCron()'s Task.Delay(5000) loop wherever a test does not need to
    /// prove the scheduling wrapper itself - CronWorkerGuardTests already
    /// pins that every StartCron loop (this one included) wraps its body in
    /// a catch that can never let the worker die, and Program.cs's own
    /// wiring is what makes this the FIFTEENTH-plus consumer of that
    /// guarantee. What is unique to THIS engine - the query, the budget, the
    /// backoff math, the dead-letter rule, the live-session notify - all
    /// lives in DrainOneCycleAsync, and calling it directly makes every one
    /// of those assertions deterministic instead of racing a 5-second timer.
    /// </summary>
    [Collection("Postgres collection")]
    public class PendingGrantDrainEngineTests
    {
        private readonly PostgresTestFixture _fixture;

        public PendingGrantDrainEngineTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task<PlayerRecord> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"drain_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player;
        }

        // Modul: same trigger-based poison PendingGrantGatheringTests and
        // PendingGrantCombatLootTests use, for the same reason - neither
        // CommodityRecords nor EquipmentInstances declares an FK to
        // PlayerRecords in this codebase, so a nonexistent PlayerId does not
        // actually throw on apply here either. This is what makes a
        // CommodityDeltas row genuinely unable to ever succeed for the
        // give-up test, in place of the plan's "a commodity delta for a
        // playerId that will never exist".
        private async Task InstallCommodityWritePoisonAsync(long poisonPlayerId)
        {
            string sql = @"
                CREATE OR REPLACE FUNCTION pg_test_poison_commodity_write_drain() RETURNS trigger AS $BODY$
                BEGIN
                  IF NEW.""PlayerId"" = " + poisonPlayerId + @" THEN
                    RAISE EXCEPTION 'poisoned for test (PendingGrantDrainEngineTests)';
                  END IF;
                  RETURN NEW;
                END;
                $BODY$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS pg_test_poison_commodity_trigger_drain ON ""CommodityRecords"";
                CREATE TRIGGER pg_test_poison_commodity_trigger_drain
                BEFORE INSERT OR UPDATE ON ""CommodityRecords""
                FOR EACH ROW EXECUTE FUNCTION pg_test_poison_commodity_write_drain();";

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        private async Task RemoveCommodityWritePoisonAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS pg_test_poison_commodity_trigger_drain ON \"CommodityRecords\"; " +
                "DROP FUNCTION IF EXISTS pg_test_poison_commodity_write_drain();");
        }

        [Fact]
        public async Task TheDrainWorkerAppliesAllThreeSourceTypesAndRemovesTheRow()
        {
            var player = await CreatePlayerAsync();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Offline production's shape.
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    db, player.Id, PendingGrantSourceType.OfflineVillageProduction,
                    new Dictionary<string, long> { ["birch_log"] = 12L });

                // Gathering's shape - same PayloadKind, different SourceType,
                // different material.
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    db, player.Id, PendingGrantSourceType.Gathering,
                    new Dictionary<string, long> { ["copper_ore"] = 7L });

                // Combat loot's equipment shape.
                await PendingGrantOutbox.EnqueueEquipmentGrantAsync(
                    db, player.Id, PendingGrantSourceType.CombatLoot,
                    baseItemId: "eq_test_helmet_slot_base", qualityTier: 3, affixPayload: "{}");
            }

            var engine = new PendingGrantDrainEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await engine.DrainOneCycleAsync();

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await verify.PendingGrants.AsNoTracking().AnyAsync(g => g.PlayerId == player.Id),
                "every row should have been applied and removed in one cycle - three rows is nowhere near the 100-row budget");

            Assert.Equal(12L, await QuantityAsync(player.Id, "birch_log"));
            Assert.Equal(7L, await QuantityAsync(player.Id, "copper_ore"));
            Assert.Equal(1, await verify.EquipmentInstances.AsNoTracking()
                .CountAsync(e => e.PlayerId == player.Id && e.BaseItemId == "eq_test_helmet_slot_base" && e.QualityTier == 3));
        }

        private async Task<long> QuantityAsync(long playerId, string itemId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var record = await db.CommodityRecords.AsNoTracking()
                .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == itemId);
            return record?.Quantity ?? 0L;
        }

        [Fact]
        public async Task BackoffActuallyBacksOff()
        {
            var player = await CreatePlayerAsync();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long futureMs = nowMs + 60_000L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    db, player.Id, PendingGrantSourceType.Gathering,
                    new Dictionary<string, long> { ["stone"] = 3L });
                // Push it into the future and bump AttemptCount, as if two
                // earlier cycles had already failed it.
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE pending_grants SET ""NextAttemptAtEpochMs"" = {futureMs}, ""AttemptCount"" = 3
                    WHERE ""PlayerId"" = {player.Id}");
            }

            var engine = new PendingGrantDrainEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await engine.DrainOneCycleAsync();

            await using (var check = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var row = await check.PendingGrants.AsNoTracking().SingleAsync(g => g.PlayerId == player.Id);
                Assert.Equal(3, row.AttemptCount);
                Assert.Equal(futureMs, row.NextAttemptAtEpochMs);
            }
            Assert.Equal(0L, await QuantityAsync(player.Id, "stone"));

            // Now make it eligible - the same row must be picked up and
            // applied on the very next cycle.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE pending_grants SET ""NextAttemptAtEpochMs"" = {nowMs}
                    WHERE ""PlayerId"" = {player.Id}");
            }
            await engine.DrainOneCycleAsync();

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await verify.PendingGrants.AsNoTracking().AnyAsync(g => g.PlayerId == player.Id));
            Assert.Equal(3L, await QuantityAsync(player.Id, "stone"));
        }

        [Fact]
        public async Task GiveUpIsPermanentAndCorrect()
        {
            const long poisonPlayerId = 8_400_000_001L;
            await InstallCommodityWritePoisonAsync(poisonPlayerId);
            try
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                        db, poisonPlayerId, PendingGrantSourceType.Gathering,
                        new Dictionary<string, long> { ["stone"] = 1L });
                    // As if nine earlier cycles had already failed it - the
                    // TENTH attempt (MaxAttempts) below must be the one that
                    // gives up for good.
                    await db.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE pending_grants SET ""AttemptCount"" = {PendingGrantDrainEngine.MaxAttempts - 1}
                        WHERE ""PlayerId"" = {poisonPlayerId}");
                }

                var engine = new PendingGrantDrainEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
                await engine.DrainOneCycleAsync();

                PendingGrant row;
                await using (var check = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    row = await check.PendingGrants.AsNoTracking().SingleAsync(g => g.PlayerId == poisonPlayerId);
                }
                Assert.Equal(PendingGrantDrainEngine.MaxAttempts, row.AttemptCount);
                Assert.NotNull(row.DeadLetteredAtEpochMs);
                long deadLetteredAt = row.DeadLetteredAtEpochMs!.Value;

                // A second cycle must not touch it again - the eligible-rows
                // query filters on DeadLetteredAtEpochMs IS NULL.
                await engine.DrainOneCycleAsync();

                await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
                var rowAfter = await verify.PendingGrants.AsNoTracking().SingleAsync(g => g.PlayerId == poisonPlayerId);
                Assert.Equal(PendingGrantDrainEngine.MaxAttempts, rowAfter.AttemptCount);
                Assert.Equal(deadLetteredAt, rowAfter.DeadLetteredAtEpochMs);
            }
            finally
            {
                await RemoveCommodityWritePoisonAsync();
            }
        }

        [Fact]
        public async Task ABudgetedCycleDoesNotStarveOnALargeBacklog()
        {
            const int totalRows = PendingGrantDrainEngine.MaxGrantsPerDrainCycle + 20;
            const long basePlayerId = 8_500_000_000L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                for (int i = 0; i < totalRows; i++)
                {
                    await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                        db, basePlayerId + i, PendingGrantSourceType.Gathering,
                        new Dictionary<string, long> { ["stone"] = 1L });
                }
            }

            var engine = new PendingGrantDrainEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await engine.DrainOneCycleAsync();

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            int remaining = await verify.PendingGrants.AsNoTracking()
                .CountAsync(g => g.PlayerId >= basePlayerId && g.PlayerId < basePlayerId + totalRows);

            Assert.Equal(20, remaining);
        }

        [Fact]
        public async Task ALiveSessionSeesARetriedGrantWithoutRelogging()
        {
            var goldPlayer = await CreatePlayerAsync();
            var materialsPlayer = await CreatePlayerAsync();
            _fixture.PlayerRegistry.RegisterPlayer(goldPlayer.Id);
            _fixture.PlayerRegistry.RegisterPlayer(materialsPlayer.Id);

            try
            {
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                        db, goldPlayer.Id, PendingGrantSourceType.OfflineVillageProduction,
                        new Dictionary<string, long> { ["gold"] = 42L, ["birch_log"] = 3L });
                    await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                        db, materialsPlayer.Id, PendingGrantSourceType.Gathering,
                        new Dictionary<string, long> { ["copper_ore"] = 9L });
                }

                var engine = new PendingGrantDrainEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
                await engine.DrainOneCycleAsync();

                // Gold: the display-only ChestSaleGoldQueue channel, exactly
                // once, for the gold-bearing grant only.
                Assert.Equal(1, _fixture.PlayerRegistry.ChestSaleGoldQueue
                    .Count(n => n.PlayerId == goldPlayer.Id && n.GoldGained == 42L));
                Assert.Equal(0, _fixture.PlayerRegistry.ChestSaleGoldQueue
                    .Count(n => n.PlayerId == materialsPlayer.Id));

                // Materials and equipment: an ordinary Success command result
                // is what drives the client's global cache invalidation - both
                // players get one, whether or not their grant included gold.
                byte success = (byte)FolkIdle.Server.Network.CommandResultCode.Success;
                Assert.Contains(_fixture.PlayerRegistry.CommandResultQueue,
                    n => n.PlayerId == goldPlayer.Id && n.ResultCode == success);
                Assert.Contains(_fixture.PlayerRegistry.CommandResultQueue,
                    n => n.PlayerId == materialsPlayer.Id && n.ResultCode == success);
            }
            finally
            {
                _fixture.PlayerRegistry.UnregisterPlayer(goldPlayer.Id);
                _fixture.PlayerRegistry.UnregisterPlayer(materialsPlayer.Id);
            }
        }
    }
}

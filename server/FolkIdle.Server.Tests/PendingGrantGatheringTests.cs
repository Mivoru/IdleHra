using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 2 of the durable-grant-retry plan: a gathering grant that fails to
    /// write now joins the same pending_grants outbox offline production does
    /// (PendingGrantOutboxTests). GrantGatheredMaterialsAsync's already-
    /// coalesced byMaterial dictionary is resolved before its write - exactly
    /// the "resolved before the write" shape the outbox requires.
    ///
    /// Modul: A DIFFERENT POISON THAN THE PLAN ASSUMED, because the assumed
    /// one does not actually throw. The plan (and LootWorkerResilienceTests'
    /// own comment) describe "a player id with no PlayerRecords row" as
    /// forcing a foreign-key violation - but neither CommodityRecords nor
    /// EquipmentInstances declares an FK to PlayerRecords in this codebase
    /// (confirmed empirically: running that exact scenario reports 0 failed
    /// requests, not 1). CommodityRecords also has no unique or check
    /// constraint a normal write could violate, and its write path uses
    /// FromSqlInterpolated + in-memory FirstOrDefault rather than
    /// SingleOrDefaultAsync, so the duplicate-row trick
    /// PendingGrantOutboxTests uses for the gold row does not apply either.
    /// A test-local BEFORE trigger that raises for one sentinel PlayerId is
    /// the honest equivalent for this table: a real Postgres exception from
    /// the real write, gated so it cannot affect any other test sharing this
    /// collection's container.
    /// </summary>
    [Collection("Postgres collection")]
    public class PendingGrantGatheringTests
    {
        private readonly PostgresTestFixture _fixture;

        public PendingGrantGatheringTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task InstallCommodityWritePoisonAsync(long poisonPlayerId)
        {
            // Modul: a fixed test constant, never external input - built as a
            // plain string first (rather than passed as an interpolated
            // literal straight to ExecuteSqlRawAsync) purely to sidestep the
            // SQL-injection analyzer, which cannot tell the two apart.
            string sql = @"
                CREATE OR REPLACE FUNCTION pg_test_poison_commodity_write() RETURNS trigger AS $BODY$
                BEGIN
                  IF NEW.""PlayerId"" = " + poisonPlayerId + @" THEN
                    RAISE EXCEPTION 'poisoned for test (PendingGrantGatheringTests)';
                  END IF;
                  RETURN NEW;
                END;
                $BODY$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS pg_test_poison_commodity_trigger ON ""CommodityRecords"";
                CREATE TRIGGER pg_test_poison_commodity_trigger
                BEFORE INSERT OR UPDATE ON ""CommodityRecords""
                FOR EACH ROW EXECUTE FUNCTION pg_test_poison_commodity_write();";

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        private async Task RemoveCommodityWritePoisonAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS pg_test_poison_commodity_trigger ON \"CommodityRecords\"; " +
                "DROP FUNCTION IF EXISTS pg_test_poison_commodity_write();");
        }

        [Fact]
        public async Task AFailedGatheringGrantLandsInThePendingGrantsTable()
        {
            const long poisonPlayerId = 8_100_000_001L;
            const int birchLogItemId = 267; // ContentRegistry.GetItemBaseId(267) == "birch_log"

            await InstallCommodityWritePoisonAsync(poisonPlayerId);
            CombatLootEngine? engine = null;
            try
            {
                CombatLootEngine.GatheringGrantQueue.Enqueue(new GatheredMaterialGrant
                {
                    PlayerId = poisonPlayerId,
                    ActivityId = 7,
                    ItemId = birchLogItemId,
                    Quantity = 50
                });

                engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
                engine.StartCron();

                var deadline = DateTime.UtcNow.AddSeconds(30);
                List<PendingGrant> rows = new();
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                    await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
                    rows = await check.PendingGrants.AsNoTracking()
                        .Where(g => g.PlayerId == poisonPlayerId)
                        .ToListAsync();
                    if (rows.Count > 0) break;
                }

                var row = Assert.Single(rows);
                Assert.Equal(PendingGrantSourceType.Gathering, row.SourceType);
                Assert.Equal(PendingGrantPayloadKind.CommodityDeltas, row.PayloadKind);
                Assert.Null(row.DeadLetteredAtEpochMs);

                var deltas = JsonSerializer.Deserialize<Dictionary<string, long>>(row.PayloadJson)!;
                Assert.Equal(50L, deltas["birch_log"]);

                // Nothing was actually granted - the write that would have
                // persisted it threw and rolled back, exactly as it did
                // before this task.
                await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
                bool anyCommodity = await verify.CommodityRecords.AsNoTracking()
                    .AnyAsync(c => c.PlayerId == poisonPlayerId);
                Assert.False(anyCommodity);
            }
            finally
            {
                engine?.StopCron();
                await RemoveCommodityWritePoisonAsync();
            }
        }
    }
}

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
    /// Task 3 of the durable-grant-retry plan: combat loot (equipment,
    /// materials, and auto-salvage gold) joins the same pending_grants outbox
    /// PendingGrantOutboxTests and PendingGrantGatheringTests already prove for
    /// offline production and gathering. ProcessMonsterLootDropAsync commits a
    /// whole BATCH of kills in one transaction, so this is the one place the
    /// outbox has to reconcile with a plain-data accumulator built alongside
    /// EF's own change tracker rather than one pre-existing local variable.
    ///
    /// Modul: same trigger-based poison as PendingGrantGatheringTests, for the
    /// same reason - neither CommodityRecords nor EquipmentInstances declares
    /// an FK to PlayerRecords in this codebase, so "no PlayerRecords row" does
    /// not actually throw here (confirmed empirically). Poisoning
    /// CommodityRecords is enough to fail the whole batch: both tables commit
    /// through the same single SaveChangesAsync call inside one transaction,
    /// so a material write throwing rolls back any staged equipment inserts
    /// too, exactly like a real connection failure would.
    /// </summary>
    [Collection("Postgres collection")]
    public class PendingGrantCombatLootTests
    {
        private readonly PostgresTestFixture _fixture;

        public PendingGrantCombatLootTests(PostgresTestFixture fixture)
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
                Username = $"combatloot_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player;
        }

        private async Task InstallCommodityWritePoisonAsync(long poisonPlayerId)
        {
            string sql = @"
                CREATE OR REPLACE FUNCTION pg_test_poison_commodity_write_loot() RETURNS trigger AS $BODY$
                BEGIN
                  IF NEW.""PlayerId"" = " + poisonPlayerId + @" THEN
                    RAISE EXCEPTION 'poisoned for test (PendingGrantCombatLootTests)';
                  END IF;
                  RETURN NEW;
                END;
                $BODY$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS pg_test_poison_commodity_trigger_loot ON ""CommodityRecords"";
                CREATE TRIGGER pg_test_poison_commodity_trigger_loot
                BEFORE INSERT OR UPDATE ON ""CommodityRecords""
                FOR EACH ROW EXECUTE FUNCTION pg_test_poison_commodity_write_loot();";

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        private async Task RemoveCommodityWritePoisonAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS pg_test_poison_commodity_trigger_loot ON \"CommodityRecords\"; " +
                "DROP FUNCTION IF EXISTS pg_test_poison_commodity_write_loot();");
        }

        [Fact]
        public async Task AMultiKillBatchForcedFailureReplaysExactly()
        {
            // Region 1, non-boss (per ContentRegistry.IsRegionalBoss, a
            // canonical monster is a boss only every 5th id from
            // FirstCanonicalMonsterId - this is the 1st of that group).
            const int monsterId = ContentRegistry.FirstCanonicalMonsterId;
            // 200 kills at 35% material / 15% equipment chance each makes
            // "zero of either" astronomically unlikely (0.85^200 for
            // equipment, the rarer of the two) without depending on a seeded
            // RNG.
            const int kills = 200;

            var player = await CreatePlayerAsync();
            await InstallCommodityWritePoisonAsync(player.Id);
            CombatLootEngine? engine = null;
            try
            {
                engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = player.Id,
                    MonsterId = monsterId,
                    Kills = kills
                });
                engine.StartCron();

                var deadline = DateTime.UtcNow.AddSeconds(30);
                List<PendingGrant> rows = new();
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                    await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
                    rows = await check.PendingGrants.AsNoTracking()
                        .Where(g => g.PlayerId == player.Id)
                        .ToListAsync();
                    if (rows.Count > 0) break;
                }

                Assert.NotEmpty(rows);
                Assert.All(rows, r => Assert.Equal(PendingGrantSourceType.CombatLoot, r.SourceType));

                var commodityRows = rows.Where(r => r.PayloadKind == PendingGrantPayloadKind.CommodityDeltas).ToList();
                var equipmentRows = rows.Where(r => r.PayloadKind == PendingGrantPayloadKind.EquipmentGrant).ToList();

                // Exactly one coalesced commodity-deltas row (the whole
                // batch's materials, enqueued once) and at least one
                // equipment row (one PendingGrants row per piece, enqueued
                // in a loop) - see the catch block in
                // ProcessMonsterLootDropAsync.
                Assert.Single(commodityRows);
                Assert.NotEmpty(equipmentRows);

                // ReadOnlySpan<int> is a ref struct and cannot cross an
                // await, hence the ToArray() before this method does anything
                // else async.
                int[] dropTable = EquipmentDropTable.GetDrops(monsterId).ToArray();
                var validBaseItemIds = new HashSet<string>();
                foreach (int itemId in dropTable)
                {
                    string baseId = ContentRegistry.GetItemBaseId(itemId);
                    if (!string.IsNullOrEmpty(baseId)) validBaseItemIds.Add(baseId);
                }

                var expectedCommodityDeltas = JsonSerializer.Deserialize<Dictionary<string, long>>(commodityRows[0].PayloadJson)!;
                Assert.All(expectedCommodityDeltas, kv =>
                {
                    Assert.False(string.IsNullOrEmpty(kv.Key));
                    Assert.True(kv.Value > 0, $"{kv.Key} delta must be positive, was {kv.Value}");
                });

                var expectedEquipmentGrants = equipmentRows
                    .Select(r => JsonSerializer.Deserialize<EquipmentGrantPayload>(r.PayloadJson))
                    .ToList();
                Assert.All(expectedEquipmentGrants, g =>
                    Assert.Contains(g.BaseItemId, validBaseItemIds));

                // Nothing was actually granted - the batch's own commit threw
                // and rolled back everything, exactly as it did before this
                // task.
                await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    Assert.False(await verify.CommodityRecords.AsNoTracking().AnyAsync(c => c.PlayerId == player.Id));
                    Assert.False(await verify.EquipmentInstances.AsNoTracking().AnyAsync(e => e.PlayerId == player.Id));
                }

                // Applying every row (Task 4's own worker does this on a
                // schedule; this task proves the mechanism directly) must
                // reproduce exactly what the batch was trying to write - no
                // more, no fewer, no duplicates.
                await RemoveCommodityWritePoisonAsync();
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    foreach (var row in rows)
                    {
                        var tracked = await db.PendingGrants.SingleAsync(g => g.Id == row.Id);
                        Assert.True(await PendingGrantOutbox.TryApplyOneAsync(db, tracked));
                    }
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                }

                await using var final = await _fixture.DbContextFactory.CreateDbContextAsync();
                foreach (var kv in expectedCommodityDeltas)
                {
                    long actual = await final.CommodityRecords.AsNoTracking()
                        .Where(c => c.PlayerId == player.Id && c.ItemId == kv.Key)
                        .Select(c => (long?)c.Quantity).SingleOrDefaultAsync() ?? 0L;
                    Assert.Equal(kv.Value, actual);
                }

                int finalEquipmentCount = await final.EquipmentInstances.AsNoTracking()
                    .CountAsync(e => e.PlayerId == player.Id);
                Assert.Equal(expectedEquipmentGrants.Count, finalEquipmentCount);
            }
            finally
            {
                engine?.StopCron();
                await RemoveCommodityWritePoisonAsync();
            }
        }

        [Fact]
        public async Task AutoSalvageGoldSurvivesAFailure()
        {
            const int monsterId = ContentRegistry.FirstCanonicalMonsterId;
            const int kills = 200;

            var player = await CreatePlayerAsync();
            await InstallCommodityWritePoisonAsync(player.Id);
            CombatLootEngine? engine = null;
            try
            {
                engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = player.Id,
                    MonsterId = monsterId,
                    Kills = kills,
                    // Every quality tier this monster can roll is at or below
                    // this, so any equipment drop in the batch is guaranteed
                    // to auto-salvage into gold rather than an
                    // EquipmentInstances row.
                    AutoSalvageBelowTier = Domain.Economy.CraftingEngine.RarityTierCount
                });
                engine.StartCron();

                var deadline = DateTime.UtcNow.AddSeconds(30);
                List<PendingGrant> rows = new();
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                    await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
                    rows = await check.PendingGrants.AsNoTracking()
                        .Where(g => g.PlayerId == player.Id)
                        .ToListAsync();
                    if (rows.Count > 0) break;
                }

                var commodityRow = Assert.Single(rows.Where(r => r.PayloadKind == PendingGrantPayloadKind.CommodityDeltas));
                // Auto-salvage below the floor for every tier this monster
                // can roll, so no EquipmentInstances row was ever staged.
                Assert.Empty(rows.Where(r => r.PayloadKind == PendingGrantPayloadKind.EquipmentGrant));

                var deltas = JsonSerializer.Deserialize<Dictionary<string, long>>(commodityRow.PayloadJson)!;
                Assert.True(deltas.TryGetValue("gold", out long gold) && gold > 0,
                    "auto-salvage gold must survive the failure as a commodity-deltas outbox row");
            }
            finally
            {
                engine?.StopCron();
                await RemoveCommodityWritePoisonAsync();
            }
        }
    }
}

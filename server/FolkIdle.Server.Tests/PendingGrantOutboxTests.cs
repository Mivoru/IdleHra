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
    /// The durable retry outbox (audit #18): a grant whose database write
    /// failed used to just be logged and lost. `PendingGrantOutbox` persists
    /// the already-resolved outcome so a retry can replay it. This file
    /// proves the mechanism end-to-end on offline village production - the
    /// simplest of the three grant paths, since every delta is a plain,
    /// fully-computed `long` before the write that can fail.
    /// </summary>
    [Collection("Postgres collection")]
    public class PendingGrantOutboxTests
    {
        private readonly PostgresTestFixture _fixture;

        public PendingGrantOutboxTests(PostgresTestFixture fixture)
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
                Username = $"outbox_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player;
        }

        private async Task<long> QuantityAsync(long playerId, string itemId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var record = await db.CommodityRecords.AsNoTracking()
                .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == itemId);
            return record?.Quantity ?? 0L;
        }

        // Modul: same poison technique OfflineVillageProductionTests already
        // uses for audit #17 - two CommodityRecords rows for the same
        // (PlayerId, "gold") make the goldRecord SingleOrDefaultAsync throw
        // "Sequence contains more than one element", the identical shape a
        // live race or a dropped connection produces from the caller's point
        // of view.
        private async Task SeedDuplicateGoldRowsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 0L });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 0L });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task AFailedOfflineProductionGrantLandsInThePendingGrantsTable()
        {
            var player = await CreatePlayerAsync();
            await SeedDuplicateGoldRowsAsync(player.Id);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // lumberjackLevel/mineLevel 1 (tier 0 materials), warehouseLevel
                // 100 (no capacity clamp anywhere near these numbers),
                // townHallLevel 1 (the 50g/h floor), 3600 elapsed seconds:
                // 200 wood/ore each before the 90/10 rare split -> 180
                // common + 20 rare each, 50 gold. All five deltas are
                // resolved, plain longs before the poisoned gold read below
                // throws and rolls the whole grant back.
                long overflow = await OfflineSimulationEngine.GrantVillagePassiveProductionAsync(
                    db, player.Id, lumberjackLevel: 1, mineLevel: 1, warehouseLevel: 100, townHallLevel: 1, elapsedSeconds: 3600L);

                Assert.Equal(0L, overflow); // committed == false, so nothing was clamped either.
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await verify.PendingGrants.AsNoTracking()
                .Where(g => g.PlayerId == player.Id)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal(PendingGrantSourceType.OfflineVillageProduction, row.SourceType);
            Assert.Equal(PendingGrantPayloadKind.CommodityDeltas, row.PayloadKind);
            Assert.Null(row.DeadLetteredAtEpochMs);
            Assert.Equal(0, row.AttemptCount);

            var deltas = JsonSerializer.Deserialize<Dictionary<string, long>>(row.PayloadJson)!;
            Assert.Equal(180L, deltas["birch_log"]);
            Assert.Equal(180L, deltas["copper_ore"]);
            Assert.Equal(20L, deltas["golden_birch_log"]);
            Assert.Equal(20L, deltas["malachite_ore"]);
            Assert.Equal(50L, deltas["gold"]);

            // Nothing was actually granted - the transaction that would have
            // written these rolled back, exactly as it did before this task.
            Assert.Equal(0L, await QuantityAsync(player.Id, "birch_log"));
        }

        [Fact]
        public async Task ApplyingAPendingGrantDeliversTheReward()
        {
            var player = await CreatePlayerAsync();

            var deltas = new Dictionary<string, long> { ["birch_log"] = 42L, ["gold"] = 7L };
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await PendingGrantOutbox.EnqueueCommodityDeltasAsync(
                    db, player.Id, PendingGrantSourceType.OfflineVillageProduction, deltas);
            }

            PendingGrant row;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                row = await db.PendingGrants.SingleAsync(g => g.PlayerId == player.Id);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                bool applied = await PendingGrantOutbox.TryApplyOneAsync(db, row);
                Assert.True(applied);
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            Assert.Equal(42L, await QuantityAsync(player.Id, "birch_log"));
            Assert.Equal(7L, await QuantityAsync(player.Id, "gold"));
        }

        [Fact]
        public async Task DuplicateEnqueueForTheSameIdempotencyKeyIsANoOp()
        {
            var player = await CreatePlayerAsync();
            const int sourceType = PendingGrantSourceType.Gathering;
            const long sourceSequence = 999_888_777L; // fixed, deterministic - not minted via the counter.
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string payloadJson = JsonSerializer.Serialize(new Dictionary<string, long> { ["stone"] = 5L });

            for (int i = 0; i < 2; i++)
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO pending_grants
                        (""PlayerId"", ""SourceType"", ""SourceSequence"", ""PayloadKind"", ""PayloadJson"",
                         ""CreatedAtEpochMs"", ""NextAttemptAtEpochMs"", ""AttemptCount"")
                    VALUES
                        ({player.Id}, {sourceType}, {sourceSequence}, {PendingGrantPayloadKind.CommodityDeltas}, {payloadJson},
                         {nowMs}, {nowMs}, 0)
                    ON CONFLICT (""PlayerId"", ""SourceType"", ""SourceSequence"") DO NOTHING");
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            int count = await verify.PendingGrants.CountAsync(g =>
                g.PlayerId == player.Id && g.SourceType == sourceType && g.SourceSequence == sourceSequence);
            Assert.Equal(1, count);
        }

        // Modul: audit #19's overflow accounting and this task's outbox touch
        // adjacent but distinct code in GrantVillagePassiveProductionAsync -
        // this pins that a SUCCESSFUL grant against a near-full warehouse
        // still reports its overflow correctly and enqueues nothing, so
        // neither feature's presence changes the other's behavior.
        [Fact]
        public async Task ASuccessfulNearFullWarehouseGrantEnqueuesNothing()
        {
            var player = await CreatePlayerAsync();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "birch_log", Quantity = 990L });
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                long overflow = await OfflineSimulationEngine.GrantVillagePassiveProductionAsync(
                    db, player.Id, lumberjackLevel: 1, mineLevel: 0, warehouseLevel: 1, townHallLevel: 0, elapsedSeconds: 36000L);

                // 10 hours at level 1 (200/hour, 90/10 split -> 180 common +
                // 20 rare per hour) requests 1000 common wood before the
                // clamp; with 990 already stored against a 1,000-cap
                // warehouse, only 10 more fits - 890 lost, matching
                // OfflineVillageProductionOverflowTests' own math.
                Assert.Equal(890L, overflow);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            int pending = await verify.PendingGrants.CountAsync(g => g.PlayerId == player.Id);
            Assert.Equal(0, pending);
        }
    }
}

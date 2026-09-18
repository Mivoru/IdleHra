using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: audit #19 - village passive production discards overflow with no
    // record. GrantVillagePassiveProductionAsync clamps a catch-up grant twice
    // (once against the window's own theoretical ceiling, once against LIVE
    // warehouse storage in GrantSingleCommodityProductionAsync) and, before
    // this fix, simply dropped whatever the second clamp cut - a warehouse at
    // capacity silently ate the rest of a player's offline production and told
    // nobody. This file pins that the clamped-away amount is now summed and
    // returned, matching TickStatePayload.OfflineMaterialsLostToFullWarehouse's
    // own doc comment.
    [Collection("Postgres collection")]
    public class OfflineVillageProductionOverflowTests
    {
        private readonly PostgresTestFixture _fixture;

        public OfflineVillageProductionOverflowTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<PlayerRecord> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"villover_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };

            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player;
        }

        private async Task SeedCommodityAsync(long playerId, string itemId, long quantity)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = itemId, Quantity = quantity });
            await db.SaveChangesAsync();
        }

        private async Task<long> QuantityAsync(long playerId, string itemId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var record = await db.CommodityRecords.AsNoTracking()
                .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == itemId);
            return record?.Quantity ?? 0L;
        }

        // Modul: near-full on BOTH the common log and the common ore, at
        // tier 0 (lumberjackLevel/mineLevel = 1 both round down to
        // GetTierMaterials(0): birch_log/copper_ore common,
        // golden_birch_log/malachite_ore rare). Ten hours at level 1
        // (200/hour each) against a level-1 warehouse (1,000 cap) requests
        // 1,000 of each before the 90/10 rare split - deliberately over the
        // cap so the window-ceiling clamp does not itself already cap the
        // request. With 990 of each common material already stored, only 10
        // more of each fits: 890 lost per common material, 0 lost on the
        // rares (fresh rows, plenty of headroom).
        [Fact]
        public async Task NearFullWarehouse_SumsOverflowAcrossAllFourMaterials()
        {
            var player = await CreatePlayerAsync();

            await SeedCommodityAsync(player.Id, "birch_log", 990L);
            await SeedCommodityAsync(player.Id, "copper_ore", 990L);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long overflow = await OfflineSimulationEngine.GrantVillagePassiveProductionAsync(
                db,
                playerId: player.Id,
                lumberjackLevel: 1,
                mineLevel: 1,
                warehouseLevel: 1,
                townHallLevel: 0,
                elapsedSeconds: 36000L);

            Assert.Equal(1780L, overflow);

            // Both commons landed exactly at the warehouse cap, not above it -
            // the clamp did its job; only the OVERFLOW REPORTING was missing.
            Assert.Equal(1000L, await QuantityAsync(player.Id, "birch_log"));
            Assert.Equal(1000L, await QuantityAsync(player.Id, "copper_ore"));

            // The rares had room for the whole grant.
            Assert.Equal(100L, await QuantityAsync(player.Id, "golden_birch_log"));
            Assert.Equal(100L, await QuantityAsync(player.Id, "malachite_ore"));
        }

        // The other half of the guard: a warehouse with headroom must report
        // exactly zero, not some leftover figure from the theoretical clamp.
        [Fact]
        public async Task WarehouseWithRoom_ReportsZeroOverflow()
        {
            var player = await CreatePlayerAsync();

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long overflow = await OfflineSimulationEngine.GrantVillagePassiveProductionAsync(
                db,
                playerId: player.Id,
                lumberjackLevel: 1,
                mineLevel: 0,
                warehouseLevel: 100,
                townHallLevel: 0,
                elapsedSeconds: 3600L);

            Assert.Equal(0L, overflow);

            // 1 hour at level 1 (200/hour), 90/10 split: 180 common + 20 rare,
            // both nowhere near a 100,000-capacity warehouse.
            Assert.Equal(180L, await QuantityAsync(player.Id, "birch_log"));
            Assert.Equal(20L, await QuantityAsync(player.Id, "golden_birch_log"));
        }

        // The zero-elapsed and zero-production early returns must not report a
        // stale or partial overflow either - there is nothing to have lost.
        [Fact]
        public async Task NoElapsedTime_ReportsZeroOverflow()
        {
            var player = await CreatePlayerAsync();

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long overflow = await OfflineSimulationEngine.GrantVillagePassiveProductionAsync(
                db,
                playerId: player.Id,
                lumberjackLevel: 1,
                mineLevel: 1,
                warehouseLevel: 1,
                townHallLevel: 0,
                elapsedSeconds: 0L);

            Assert.Equal(0L, overflow);
        }
    }
}

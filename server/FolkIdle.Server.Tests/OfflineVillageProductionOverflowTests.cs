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

        // Modul: THE WAREHOUSE IS ITS OWN STORE (2026-11-07). Tier 0
        // (lumberjackLevel/mineLevel = 1 both round down to
        // GetTierMaterials(0): birch_log/copper_ore common,
        // golden_birch_log/malachite_ore rare). Ten hours at level 1 (200 an
        // hour, +5% for the level-1 Warehouse = 210) is 2,100 of each: 1,890
        // common and 210 rare. The Warehouse holds 1,000 of each material for
        // the window, so 890 of each common is lost and the rares fit. The 990
        // already in the chest does NOT count any more - before, it left room
        // for 10 and 1,790 of each common was thrown away.
        [Fact]
        public async Task FullWarehouse_CapsTheWindow_NotTheChest()
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

            // A full Warehouse's worth of each common landed on top of the stack.
            Assert.Equal(1990L, await QuantityAsync(player.Id, "birch_log"));
            Assert.Equal(1990L, await QuantityAsync(player.Id, "copper_ore"));

            // The rares fit whole.
            Assert.Equal(210L, await QuantityAsync(player.Id, "golden_birch_log"));
            Assert.Equal(210L, await QuantityAsync(player.Id, "malachite_ore"));
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

            // 1 hour at level 1 (200/hour), +60% (the Warehouse bonus stops at
            // level 12) = 320, 90/10 split: 288 common + 32 rare, both nowhere
            // near a 100,000-capacity warehouse.
            Assert.Equal(288L, await QuantityAsync(player.Id, "birch_log"));
            Assert.Equal(32L, await QuantityAsync(player.Id, "golden_birch_log"));
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

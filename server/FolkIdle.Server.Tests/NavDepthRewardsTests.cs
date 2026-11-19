using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>Every depth of Nav pays once (owner, 2026-11-19): the table and its rhythm.</summary>
    public class NavDepthRewardTableTests
    {
        [Fact]
        public void PlainDepthsPayBronze_FifthsGold_TenthsAncientWithDiamondsAndTickets()
        {
            var plain = NavRules.DepthRewardFor(1, 7);
            Assert.Equal(LootChestTier.Bronze, plain.Chest);
            Assert.Equal(NavRules.DepthGoldMinutes, plain.GoldMinutes);
            Assert.Equal(0, plain.Diamonds);
            Assert.Equal(0, plain.TicketsPerGate);

            Assert.Equal(LootChestTier.Gold, NavRules.DepthRewardFor(1, 15).Chest);

            var tenth = NavRules.DepthRewardFor(1, 20);
            Assert.Equal(LootChestTier.Ancient, tenth.Chest);
            Assert.Equal(NavRules.DepthDiamondsPerCircle, tenth.Diamonds);
            Assert.Equal(1, tenth.TicketsPerGate);
        }

        [Fact]
        public void DeeperCirclesPayMore_AndTheFiftiethOpensTheNextWithABonus()
        {
            var c3 = NavRules.DepthRewardFor(3, 30);
            Assert.Equal(NavRules.DepthGoldMinutes * 3, c3.GoldMinutes);
            Assert.Equal(NavRules.DepthDiamondsPerCircle * 3, c3.Diamonds);
            Assert.Equal(NavRules.DepthDiamondsPerCircle + NavRules.CircleOpenDiamonds, NavRules.DepthRewardFor(1, NavRules.CircleOpensAt).Diamonds);
        }

        [Fact]
        public void TheFirstBuildsMilestonesAreRecognised()
        {
            long mask = 1L << NavRules.LegacyMilestoneBit(1, 0) | 1L << NavRules.LegacyMilestoneBit(2, 1);
            Assert.True(NavRules.LegacyChestPaid(mask, 1, 10));
            Assert.False(NavRules.LegacyChestPaid(mask, 1, 25));
            Assert.True(NavRules.LegacyChestPaid(mask, 2, 25));
            Assert.False(NavRules.LegacyChestPaid(mask, 1, 11));
        }

        [Fact]
        public void PayoutsAdd()
        {
            var sum = new NavPayout(10, 1) + new NavPayout(5, 2);
            Assert.Equal(new NavPayout(15, 3), sum);
            Assert.False(NavPayout.None.Any);
        }
    }

    /// <summary>The per-depth rewards against a real database: chests, tickets, gold, diamonds, the one-time back-pay.</summary>
    [Collection("Postgres collection")]
    public class NavDepthRewardDbTests
    {
        private readonly PostgresTestFixture _fixture;

        public NavDepthRewardDbTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task SeedAsync(long playerId, Action<NavProgress> shape)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            if (!await db.PlayerRecords.AnyAsync(p => p.Id == playerId))
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = $"navr{playerId}" });
            var row = await db.NavProgress.FirstOrDefaultAsync(n => n.PlayerId == playerId);
            if (row == null) { row = new NavProgress { PlayerId = playerId }; db.NavProgress.Add(row); }
            shape(row);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task TheBackPayPaysEveryOldDepthOnce_MinusTheMilestoneChests()
        {
            const long id = 960_450_001L;
            // Circle 1 to depth 12, with the first build's depth-10 Silver already paid.
            await SeedAsync(id, r =>
            {
                r.BestDepths = "1:12";
                r.MilestoneMask = 1L << NavRules.LegacyMilestoneBit(1, 0);
                r.DepthRewardsVersion = 0;
            });

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (lines, payout) = await NavEngine.SettleBacklogAsync(db, id, DateTime.UtcNow);
                Assert.Single(lines);
                Assert.Contains("Depths 1-12 of Circle 1", lines[0]);
                Assert.Equal(NavRules.DepthDiamondsPerCircle, payout.Diamonds);
                Assert.True(payout.Gold > 0);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var chests = await db.LootChests.AsNoTracking().Where(c => c.PlayerId == id).ToListAsync();
                // 12 depths, the 10th's Ancient skipped (its Silver was paid): 10 Bronze + the 5th's Gold.
                Assert.Equal(11, chests.Count);
                Assert.Equal(1, chests.Count(c => c.Tier == (byte)LootChestTier.Gold));
                Assert.Equal(0, chests.Count(c => c.Tier == (byte)LootChestTier.Ancient));
                Assert.All(chests, c => Assert.Equal((byte)LootChestSource.Nav, c.Source));

                var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == id);
                Assert.Equal(NavRules.DepthDiamondsPerCircle, player.PremiumDiamonds);
                long gold = await db.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == id && c.ItemId == "gold").Select(c => c.Quantity).SingleAsync();
                Assert.True(gold > 0);
                Assert.Equal(1, (await db.NavProgress.AsNoTracking().SingleAsync(n => n.PlayerId == id)).DepthRewardsVersion);
            }

            // A second visit pays nothing.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (lines, payout) = await NavEngine.SettleBacklogAsync(db, id, DateTime.UtcNow);
                Assert.Empty(lines);
                Assert.False(payout.Any);
                Assert.Equal(11, await db.LootChests.CountAsync(c => c.PlayerId == id));
            }
        }

        [Fact]
        public async Task ATenthDepthAddsADungeonTicketToEveryGate()
        {
            const long id = 960_450_002L;
            await SeedAsync(id, r => { r.DepthRewardsVersion = 1; });
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var before = (await DungeonEngine.LoadAsync(db, id, DateTime.UtcNow)).ToDictionary(kv => kv.Key, kv => kv.Value.Tickets);
            await db.SaveChangesAsync();

            var row = await db.NavProgress.SingleAsync(n => n.PlayerId == id);
            var lines = new List<string>();
            var payout = await NavEngine.PayDepthsAsync(db, row, 2, 19, 20, 600_000, DateTime.UtcNow, lines);
            await db.SaveChangesAsync();

            var after = await DungeonEngine.LoadAsync(db, id, DateTime.UtcNow);
            foreach (var kv in after) Assert.Equal(Math.Min(DungeonEngine.BonusTicketCap, before[kv.Key] + 1), kv.Value.Tickets);
            Assert.Equal(NavRules.DepthDiamondsPerCircle * 2, payout.Diamonds);
            // 2 depths x 10 minutes x Circle 2 of 600k an hour.
            Assert.Equal(GoldRewardScale.ForMinutes(600_000, 40), payout.Gold);
            Assert.Contains("Depths 19-20 of Circle 2", lines.Single());
            Assert.Equal(2, await db.LootChests.CountAsync(c => c.PlayerId == id));
        }
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Loot chests through a real database: a chest is granted, opened once,
    /// pays materials and gold into the wallet and the income ledger, and the
    /// row it was opened from is gone - so a second open grants nothing. The
    /// weekly diamond ceiling turns the excess into gold.
    /// </summary>
    [Collection("Postgres collection")]
    public class LootChestEngineTests
    {
        private readonly PostgresTestFixture _fixture;
        private static long _nextPlayerId = 957_000_000L;

        public LootChestEngineTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task<long> SeedAsync()
        {
            long playerId = Interlocked.Increment(ref _nextPlayerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
            await db.SaveChangesAsync();
            return playerId;
        }

        private async Task<long> HeldAsync(long playerId, string item)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == item).SumAsync(c => c.Quantity);
        }

        [Fact]
        public async Task AChestOpensOnce_AndPaysIntoTheWalletAndTheLedger()
        {
            long playerId = await SeedAsync();
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await LootChestEngine.GrantAsync(db, playerId, LootChestTier.Silver, 1, LootChestSource.Dev, now);
                await LootChestEngine.GrantAsync(db, playerId, LootChestTier.Silver, 1, LootChestSource.Dev, now);
                Assert.Equal(2, (await LootChestEngine.CountsAsync(db, playerId))[LootChestTier.Silver]);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var opened = await LootChestEngine.OpenAsync(db, playerId, LootChestTier.Silver, 5, now, new Random(1));
                Assert.Equal(2, opened.Count);
                long gold = opened.Sum(o => o.Roll.Gold + o.Roll.GoldInsteadOfDiamonds);
                Assert.True(gold > 0);
                Assert.Equal(gold, await HeldAsync(playerId, "gold"));

                var firstLine = opened[0].Roll.Materials[0];
                Assert.True(await HeldAsync(playerId, firstLine.ItemId) >= firstLine.Quantity);

                int pieces = opened.Count(o => o.Piece != null);
                Assert.Equal(pieces, await db.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
                Assert.All(opened.Where(o => o.Piece != null), o => Assert.True(o.Piece!.InstanceId > 0));

                Assert.Equal(gold, await db.GoldIncomeDaily.Where(g => g.PlayerId == playerId && g.Source == (short)GoldIncomeSource.LootChest).SumAsync(g => g.Amount));
            }

            // The rows are the ticket: nothing left to open, nothing granted.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Empty(await LootChestEngine.OpenAsync(db, playerId, LootChestTier.Silver, 5, now, new Random(2)));
                Assert.Empty(await LootChestEngine.CountsAsync(db, playerId));
            }
        }

        [Fact]
        public async Task TheWeeklyDiamondCeilingPaysTheRestInGold_AndResetsNextWeek()
        {
            long playerId = await SeedAsync();
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                for (int i = 0; i < 5; i++)
                    await LootChestEngine.GrantAsync(db, playerId, LootChestTier.Ancient, 2, LootChestSource.Dev, now);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Ancient always rolls 5-10: five of them pass a 15 ceiling.
                var opened = await LootChestEngine.OpenAsync(db, playerId, LootChestTier.Ancient, 5, now, new Random(3));
                Assert.Equal(LootChestEngine.MaxDiamondsPerWeek, opened.Sum(o => o.Roll.Diamonds));
                Assert.True(opened.Sum(o => o.Roll.GoldInsteadOfDiamonds) > 0);
                var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
                Assert.Equal(LootChestEngine.MaxDiamondsPerWeek, player.PremiumDiamonds);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await LootChestEngine.GrantAsync(db, playerId, LootChestTier.Ancient, 2, LootChestSource.Dev, now.AddDays(7));
                var nextWeek = await LootChestEngine.OpenAsync(db, playerId, LootChestTier.Ancient, 1, now.AddDays(7), new Random(4));
                Assert.InRange(nextWeek.Single().Roll.Diamonds, 5, 10);
            }
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Wardrobe threads against Postgres (owner, 2026-11-15): a duplicate
    /// unravels into threads instead of a copy, spare copies unravel on
    /// request, and threads buy a missing piece or a thread-only frame. Every
    /// balance and row is read back from its table.
    /// </summary>
    [Collection("Postgres collection")]
    public class WardrobeThreadsTests
    {
        private readonly PostgresTestFixture _fixture;
        public WardrobeThreadsTests(PostgresTestFixture fixture) => _fixture = fixture;

        private static readonly DateTime Now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

        private async Task<long> SeedPlayerAsync()
        {
            string tag = Guid.NewGuid().ToString("N");
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Email = $"threads_{tag}@example.com",
                Username = "Thr" + tag[..8],
                DeviceId = "threads-device-" + tag,
            };
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        private async Task AddPieceAsync(long playerId, CosmeticDefinition def, int copies = 1, bool listed = false)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            for (int i = 0; i < copies; i++)
            {
                db.CosmeticItems.Add(new CosmeticItem
                {
                    PlayerId = playerId, Kind = (byte)def.Kind, DefinitionId = def.Id, Rarity = (byte)def.Rarity,
                    Source = (byte)CosmeticSource.Kill, AcquiredAtUtc = Now, IsListed = listed && i == 0,
                });
            }
            await db.SaveChangesAsync();
        }

        private async Task<long> ThreadsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await WardrobeThreads.BalanceAsync(db, playerId);
        }

        [Fact]
        public async Task AChestThatRollsAnOwnedPieceGivesThreadsNotACopy()
        {
            long playerId = await SeedPlayerAsync();
            // Own the whole Common pool, so a Common roll can only be a duplicate.
            foreach (var def in CosmeticRegistry.ChestPool(CosmeticRegistry.Common)) await AddPieceAsync(playerId, def);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await CosmeticEngine.InsertChestsAsync(db, playerId, new[] { CosmeticRegistry.Common }, CosmeticSource.Kill, Now);
            }

            int before;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                before = await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.Kind != (byte)CosmeticKind.Chest);
            }

            // roll 0.0 lands in the Common band of a Common chest.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (result, opened) = await CosmeticEngine.OpenAsync(db, playerId, CosmeticRegistry.Common, new FixedRandom(0.0), Now);
                Assert.Equal(CosmeticResult.Ok, result);
                Assert.True(opened!.Duplicate);
                Assert.Equal(WardrobeThreads.PerDuplicate[CosmeticRegistry.Common], opened.Threads);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(before, await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.Kind != (byte)CosmeticKind.Chest));
                Assert.Equal(0, await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.Kind == (byte)CosmeticKind.Chest));
            }
            Assert.Equal(WardrobeThreads.PerDuplicate[CosmeticRegistry.Common], await ThreadsAsync(playerId));
        }

        [Fact]
        public async Task UnravelKeepsOneCopy_AndNeverTouchesListedOrBoundPieces()
        {
            long playerId = await SeedPlayerAsync();
            var epic = CosmeticRegistry.ChestPool(CosmeticRegistry.Epic).First();
            var common = CosmeticRegistry.ChestPool(CosmeticRegistry.Common).First();
            var bound = CosmeticRegistry.All.First(d => d.Bound && d.Kind == CosmeticKind.Frame);
            await AddPieceAsync(playerId, epic, copies: 3);
            await AddPieceAsync(playerId, common, copies: 4, listed: true);
            await AddPieceAsync(playerId, bound, copies: 2);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, pieces, threads) = await WardrobeThreads.UnravelAsync(db, playerId);
                Assert.Equal(WardrobeThreads.UnravelOutcome.Ok, outcome);
                // Epic: 3 -> 1 (2 spare). Common: 4 with one listed -> the 3
                // unlisted keep one (2 spare). Bound: untouched.
                Assert.Equal(4, pieces);
                Assert.Equal(2 * WardrobeThreads.PerDuplicate[CosmeticRegistry.Epic] + 2 * WardrobeThreads.PerDuplicate[CosmeticRegistry.Common], threads);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(1, await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.DefinitionId == epic.Id));
                Assert.Equal(2, await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.DefinitionId == common.Id));
                Assert.Equal(2, await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.DefinitionId == bound.Id));
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, _, _) = await WardrobeThreads.UnravelAsync(db, playerId);
                Assert.Equal(WardrobeThreads.UnravelOutcome.NothingSpare, outcome);
            }
        }

        [Fact]
        public async Task ThreadsBuyAMissingPiece_OnceAndOnlyWhenAffordable()
        {
            long playerId = await SeedPlayerAsync();
            var exclusive = WardrobeThreads.Exclusives[0];

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(WardrobeThreads.BuyOutcome.NotEnoughThreads, await WardrobeThreads.BuyAsync(db, playerId, exclusive.Id, Now));
                await WardrobeThreads.AddAsync(db, playerId, exclusive.Price + 5);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(WardrobeThreads.BuyOutcome.Ok, await WardrobeThreads.BuyAsync(db, playerId, exclusive.Id, Now));
            }
            Assert.Equal(5, await ThreadsAsync(playerId));
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(await db.CosmeticItems.AnyAsync(c => c.PlayerId == playerId && c.DefinitionId == exclusive.Id && c.Source == (byte)CosmeticSource.Threads));
                await WardrobeThreads.AddAsync(db, playerId, 10_000);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(WardrobeThreads.BuyOutcome.AlreadyOwned, await WardrobeThreads.BuyAsync(db, playerId, exclusive.Id, Now));
                // Bound pieces from elsewhere are not for sale.
                var ascension = CosmeticRegistry.All.First(d => d.Id.StartsWith("frame_ascent_"));
                Assert.Equal(WardrobeThreads.BuyOutcome.NotForSale, await WardrobeThreads.BuyAsync(db, playerId, ascension.Id, Now));
            }
        }

        [Fact]
        public void PricesAreTenDuplicatesOfARarity_AndTheExclusivesAreBoundFrames()
        {
            for (int r = 1; r <= CosmeticRegistry.MaxRarity; r++)
            {
                Assert.Equal(10 * WardrobeThreads.PerDuplicate[r], WardrobeThreads.ShopPrice[r]);
            }
            foreach (var e in WardrobeThreads.Exclusives)
            {
                var def = CosmeticRegistry.Find(e.Id);
                Assert.NotNull(def);
                Assert.True(def!.Bound);
                Assert.Equal(CosmeticKind.Frame, def.Kind);
                Assert.Equal(e.Price, WardrobeThreads.PriceOf(e.Id));
            }
            Assert.DoesNotContain(CosmeticRegistry.ChestPool(CosmeticRegistry.Legendary), d => WardrobeThreads.Exclusives.Any(e => e.Id == d.Id));
        }

        private sealed class FixedRandom : Random
        {
            private readonly double _value;
            public FixedRandom(double value) => _value = value;
            public override double NextDouble() => _value;
            public override int Next(int maxValue) => 0;
        }
    }
}

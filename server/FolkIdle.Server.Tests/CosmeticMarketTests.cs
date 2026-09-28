using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: TASK 54 PHASE 4 - the cosmetic market. The seller sets any price
    /// (owner decision); everything else is the equipment market's rules: a
    /// guild licence on both sides, the wealth fee burned, and the two gold
    /// paths kept apart (a buyer debit on the row; an offline seller credited
    /// on the row - the online seller's queue path is the equipment market's
    /// own, already covered there).
    /// </summary>
    [Collection("Postgres collection")]
    public class CosmeticMarketTests
    {
        private readonly PostgresTestFixture _fixture;

        public CosmeticMarketTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task ListingRefusesWhatItShould_AndCancelGivesItBack()
        {
            const long seller = 955000001L;
            const long stranger = 955000002L;
            await SeedAsync(seller, guildId: 9550, gold: 0);
            await SeedAsync(stranger, guildId: 0, gold: 0);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long avatar = await GiveAsync(db, seller, "avatar_malakor");
            long strangersFrame = await GiveAsync(db, stranger, "frame_knotwork");

            Assert.Equal(CosmeticMarketResult.InvalidPrice, await CosmeticMarketEngine.ListAsync(db, seller, avatar, 0, DateTime.UtcNow));
            Assert.Equal(CosmeticMarketResult.InvalidPrice, await CosmeticMarketEngine.ListAsync(db, seller, avatar, CosmeticRegistry.MaxMarketPrice + 1, DateTime.UtcNow));
            Assert.Equal(CosmeticMarketResult.NotYours, await CosmeticMarketEngine.ListAsync(db, seller, strangersFrame, 100, DateTime.UtcNow));
            Assert.Equal(CosmeticMarketResult.NoGuildLicense, await CosmeticMarketEngine.ListAsync(db, stranger, strangersFrame, 100, DateTime.UtcNow));

            // Worn, and the only copy: refused. A second copy makes the first sellable.
            Assert.Equal(CosmeticResult.Ok, await CosmeticEngine.EquipAsync(db, seller, CosmeticKind.Avatar, "avatar_malakor"));
            Assert.Equal(CosmeticMarketResult.Worn, await CosmeticMarketEngine.ListAsync(db, seller, avatar, 100, DateTime.UtcNow));
            await GiveAsync(db, seller, "avatar_malakor");

            // Any price the seller likes - far past any equipment corridor.
            Assert.Equal(CosmeticMarketResult.Ok, await CosmeticMarketEngine.ListAsync(db, seller, avatar, 750_000_000, DateTime.UtcNow));
            Assert.Equal(CosmeticMarketResult.AlreadyListed, await CosmeticMarketEngine.ListAsync(db, seller, avatar, 100, DateTime.UtcNow));

            var listing = await db.CosmeticListings.AsNoTracking().SingleAsync(l => l.CosmeticItemId == avatar);
            Assert.Equal(750_000_000, listing.Price);
            Assert.True((await db.CosmeticItems.AsNoTracking().SingleAsync(c => c.Id == avatar)).IsListed);

            Assert.Equal(CosmeticMarketResult.NotYours, await CosmeticMarketEngine.CancelAsync(db, stranger, listing.Id));
            Assert.Equal(CosmeticMarketResult.Ok, await CosmeticMarketEngine.CancelAsync(db, seller, listing.Id));
            Assert.False((await db.CosmeticItems.AsNoTracking().SingleAsync(c => c.Id == avatar)).IsListed);
            Assert.False(await db.CosmeticListings.AnyAsync(l => l.Id == listing.Id));
        }

        [Fact]
        public async Task AListedChestCannotBeOpened_AndAListedCosmeticCannotBeWorn()
        {
            const long seller = 955000003L;
            await SeedAsync(seller, guildId: 9550, gold: 0);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long chest = (await CosmeticEngine.InsertChestsAsync(db, seller, new[] { CosmeticRegistry.Rare }, CosmeticSource.Dev, DateTime.UtcNow)).Single();
            long frame = await GiveAsync(db, seller, "frame_sun_crown");

            Assert.Equal(CosmeticMarketResult.Ok, await CosmeticMarketEngine.ListAsync(db, seller, chest, 5000, DateTime.UtcNow));
            Assert.Equal(CosmeticMarketResult.Ok, await CosmeticMarketEngine.ListAsync(db, seller, frame, 5000, DateTime.UtcNow));

            var (opened, _) = await CosmeticEngine.OpenAsync(db, seller, CosmeticRegistry.Rare, new Random(1), DateTime.UtcNow);
            Assert.Equal(CosmeticResult.NoChest, opened);
            Assert.Equal(CosmeticResult.NotOwned, await CosmeticEngine.EquipAsync(db, seller, CosmeticKind.Frame, "frame_sun_crown"));
        }

        [Fact]
        public async Task ASaleMovesTheItem_DebitsTheBuyer_AndPaysTheSellerLessTheFee()
        {
            const long seller = 955000004L;
            const long buyer = 955000005L;
            const long poorBuyer = 955000006L;
            await SeedAsync(seller, guildId: 9551, gold: 1_000);
            await SeedAsync(buyer, guildId: 9552, gold: 100_000);
            await SeedAsync(poorBuyer, guildId: 9552, gold: 10);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long chest = (await CosmeticEngine.InsertChestsAsync(db, seller, new[] { CosmeticRegistry.Legendary }, CosmeticSource.Dev, DateTime.UtcNow)).Single();
            Assert.Equal(CosmeticMarketResult.Ok, await CosmeticMarketEngine.ListAsync(db, seller, chest, 40_000, DateTime.UtcNow));
            long listingId = (await db.CosmeticListings.AsNoTracking().SingleAsync(l => l.CosmeticItemId == chest)).Id;

            Assert.Equal(CosmeticMarketResult.OwnListing, (await CosmeticMarketEngine.BuyAsync(db, null, seller, listingId, DateTime.UtcNow)).Result);
            Assert.Equal(CosmeticMarketResult.InsufficientGold, (await CosmeticMarketEngine.BuyAsync(db, null, poorBuyer, listingId, DateTime.UtcNow)).Result);

            var (result, sale) = await CosmeticMarketEngine.BuyAsync(db, null, buyer, listingId, DateTime.UtcNow);
            Assert.Equal(CosmeticMarketResult.Ok, result);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var item = await verify.CosmeticItems.SingleAsync(c => c.Id == chest);
            Assert.Equal(buyer, item.PlayerId);
            Assert.False(item.IsListed);
            Assert.Equal((byte)CosmeticSource.Market, item.Source);

            // 5% burned at the seller's wealth bracket; the seller's guild has
            // no GuildRecords row, so no guild tax.
            long expectedProceeds = 40_000 - (long)(40_000 * 0.05);
            Assert.Equal(expectedProceeds, sale!.SellerProceeds);
            Assert.Equal(100_000 - 40_000, await GoldAsync(verify, buyer));
            Assert.Equal(1_000 + expectedProceeds, await GoldAsync(verify, seller));

            Assert.False(await verify.CosmeticListings.AnyAsync(l => l.Id == listingId));
            var archive = await verify.HistoricalMarketArchives.SingleAsync(a => a.OriginalOrderId == listingId && a.OrderType == "COSMETIC");
            Assert.Equal(40_000, archive.ExecutionPrice);
            Assert.Equal(2_000, archive.FeeBurned);

            // The buyer can open what they bought.
            var (opened, contents) = await CosmeticEngine.OpenAsync(verify, buyer, CosmeticRegistry.Legendary, new Random(3), DateTime.UtcNow);
            Assert.Equal(CosmeticResult.Ok, opened);
            Assert.Equal(CosmeticRegistry.Legendary, contents!.Rarity);

            // And a sold listing cannot be bought twice.
            Assert.Equal(CosmeticMarketResult.NotFound, (await CosmeticMarketEngine.BuyAsync(verify, null, poorBuyer, listingId, DateTime.UtcNow)).Result);
        }

        private async Task SeedAsync(long playerId, long guildId, long gold)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            if (await db.PlayerRecords.AnyAsync(p => p.Id == playerId)) return;
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = guildId,
                Username = $"t{playerId}",
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = gold });
            await db.SaveChangesAsync();
        }

        private static async Task<long> GiveAsync(FolkIdleDbContext db, long playerId, string definitionId)
        {
            var def = CosmeticRegistry.Find(definitionId)!;
            var row = new CosmeticItem
            {
                PlayerId = playerId, Kind = (byte)def.Kind, DefinitionId = def.Id, Rarity = (byte)def.Rarity,
                Source = (byte)CosmeticSource.Dev, AcquiredAtUtc = DateTime.UtcNow,
            };
            db.CosmeticItems.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        }

        private static async Task<long> GoldAsync(FolkIdleDbContext db, long playerId)
            => (await db.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold")).Quantity;
    }
}

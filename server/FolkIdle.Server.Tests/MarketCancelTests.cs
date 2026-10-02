using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Cancelling one of your own market orders (POST /api/v1/market/cancel).
    /// </summary>
    /// <remarks>
    /// Modul: A DATA-LOSS SURFACE. A cancel moves a piece out of escrow and
    /// back into the chest, or gold out of escrow and back onto the row, so
    /// every way of getting it wrong loses or duplicates something: a piece
    /// that comes back changed, a stranger cancelling your listing into THEIR
    /// chest, a second press refunding twice, and - the one that matters most -
    /// a cancel and a purchase landing in the same instant and BOTH happening,
    /// which would be a free copy of the item.
    /// </remarks>
    [Collection("Postgres collection")]
    public class MarketCancelTests
    {
        private const string CanonicalBoots = "eq_hunter_boots_boots_armor_slot_base"; // 200 base gold: corridor [160, 600] at tier 0
        private const string RaceItem = "copper_greatsword_melee_weapon_slot_base";
        private const long SellerGuild = 957390001L;
        private const long BuyerGuild = 957390002L;

        private readonly PostgresTestFixture _fixture;

        public MarketCancelTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task CancellingAListing_PutsThePieceBackUnchanged()
        {
            const long seller = 957300001L;
            await SeedPlayerAsync(seller, SellerGuild, gold: 0);

            long pieceId;
            string originalAffixes;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var piece = new EquipmentInstance
                {
                    PlayerId = seller,
                    BaseItemId = CanonicalBoots,
                    QualityTier = 0,
                    AffixPayload = "{\"crit_chance_pct\": 3, \"flat_hp\": 41}",
                    IsAffixLocked = true,
                };
                db.EquipmentInstances.Add(piece);
                await db.SaveChangesAsync();
                pieceId = piece.Id;
                // Read back, so the comparison is jsonb-normalised on both sides.
                originalAffixes = (await db.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == pieceId)).AffixPayload;
            }

            var escrow = new MarketEscrowEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            Assert.True(await escrow.ListItemAsync(seller, pieceId, 500L));

            long orderId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.False(await db.EquipmentInstances.AnyAsync(e => e.PlayerId == seller), "listing moves the piece into escrow");
                orderId = (await db.MarketOrderRecords.AsNoTracking().SingleAsync(o => o.SellerId == seller && o.Status == 0)).Id;
            }

            MarketCancelOutcome outcome;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                outcome = await MarketEscrowEngine.CancelOrderAsync(db, seller, orderId);
            }

            Assert.Equal(MarketCancelResult.Ok, outcome.Result);
            Assert.NotNull(outcome.ReturnedEquipmentId);
            Assert.Equal(0, outcome.RefundedGold);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var back = await db.EquipmentInstances.AsNoTracking().SingleAsync(e => e.PlayerId == seller);
                Assert.Equal(outcome.ReturnedEquipmentId, back.Id);
                Assert.Equal(CanonicalBoots, back.BaseItemId);
                Assert.Equal(0, back.QualityTier);
                Assert.Equal(originalAffixes, back.AffixPayload);
                Assert.True(back.IsAffixLocked);

                // Nothing left half-owned: no order, no escrowed copy.
                Assert.False(await db.MarketOrderRecords.AnyAsync(o => o.Id == orderId));
                Assert.False(await db.MarketEquipmentInstances.AnyAsync(e => e.PlayerId == seller));
            }
        }

        [Fact]
        public async Task SomeoneElsesOrder_IsRefused_AndLeftOnTheBook()
        {
            const long seller = 957300011L;
            const long stranger = 957300012L;
            await SeedPlayerAsync(seller, SellerGuild, gold: 0);
            await SeedPlayerAsync(stranger, SellerGuild, gold: 0);
            var (orderId, escrowedId) = await SeedListingAsync(seller, 700L);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(MarketCancelResult.NotYours, (await MarketEscrowEngine.CancelOrderAsync(db, stranger, orderId)).Result);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var order = await db.MarketOrderRecords.AsNoTracking().SingleAsync(o => o.Id == orderId);
                Assert.Equal(0, order.Status);
                var escrowed = await db.MarketEquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == escrowedId);
                Assert.Equal(seller, escrowed.PlayerId);
                Assert.True(escrowed.IsLockedInEscrow);
                Assert.False(await db.EquipmentInstances.AnyAsync(e => e.PlayerId == stranger || e.PlayerId == seller));
            }
        }

        [Fact]
        public async Task ASecondCancel_IsRefused_AndReturnsNothingMore()
        {
            const long seller = 957300021L;
            await SeedPlayerAsync(seller, SellerGuild, gold: 0);
            var (orderId, _) = await SeedListingAsync(seller, 700L);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(MarketCancelResult.Ok, (await MarketEscrowEngine.CancelOrderAsync(db, seller, orderId)).Result);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var second = await MarketEscrowEngine.CancelOrderAsync(db, seller, orderId);
                Assert.Equal(MarketCancelResult.Gone, second.Result);
                Assert.Null(second.ReturnedEquipmentId);
            }

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(1, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == seller));
            }
        }

        [Fact]
        public async Task CancellingABuyOrder_RefundsTheEscrowedGoldExactly_Once()
        {
            const long buyer = 957300031L;
            const long price = 1234L;
            // The escrow already left the row when the order was placed
            // (PlaceLimitOrderAsync: goldRecord.Quantity -= price).
            await SeedPlayerAsync(buyer, BuyerGuild, gold: 5000L - price);

            long orderId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var order = new MarketOrderRecord
                {
                    SellerId = buyer,
                    OrderType = "BUY",
                    BaseItemId = CanonicalBoots,
                    QualityTier = 0,
                    Price = price,
                    Status = 0,
                    CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                db.MarketOrderRecords.Add(order);
                await db.SaveChangesAsync();
                orderId = order.Id;
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var outcome = await MarketEscrowEngine.CancelOrderAsync(db, buyer, orderId);
                Assert.Equal(MarketCancelResult.Ok, outcome.Result);
                Assert.Equal(price, outcome.RefundedGold);
                Assert.Null(outcome.ReturnedEquipmentId);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(MarketCancelResult.Gone, (await MarketEscrowEngine.CancelOrderAsync(db, buyer, orderId)).Result);
            }

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(5000L, await GoldAsync(verify, buyer));
                Assert.False(await verify.MarketOrderRecords.AnyAsync(o => o.Id == orderId));
            }
        }

        [Fact]
        public async Task CancelRacingABuy_EndsInExactlyOneOutcome()
        {
            const long startGold = 10_000L;
            const long price = 900L;
            const int rounds = 8;
            int sales = 0, cancels = 0;

            for (int round = 0; round < rounds; round++)
            {
                long seller = 957310001L + round * 2;
                long buyer = seller + 1;
                await SeedPlayerAsync(seller, SellerGuild, gold: 0);
                await SeedPlayerAsync(buyer, BuyerGuild, gold: startGold, level: 100);
                var (orderId, escrowedId) = await SeedListingAsync(seller, price, RaceItem);

                var escrow = new MarketEscrowEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                Task buy = Task.Run(async () =>
                {
                    await gate.Task;
                    await escrow.BuyItemAsync(buyer, orderId, hasSpace: true);
                });
                Task<MarketCancelResult> cancel = Task.Run(async () =>
                {
                    await gate.Task;
                    await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                    return (await MarketEscrowEngine.CancelOrderAsync(db, seller, orderId)).Result;
                });
                gate.SetResult();
                await Task.WhenAll(buy, cancel);

                await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
                bool sold = await verify.HistoricalMarketArchives.AnyAsync(a => a.OriginalOrderId == orderId);
                int sellerPieces = await verify.EquipmentInstances.CountAsync(e => e.PlayerId == seller);
                int buyerPieces = await verify.EquipmentInstances.CountAsync(e => e.PlayerId == buyer);
                long buyerGold = await GoldAsync(verify, buyer);

                // Whichever side won, the book and the escrow are empty.
                Assert.False(await verify.MarketOrderRecords.AnyAsync(o => o.Id == orderId), $"round {round}: order left on the book");
                Assert.False(await verify.MarketEquipmentInstances.AnyAsync(e => e.Id == escrowedId), $"round {round}: piece left in escrow");

                // Exactly one copy of the piece exists, and the gold agrees
                // with whoever holds it.
                Assert.Equal(1, sellerPieces + buyerPieces);
                if (sold)
                {
                    sales++;
                    Assert.Equal(1, buyerPieces);
                    Assert.Equal(startGold - price, buyerGold);
                    Assert.NotEqual(MarketCancelResult.Ok, cancel.Result);
                }
                else
                {
                    cancels++;
                    Assert.Equal(1, sellerPieces);
                    Assert.Equal(startGold, buyerGold);
                    Assert.Equal(MarketCancelResult.Ok, cancel.Result);
                }
            }

            Assert.Equal(rounds, sales + cancels);
        }

        // ---------------------------------------------------------------

        private async Task SeedPlayerAsync(long playerId, long guildId, long gold, int level = 1)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            if (!await db.GuildRecords.AnyAsync(g => g.Id == guildId))
            {
                db.GuildRecords.Add(new GuildRecord { Id = guildId, Name = $"CancelTestGuild{guildId}" });
            }
            if (!await db.PlayerRecords.AnyAsync(p => p.Id == playerId))
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    GuildId = guildId,
                    CurrentLevel = level,
                    Username = $"t{playerId}",
                });
                await db.SaveChangesAsync();
                await CommodityLedger.AddAsync(db, playerId, "gold", gold);
            }
            await db.SaveChangesAsync();
        }

        /// <summary>A listing as ListItemAsync leaves it: an escrowed copy and an open SELL order.</summary>
        private async Task<(long OrderId, long EscrowedId)> SeedListingAsync(long seller, long price, string baseItemId = CanonicalBoots)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var escrowed = new MarketEquipmentInstance
            {
                PlayerId = seller,
                BaseItemId = baseItemId,
                QualityTier = 0,
                AffixPayload = "{}",
                IsLockedInEscrow = true,
            };
            db.MarketEquipmentInstances.Add(escrowed);
            await db.SaveChangesAsync();

            var order = new MarketOrderRecord
            {
                SellerId = seller,
                OrderType = "SELL",
                EquipmentInstanceId = escrowed.Id,
                BaseItemId = baseItemId,
                QualityTier = 0,
                Price = price,
                Status = 0,
                CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            db.MarketOrderRecords.Add(order);
            await db.SaveChangesAsync();
            return (order.Id, escrowed.Id);
        }

        private static async Task<long> GoldAsync(FolkIdleDbContext db, long playerId)
            => (await db.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold")).Quantity;
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 102: "My orders" lists what the player has open on the book - both
    /// sides, only theirs, only open, newest first.
    /// </summary>
    [Collection("Postgres collection")]
    public class MarketOwnOrdersTests
    {
        private readonly PostgresTestFixture _fixture;

        public MarketOwnOrdersTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task OwnOpenOrders_AreBothSides_OnlyMine_OnlyOpen_NewestFirst()
        {
            const string Helm = "eq_sentry_helm_helmet_armor_slot_base";
            long me = DbSeeder.PlayerMidId;
            long someoneElse = DbSeeder.PlayerLowId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            MarketOrderRecord Row(long seller, string side, int status, long created) => new()
            {
                SellerId = seller,
                OrderType = side,
                BaseItemId = Helm,
                QualityTier = side == "BUY" ? 0 : 3,
                Price = 1234,
                Status = status,
                CreatedAtEpoch = created,
            };

            var olderSell = Row(me, "SELL", 0, now - 2000);
            var newerBuy = Row(me, "BUY", 0, now - 1000);
            var filled = Row(me, "SELL", 1, now);
            var theirs = Row(someoneElse, "SELL", 0, now);

            using (var scope = _fixture.ServiceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                db.MarketOrderRecords.AddRange(olderSell, newerBuy, filled, theirs);
                await db.SaveChangesAsync();
            }

            try
            {
                using var scope = _fixture.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var mine = await MarketOrderBookEngine.FetchOwnOpenOrdersAsync(db, me);

                var ids = mine.Select(o => o.Id).ToList();
                Assert.Contains(olderSell.Id, ids);
                Assert.Contains(newerBuy.Id, ids);
                Assert.DoesNotContain(filled.Id, ids);
                Assert.DoesNotContain(theirs.Id, ids);
                Assert.All(mine, o => Assert.Equal(me, o.SellerId));
                Assert.All(mine, o => Assert.Equal(0, o.Status));
                Assert.True(ids.IndexOf(newerBuy.Id) < ids.IndexOf(olderSell.Id), "newest first");
            }
            finally
            {
                using var scope = _fixture.ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var ids = new[] { olderSell.Id, newerBuy.Id, filled.Id, theirs.Id };
                await db.MarketOrderRecords.Where(o => ids.Contains(o.Id)).ExecuteDeleteAsync();
            }
        }
    }
}

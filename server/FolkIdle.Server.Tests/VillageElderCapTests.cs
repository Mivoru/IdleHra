using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Elders - newcomers who have married into the line - stay on the roster
    /// but do not take one of the Inn's places.
    ///
    /// Modul: they used to. DismissAsync refuses an elder, so each marriage
    /// filled a place for good, and about Inn level + 6 of them stopped both
    /// the arrival clock and the feast for the rest of the season. See
    /// VillageArrivalEngine.CountRoomTakersAsync.
    /// </summary>
    [Collection("Postgres collection")]
    public class VillageElderCapTests
    {
        private readonly PostgresTestFixture _fixture;

        public VillageElderCapTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task SeedAsync(long playerId, int elders, int unmarried)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                LastVillagerArrivalEpoch = 1_800_000_000L,
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000L });
            for (int i = 0; i < elders; i++)
            {
                db.VillageNewcomers.Add(new VillageNewcomer { PlayerId = playerId, RaceId = RaceIds.Human, IsElder = true });
            }
            for (int i = 0; i < unmarried; i++)
            {
                db.VillageNewcomers.Add(new VillageNewcomer { PlayerId = playerId, RaceId = RaceIds.Human, IsElder = false });
            }
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task AVillageFullOfElders_StillTakesAFeastAndArrivals()
        {
            const long playerId = 970013301L;
            int cap = VillagerArrivalRules.PopulationCapFor(0);
            await SeedAsync(playerId, elders: cap, unmarried: 0);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = await db.PlayerRecords.SingleAsync(p => p.Id == playerId);
                var (refusal, _) = await VillageArrivalEngine.RecruitAsync(db, player, innLevel: 0, nowEpoch: 1_800_000_100L);
                Assert.Null(refusal);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = await db.PlayerRecords.SingleAsync(p => p.Id == playerId);
                long later = player.LastVillagerArrivalEpoch + VillagerArrivalRules.IntervalSecondsFor(0);
                int arrived = await VillageArrivalEngine.SettleAsync(db, player, innLevel: 0, later);
                Assert.Equal(1, arrived);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(cap, await verify.VillageNewcomers.CountAsync(v => v.PlayerId == playerId && v.IsElder));
            Assert.Equal(2, await VillageArrivalEngine.CountRoomTakersAsync(verify, playerId));
        }

        [Fact]
        public async Task UnmarriedNewcomers_StillFillTheVillage()
        {
            const long playerId = 970013302L;
            int cap = VillagerArrivalRules.PopulationCapFor(0);
            await SeedAsync(playerId, elders: 3, unmarried: cap);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await db.PlayerRecords.SingleAsync(p => p.Id == playerId);
            var (refusal, spent) = await VillageArrivalEngine.RecruitAsync(db, player, innLevel: 0, nowEpoch: 1_800_000_100L);

            Assert.NotNull(refusal);
            Assert.Contains($"({cap}/{cap})", refusal);
            Assert.Equal(0, spent);
        }
    }
}

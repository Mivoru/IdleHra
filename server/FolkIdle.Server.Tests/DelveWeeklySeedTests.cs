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
    /// THE WEEKLY DEEP SEED (task 61): every Deep floor is the same for
    /// everyone within an ISO week, so a player races their own last week.
    /// The doors are seeded; the pass roll and every price are not.
    /// </summary>
    [Collection("Postgres collection")]
    public class DelveWeeklySeedTests
    {
        private readonly PostgresTestFixture _fixture;

        public DelveWeeklySeedTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private static readonly Random Pass = new ConstantRandom(0.0);

        private static int[] Doors(int weekKey, int floor, int attempt)
        {
            var rng = DelveRegistry.WeeklyDeepFloorRandom(weekKey, floor, attempt);
            var doors = new int[DelveRegistry.DoorsPerFloor];
            for (int i = 0; i < doors.Length; i++)
            {
                doors[i] = rng.Next(AttributeRegistry.Count);
                rng.NextDouble();
            }
            return doors;
        }

        [Fact]
        public void TheSameWeekFloorAndAttemptAlwaysGiveTheSameDoors()
        {
            for (int floor = 9; floor <= 30; floor++)
            {
                Assert.Equal(Doors(202640, floor, 1), Doors(202640, floor, 1));
            }
        }

        [Fact]
        public void AnotherWeekIsAnotherCourse()
        {
            var thisWeek = Enumerable.Range(9, 12).SelectMany(f => Doors(202640, f, 1)).ToArray();
            var nextWeek = Enumerable.Range(9, 12).SelectMany(f => Doors(202641, f, 1)).ToArray();
            Assert.NotEqual(thisWeek, nextWeek);
        }

        /// <summary>
        /// Pinned so a runtime or refactor that changed the generator is
        /// noticed: it would reshuffle a live week under everyone mid-race.
        /// </summary>
        [Fact]
        public void TheGeneratorIsPinned()
        {
            var a = Doors(202640, 9, 1);
            var b = Doors(202640, 9, 1);
            Assert.Equal(a, b);
            Assert.All(a, d => Assert.InRange(d, 0, AttributeRegistry.Count - 1));
            // Recorded 2026-09-29 from this implementation.
            Assert.Equal(PinnedWeek40Floor9, a);
        }

        private static readonly int[] PinnedWeek40Floor9 = { 3, 1, 0 };

        private async Task SeedAsync(long playerId, int weekKey = 0, int deepestThisWeek = 0)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM player_gold_daily_high WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DelveRunRecords\" WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerRecords\" WHERE \"Id\" = {0}", playerId);

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                BaseStrength = 400,
                BaseDexterity = 400,
                BaseConstitution = 400,
                BaseLuck = 400,
                DelveWeekKey = weekKey,
                DelveDeepestThisWeek = deepestThisWeek,
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 50_000_000 });
            if (!await db.MonsterCodexEntries.AnyAsync(c => c.PlayerId == playerId))
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = playerId, MonsterId = ContentRegistry.FirstCanonicalMonsterId, KillCount = 1 });
            }
            await db.SaveChangesAsync();
        }

        private async Task<DelveRunRecord> RunAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.DelveRunRecords.AsNoTracking().SingleAsync(r => r.PlayerId == playerId);
        }

        private async Task<PlayerRecord> PlayerAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
        }

        private DelveEngine Engine(DateTime now)
            => new DelveEngine(_fixture.DbContextFactory, new DelveDeepSettings { Enabled = true, UtcNow = () => now });

        private async Task<(int Floor9, int Floor10)> DescendTwoFloorsAsync(DelveEngine engine, long playerId)
        {
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            int floor9 = (await RunAsync(playerId)).PackedDoorDemands;
            await engine.ChooseDoorAsync(playerId, 0, Pass);
            var landing = await engine.GetViewAsync(playerId);
            await engine.DescendAsync(playerId, landing.StakeGold, Pass);
            int floor10 = (await RunAsync(playerId)).PackedDoorDemands;
            return (floor9, floor10);
        }

        [Fact]
        public async Task TwoPlayersInOneWeekMeetTheSameDeepFloors()
        {
            var monday = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
            var engine = Engine(monday);
            await SeedAsync(983000001L);
            await SeedAsync(983000002L);

            var first = await DescendTwoFloorsAsync(engine, 983000001L);
            var second = await DescendTwoFloorsAsync(engine, 983000002L);

            Assert.Equal(first, second);
        }

        [Fact]
        public async Task TheNextWeekIsADifferentCourse()
        {
            var thisWeek = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
            var nextWeek = thisWeek.AddDays(7);
            await SeedAsync(983000003L);
            var a = await DescendTwoFloorsAsync(Engine(thisWeek), 983000003L);
            await SeedAsync(983000003L);
            var b = await DescendTwoFloorsAsync(Engine(nextWeek), 983000003L);

            Assert.NotEqual(a, b);
        }

        [Fact]
        public async Task LastWeeksBestIsCarriedIntoThisWeek()
        {
            var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
            int lastWeek = DelveEngine.CurrentWeekKey(now.AddDays(-7));
            await SeedAsync(983000004L, weekKey: lastWeek, deepestThisWeek: 23);

            var engine = Engine(now);
            // Shown before anything is written: a read does not turn the week.
            Assert.Equal(23, (await engine.GetViewAsync(983000004L)).DeepestLastWeek);

            await DescendTwoFloorsAsync(engine, 983000004L);

            var player = await PlayerAsync(983000004L);
            Assert.Equal(23, player.DelveDeepestLastWeek);
            Assert.Equal(DelveEngine.CurrentWeekKey(now), player.DelveWeekKey);
            var view = await engine.GetViewAsync(983000004L);
            Assert.Equal(23, view.DeepestLastWeek);
            Assert.Equal(DelveEngine.CurrentWeekKey(now), view.DeepWeekKey);
            Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), view.DeepWeekEndsUtc);
        }

        [Fact]
        public async Task APlayerBackAfterAMonthHasNoLastWeek()
        {
            var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
            int aMonthAgo = DelveEngine.CurrentWeekKey(now.AddDays(-28));
            await SeedAsync(983000005L, weekKey: aMonthAgo, deepestThisWeek: 40);

            var engine = Engine(now);
            Assert.Equal(0, (await engine.GetViewAsync(983000005L)).DeepestLastWeek);
            await DescendTwoFloorsAsync(engine, 983000005L);
            Assert.Equal(0, (await PlayerAsync(983000005L)).DelveDeepestLastWeek);
        }

        [Theory]
        [InlineData(2026, 9, 28, 0, 2026, 10, 5)]   // Monday 00:00 -> next Monday
        [InlineData(2026, 10, 4, 23, 2026, 10, 5)]  // Sunday night
        [InlineData(2026, 9, 30, 12, 2026, 10, 5)]  // midweek
        public void TheWeekEndsOnMondayMidnightUtc(int y, int m, int d, int h, int ey, int em, int ed)
        {
            Assert.Equal(new DateTime(ey, em, ed, 0, 0, 0, DateTimeKind.Utc),
                DelveEngine.WeekEndsUtc(new DateTime(y, m, d, h, 0, 0, DateTimeKind.Utc)));
        }
    }
}

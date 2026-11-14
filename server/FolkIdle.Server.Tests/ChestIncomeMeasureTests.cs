using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The income measure chest gold and the expedition prices are
    /// sized from counts EARNED gold only - the same sources as
    /// GoldRewardScale.EarnedSources - so a reward cannot raise its own base.
    /// </summary>
    [Collection("Postgres collection")]
    public class ChestIncomeMeasureTests
    {
        private readonly PostgresTestFixture _fixture;

        public ChestIncomeMeasureTests(PostgresTestFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task OnlyEarnedSourcesCount()
        {
            const long playerId = 960_118_001L;
            var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
            var today = DateOnly.FromDateTime(now);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                // 100.8B earned in a week = 10M a minute, far above every region floor.
                db.GoldIncomeDaily.Add(new GoldIncomeDaily { PlayerId = playerId, Day = today, Source = (short)GoldIncomeSource.CombatAway, Amount = 100_800_000_000L });
                // Rewards and trades: none of these may move the measure.
                foreach (var source in new[] { GoldIncomeSource.Mail, GoldIncomeSource.Market, GoldIncomeSource.Dungeon, GoldIncomeSource.LootChest, GoldIncomeSource.GuildPayout, GoldIncomeSource.Delve })
                {
                    db.GoldIncomeDaily.Add(new GoldIncomeDaily { PlayerId = playerId, Day = today, Source = (short)source, Amount = 50_000_000_000L });
                }
                await db.SaveChangesAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                double perMinute = await LootChestEngine.GoldPerMinuteAsync(db, playerId, 5, now);
                Assert.Equal(10_000_000d, perMinute, 0);
            }
        }
    }
}

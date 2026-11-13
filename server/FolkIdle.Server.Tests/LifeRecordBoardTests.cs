using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>The Fastest lives board (2026-11-13): timed lives only, fastest first.</summary>
    [Collection("Postgres collection")]
    public class LifeRecordBoardTests
    {
        private readonly PostgresTestFixture _fixture;

        public LifeRecordBoardTests(PostgresTestFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task TheFastestLifeRanksFirst_AndUntimedAccountsAreAbsent()
        {
            const long fast = 960_115_001L, slow = 960_115_002L, untimed = 960_115_003L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.AddRange(
                    new PlayerRecord { Id = fast, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = "fast_life", BestLifeSecondsTo50 = 7_200 },
                    new PlayerRecord { Id = slow, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = "slow_life", BestLifeSecondsTo50 = 9_000, BestLifeSecondsTo100 = 40_000 },
                    new PlayerRecord { Id = untimed, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = "no_clock" });
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await LifeRecordBoard.TopAsync(read);
            var mine = rows.Where(r => r.PlayerId is fast or slow or untimed).ToList();

            Assert.Equal(new[] { fast, slow }, mine.Select(r => r.PlayerId));
            Assert.True(mine[0].Rank < mine[1].Rank);
            Assert.Equal(40_000, mine[1].BestTo100);
        }
    }
}

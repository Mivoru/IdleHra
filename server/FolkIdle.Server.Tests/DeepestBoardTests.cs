using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The weekly "Deepest" board (task 37 spec §4): ordered by this week's
    /// record, ties to the earlier one, a stale week and a throwaway account
    /// never shown - and it pays nothing, which is pinned in the source.
    /// </summary>
    [Collection("Postgres collection")]
    public class DeepestBoardTests
    {
        private readonly PostgresTestFixture _fixture;
        public DeepestBoardTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task SeedAsync(long id, int level, int weekKey, int deepestThisWeek, DateTime? at)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerRecords\" WHERE \"Id\" = {0}", id);
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = id,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"deep{id}",
                CurrentLevel = level,
                DelveWeekKey = weekKey,
                DelveDeepestThisWeek = deepestThisWeek,
                DelveDeepestFloor = deepestThisWeek,
                DelveDeepestThisWeekAtUtc = at,
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task TheBoardOrdersByThisWeeksRecordThenByWhoGotThereFirst()
        {
            var now = DateTime.UtcNow;
            int week = DelveEngine.CurrentWeekKey(now);
            int lastWeek = DelveEngine.CurrentWeekKey(now.AddDays(-7));

            await SeedAsync(984000001L, 50, week, 20, now.AddHours(-1));   // 20, later
            await SeedAsync(984000002L, 50, week, 20, now.AddHours(-3));   // 20, earlier: ranks first
            await SeedAsync(984000003L, 50, week, 35, now.AddHours(-2));   // deepest
            await SeedAsync(984000004L, 50, lastWeek, 99, now.AddDays(-8)); // last week: excluded
            await SeedAsync(984000005L, LeaderboardTierRegistry.MinimumRankedLevel - 1, week, 60, now); // under the floor

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var board = await DeepestBoard.TopAsync(db, now);
            var mine = board.Where(r => r.PlayerId >= 984000001L && r.PlayerId <= 984000005L).Select(r => r.PlayerId).ToArray();

            Assert.Equal(new[] { 984000003L, 984000002L, 984000001L }, mine);
            Assert.True(board.Count <= DeepestBoard.Size);
            Assert.Equal(Enumerable.Range(1, board.Count), board.Select(r => r.Rank));
        }

        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server");
        }

        /// <summary>
        /// Modul: THE PAYOUT READS ONE BOARD. LeaderboardPayoutEngine pays
        /// diamonds; if it ever read another key, a board built to pay nothing
        /// could start paying without anyone deciding it should.
        /// </summary>
        [Fact]
        public void TheDiamondPayoutReadsOnlyTheMasteryBoard()
        {
            string code = DelveDeepTests.CodeWithoutComments(Path.Combine(ServerRoot(), "Engine", "LeaderboardPayoutEngine.cs"));
            var keys = Regex.Matches(code, "\"leaderboard:[^\"]*\"").Select(m => m.Value).Distinct().ToArray();
            Assert.Equal(new[] { "\"leaderboard:mastery\"" }, keys);
        }

        /// <summary>No Deep, title or board code writes a diamond.</summary>
        [Fact]
        public void NothingInTheDeepOrTheBoardAssignsDiamonds()
        {
            string root = ServerRoot();
            var files = new[]
            {
                Path.Combine(root, "Domain", "Economy", "DeepestBoard.cs"),
                Path.Combine(root, "Domain", "Progression", "TitleRegistry.cs"),
                Path.Combine(root, "Domain", "Progression", "TitleEngine.cs"),
                Path.Combine(root, "Engine", "DelveRegistry.cs"),
            };
            foreach (var file in files)
            {
                Assert.DoesNotContain("PremiumDiamonds", DelveDeepTests.CodeWithoutComments(file));
            }

            // DelveEngine does write diamonds - in the ONE floors-1-8 bank, and
            // nowhere else. Every assignment must sit inside BankFloorsAsync.
            string engine = File.ReadAllText(Path.Combine(root, "Domain", "Economy", "DelveEngine.cs"));
            int bankStart = engine.IndexOf("private async Task<(int Granted, long Consolation, CommodityRecord? GoldRow)> BankFloorsAsync", StringComparison.Ordinal);
            Assert.True(bankStart > 0, "BankFloorsAsync moved - restate this guard");
            int bankEnd = engine.IndexOf("private static Task<PlayerRecord?> LockPlayerAsync", bankStart, StringComparison.Ordinal);
            var writes = Regex.Matches(engine, @"PremiumDiamonds\s*(\+|-)?=").Select(m => m.Index).ToArray();
            Assert.NotEmpty(writes);
            Assert.All(writes, i => Assert.InRange(i, bankStart, bankEnd));
        }
    }
}

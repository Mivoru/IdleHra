using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The boss does not have to fall (owner, 2026-09-26). An encounter pays
    /// when it ends either way, by rank in damage dealt, from the same rows the
    /// damage board shows - and a developer's window pays nothing.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossPayoutTests
    {
        private readonly PostgresTestFixture _fixture;

        public WorldBossPayoutTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<long[]> PlayersAsync(int count)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var players = Enumerable.Range(0, count).Select(i => new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"boss_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            }).ToList();
            db.PlayerRecords.AddRange(players);
            await db.SaveChangesAsync();
            return players.Select(p => p.Id).ToArray();
        }

        /// <summary>Gives each player a strike row with the given damage, as a landed strike would.</summary>
        private async Task DealAsync(long[] players, long[] damage)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long today = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            for (int i = 0; i < players.Length; i++)
            {
                db.PlayerWorldBossAttempts.Add(new PlayerWorldBossAttempt
                {
                    PlayerId = players[i],
                    BossInstanceId = WorldBossEngine.ActiveBossInstanceId,
                    AttemptCount = 1,
                    TotalInflictedDamage = damage[i],
                    AttemptDateKey = today,
                });
            }
            await db.SaveChangesAsync();
        }

        private async Task<(int Tokens, long Gold)?> RewardAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var mail = await db.MailboxInstances.AsNoTracking()
                .Where(m => m.PlayerId == playerId && m.BaseItemId == "perun_avatar_reward_token")
                .ToListAsync();
            return mail.Count == 0 ? null : (mail.Sum(m => m.Quantity), mail.Sum(m => m.GoldAttachment));
        }

        private async Task<WorldBossEngine> OpenAsync()
        {
            var engine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
            return engine;
        }

        [Fact]
        public async Task AWeekTheBossSurvivedStillPaysEveryoneByTheirDamage()
        {
            var engine = await OpenAsync();
            var players = await PlayersAsync(4);
            await DealAsync(players, new long[] { 9_000, 1_000, 4_000, 2_000 });

            await engine.FinalizeEventAsFailedAsync();

            // Four participants: rank 1 is the top 25%, which is inside the top
            // 50% bracket, as is rank 2; ranks 3 and 4 are participation.
            Assert.Equal((3, 50_000L), await RewardAsync(players[0]));
            Assert.Equal((3, 50_000L), await RewardAsync(players[2]));
            Assert.Equal((1, 10_000L), await RewardAsync(players[3]));
            Assert.Equal((1, 10_000L), await RewardAsync(players[1]));

            // Once only: a second close finds the encounter already concluded.
            await engine.FinalizeEventAsFailedAsync();
            Assert.Equal((3, 50_000L), await RewardAsync(players[0]));
        }

        [Fact]
        public async Task ADevelopersWindowPaysNothingWhenItCloses()
        {
            var engine = await OpenAsync();
            var players = await PlayersAsync(2);
            await DealAsync(players, new long[] { 5_000, 3_000 });

            await engine.CloseManualWindowAsync();

            Assert.Null(await RewardAsync(players[0]));
            Assert.Null(await RewardAsync(players[1]));
        }

        [Fact]
        public async Task TheBoardRanksFromTheSameRowsAndShowsTheServersTotal()
        {
            await OpenAsync();
            var players = await PlayersAsync(3);
            await DealAsync(players, new long[] { 2_000, 7_000, 1_000 });

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var view = await WorldBossBoard.ViewAsync(db, players[2]);

            Assert.Equal(10_000, view.TotalDamage);
            Assert.Equal(3, view.Participants);
            Assert.Equal(new[] { players[1], players[0], players[2] }, view.Top.Select(r => r.PlayerId));
            Assert.Equal(new[] { 1, 2, 3 }, view.Top.Select(r => r.Rank));
            Assert.All(view.Top, r => Assert.StartsWith("boss_", r.Name));
            Assert.Equal(3, view.Me!.Rank);
            Assert.Equal("Participation", view.MyBracket);

            var stranger = await WorldBossBoard.ViewAsync(db, 1);
            Assert.Null(stranger.Me);
            Assert.Null(stranger.MyBracket);
        }

        [Theory]
        [InlineData(1, 1, "Participation")] // a percentile rank: alone is the 100th, never "top"
        [InlineData(1, 200, "Top 1%")]
        [InlineData(2, 200, "Top 1%")]
        [InlineData(3, 200, "Top 10%")]
        [InlineData(20, 200, "Top 10%")]
        [InlineData(100, 200, "Top 50%")]
        [InlineData(101, 200, "Participation")]
        public void TheBracketsAreTheOnesThePayoutMails(int rank, int participants, string bracket)
        {
            Assert.Equal(bracket, WorldBossBoard.BracketFor(rank, participants).Bracket);
        }
    }
}

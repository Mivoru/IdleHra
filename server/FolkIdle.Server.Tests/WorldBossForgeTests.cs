using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Svarog's forge (owner 2026-11-15, option (a)): right duel calls fill it,
    /// a full forge pays everyone who fed that fill, and nothing else moves.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossForgeTests
    {
        private readonly PostgresTestFixture _fixture;
        public WorldBossForgeTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task<long[]> PlayersAsync(int count)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var players = Enumerable.Range(0, count).Select(_ => new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"forge_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 20,
            }).ToList();
            db.PlayerRecords.AddRange(players);
            await db.SaveChangesAsync();
            return players.Select(p => p.Id).ToArray();
        }

        // A boss instance of the test's own, so other tests' rows never count as feeders.
        private static long Instance() => 900_000 + Random.Shared.Next(1, 99_999);

        private static async Task<bool> StrikeAsync(FolkIdleDbContext db, WorldBossSnapshot snapshot, long playerId, int rightCalls)
        {
            var attempt = await db.PlayerWorldBossAttempts.SingleOrDefaultAsync(a => a.PlayerId == playerId && a.BossInstanceId == snapshot.BossInstanceId);
            if (attempt == null)
            {
                attempt = new PlayerWorldBossAttempt { PlayerId = playerId, BossInstanceId = snapshot.BossInstanceId };
                db.PlayerWorldBossAttempts.Add(attempt);
            }
            bool filled = await WorldBossForge.FeedAsync(db, snapshot, attempt, rightCalls, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return filled;
        }

        private async Task<int> ForgeMailAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.MailboxInstances.CountAsync(m => m.PlayerId == playerId && m.SenderName == WorldBossForge.SenderName);
        }

        [Fact]
        public async Task AFullForge_PaysEveryoneWhoFedThatFill_AndNobodyElse()
        {
            var p = await PlayersAsync(4);
            var snapshot = new WorldBossSnapshot { BossInstanceId = Instance(), CurrentHp = 1_000, MaxHp = 1_000 };
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.False(await StrikeAsync(db, snapshot, p[0], 3));
                Assert.False(await StrikeAsync(db, snapshot, p[1], 0)); // a quick strike feeds nothing
                Assert.False(await StrikeAsync(db, snapshot, p[2], 3));
                Assert.Equal(6, snapshot.ForgeCharge);
                for (int i = 0; i < 2; i++) Assert.False(await StrikeAsync(db, snapshot, p[0], 3));
                Assert.True(await StrikeAsync(db, snapshot, p[3], 3)); // 15th ember
            }

            Assert.Equal(1, snapshot.ForgeFills);
            Assert.Equal(0, snapshot.ForgeCharge);
            Assert.True(snapshot.ForgeLastFillEpoch > 0);
            Assert.Equal(1_000, snapshot.CurrentHp); // the hammer is not damage
            Assert.Equal(1, await ForgeMailAsync(p[0]));
            Assert.Equal(0, await ForgeMailAsync(p[1]));
            Assert.Equal(1, await ForgeMailAsync(p[2]));
            Assert.Equal(1, await ForgeMailAsync(p[3]));
        }

        [Fact]
        public async Task TheSecondFill_PaysOnlyItsOwnFeeders()
        {
            var p = await PlayersAsync(2);
            var snapshot = new WorldBossSnapshot { BossInstanceId = Instance(), ForgeCharge = WorldBossForge.EmbersToFill - 1 };
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(await StrikeAsync(db, snapshot, p[0], 1));      // fill 0, paid to p0
                Assert.False(await StrikeAsync(db, snapshot, p[1], 3));     // fill 1 begins
                snapshot.ForgeCharge = WorldBossForge.EmbersToFill - 1;
                Assert.True(await StrikeAsync(db, snapshot, p[1], 2));      // fill 1, surplus kept
            }
            Assert.Equal(2, snapshot.ForgeFills);
            Assert.Equal(1, snapshot.ForgeCharge);
            Assert.Equal(1, await ForgeMailAsync(p[0]));
            Assert.Equal(1, await ForgeMailAsync(p[1]));
        }

        [Fact]
        public void ANewEncounter_StartsCold()
        {
            var s = new WorldBossSnapshot { ForgeCharge = 9, ForgeFills = 3, ForgeLastFillEpoch = 42 };
            WorldBossForge.Reset(s);
            Assert.Equal(0, s.ForgeCharge);
            Assert.Equal(0, s.ForgeFills);
            Assert.Equal(0, s.ForgeLastFillEpoch);
        }

        [Fact]
        public void ANewStrikeRow_HasFedNoFill() => Assert.Equal(-1, new PlayerWorldBossAttempt().ForgeFillFed);
    }
}

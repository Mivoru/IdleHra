using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A world boss window that a developer can open on any day of the month.
    ///
    /// Modul: THE CALENDAR WAS THE ONLY WAY IN, AND IT HID A DEAD BUTTON.
    ///
    /// Windows run on the 1st-7th and 15th-22nd. On every other day nothing -
    /// not `exercise.mjs`, not a developer - could press Strike, and a SQL poke
    /// to EventState = 1 was finalised by LiveOps as "window expired" within one
    /// 60-second tick. So "no attack has ever landed" (task 25) could not be
    /// reproduced on the day it was reported. These pin an override that
    /// LiveOps respects, against an injected clock so they do not depend on
    /// today's date.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossWindowOverrideTests
    {
        private readonly PostgresTestFixture _fixture;

        public WorldBossWindowOverrideTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        // The 10th is outside both calendar windows (1-7, 15-22) in every month.
        private static readonly DateTimeOffset DormantDay = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        private (WorldBossEngine Boss, LiveOpsTickEngine LiveOps) Build()
        {
            var boss = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var liveOps = new LiveOpsTickEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry, boss, null!, () => DormantDay);
            return (boss, liveOps);
        }

        private async Task<WorldBossSnapshot> ReadSnapshotAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.WorldBossSnapshots.AsNoTracking()
                .SingleAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
        }

        [Fact]
        public async Task AManualWindowSurvivesALiveOpsTickOutsideTheCalendar()
        {
            var (boss, liveOps) = Build();
            await boss.EnsureSnapshotAsync();

            await boss.OpenManualWindowAsync(900, DormantDay.ToUnixTimeSeconds());
            Assert.True(boss.IsEventActive);

            // The tick that used to kill a poked window within a minute.
            await liveOps.EvaluateWorldBossEventWindowAsync();

            Assert.True(boss.IsEventActive, "LiveOps finalised a manual window on a dormant calendar day.");
            Assert.Equal(1, (await ReadSnapshotAsync()).EventState);

            await boss.CloseManualWindowAsync();
        }

        [Fact]
        public async Task ClosingTheManualWindowConcludesIt_AndTheNextTickDoesNotReopenIt()
        {
            var (boss, liveOps) = Build();
            await boss.EnsureSnapshotAsync();
            await boss.OpenManualWindowAsync(900, DormantDay.ToUnixTimeSeconds());

            await boss.CloseManualWindowAsync();
            Assert.False(boss.IsEventActive);
            Assert.Equal(2, (await ReadSnapshotAsync()).EventState);

            await liveOps.EvaluateWorldBossEventWindowAsync();
            Assert.False(boss.IsEventActive);
            Assert.Equal(2, (await ReadSnapshotAsync()).EventState);
        }

        [Fact]
        public async Task AManualWindowThatRunsOutIsConcludedByTheNextTick()
        {
            var (boss, liveOps) = Build();
            await boss.EnsureSnapshotAsync();

            // Opened 1,000 seconds before the clock, for 900: already over.
            await boss.OpenManualWindowAsync(900, DormantDay.ToUnixTimeSeconds() - 1000);
            Assert.False(boss.IsManualWindowOpen(DormantDay.ToUnixTimeSeconds()));

            await liveOps.EvaluateWorldBossEventWindowAsync();
            Assert.False(boss.IsEventActive);
        }

        [Fact]
        public async Task OpeningClearsAttempts()
        {
            var (boss, _) = Build();
            await boss.EnsureSnapshotAsync();

            const long playerId = 970_025_901L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"player_world_boss_attempts\" WHERE \"PlayerId\" = {0}", playerId);
                db.PlayerWorldBossAttempts.Add(new PlayerWorldBossAttempt
                {
                    PlayerId = playerId,
                    BossInstanceId = WorldBossEngine.ActiveBossInstanceId,
                    AttemptCount = 3,
                    SessionStartEpoch = 1,
                });
                await db.SaveChangesAsync();
            }

            await boss.OpenManualWindowAsync(900);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.False(await db.PlayerWorldBossAttempts.AnyAsync(a => a.PlayerId == playerId));
            }

            await boss.CloseManualWindowAsync();
        }

        [Fact]
        public void TheDurationIsClamped()
        {
            Assert.Equal(60L, WorldBossEngine.ClampManualWindowSeconds(1));
            Assert.Equal(900L, WorldBossEngine.ClampManualWindowSeconds(900));
            Assert.Equal(86_400L, WorldBossEngine.ClampManualWindowSeconds(10_000_000));
        }
    }
}

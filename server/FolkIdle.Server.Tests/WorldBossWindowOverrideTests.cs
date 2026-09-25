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

        // A Thursday (2026-09-10). It was chosen as outside the OLD calendar's
        // windows (1-7, 15-22); since 2026-09-25 every day is inside a week's
        // encounter, and the name is kept for the tests' history.
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

        // Modul: SINCE 2026-09-25 THERE IS ALWAYS A BOSS (one encounter a week,
        // Monday to Sunday, back to back). These two used to pin "and then
        // nothing until the next calendar window". Now closing or outliving a
        // dev window hands the boss back to the calendar, which opens THIS
        // week's encounter on the next tick.
        [Fact]
        public async Task ClosingTheManualWindowHandsTheBossBackToTheWeek()
        {
            var (boss, liveOps) = Build();
            await boss.EnsureSnapshotAsync();
            await boss.OpenManualWindowAsync(900, DormantDay.ToUnixTimeSeconds());

            await boss.CloseManualWindowAsync();
            Assert.False(boss.IsEventActive);
            Assert.Equal(2, (await ReadSnapshotAsync()).EventState);

            await liveOps.EvaluateWorldBossEventWindowAsync();
            Assert.True(boss.IsEventActive);
            Assert.Equal(WorldBossCalendar.WeekEndEpoch(DormantDay), boss.EventEndEpoch);
        }

        [Fact]
        public async Task AManualWindowThatRunsOutIsReplacedByTheWeeksEncounter()
        {
            var (boss, liveOps) = Build();
            await boss.EnsureSnapshotAsync();

            // Opened 1,000 seconds before the clock, for 900: already over.
            await boss.OpenManualWindowAsync(900, DormantDay.ToUnixTimeSeconds() - 1000);
            Assert.False(boss.IsManualWindowOpen(DormantDay.ToUnixTimeSeconds()));

            await liveOps.EvaluateWorldBossEventWindowAsync();
            Assert.True(boss.IsEventActive);
            Assert.Equal(WorldBossCalendar.WeekEndEpoch(DormantDay), boss.EventEndEpoch);
        }

        [Fact]
        public async Task ADefeatedBossStaysDefeatedUntilMonday_ThenANewOneArrives()
        {
            var clock = DormantDay; // a Thursday
            var boss = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var liveOps = new LiveOpsTickEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry, boss, null!, () => clock);
            await boss.EnsureSnapshotAsync();
            await boss.CloseManualWindowAsync();

            await liveOps.EvaluateWorldBossEventWindowAsync();
            Assert.True(boss.IsEventActive);
            long thisWeekEnd = WorldBossCalendar.WeekEndEpoch(clock);

            // Defeat it on Thursday: the snapshot keeps this week's end.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"WorldBossSnapshots\" SET \"CurrentHp\" = 0 WHERE \"BossInstanceId\" = {0}", (long)WorldBossEngine.ActiveBossInstanceId);
            }
            await boss.EnsureSnapshotAsync();
            await liveOps.EvaluateWorldBossEventWindowAsync(); // pays out and concludes
            Assert.False(boss.IsEventActive);
            Assert.Equal(thisWeekEnd, boss.EventEndEpoch);

            // Saturday: still this week, so it stays down.
            clock = DormantDay.AddDays(2);
            await liveOps.EvaluateWorldBossEventWindowAsync();
            Assert.False(boss.IsEventActive);

            // Monday 00:00:30 UTC: next week, a new boss.
            clock = WorldBossCalendar.WeekStart(DormantDay).AddDays(7).AddSeconds(30);
            await liveOps.EvaluateWorldBossEventWindowAsync();
            Assert.True(boss.IsEventActive);
            Assert.Equal(WorldBossCalendar.WeekEndEpoch(clock), boss.EventEndEpoch);
            Assert.Equal(thisWeekEnd + 7 * 86_400, boss.EventEndEpoch);
        }

        [Fact]
        public async Task AWeekThatEndsWithTheBossAliveIsConcludedAndTheNextWeekOpens()
        {
            var clock = DormantDay;
            var boss = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var liveOps = new LiveOpsTickEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry, boss, null!, () => clock);
            await boss.EnsureSnapshotAsync();
            await boss.CloseManualWindowAsync();
            await liveOps.EvaluateWorldBossEventWindowAsync();
            long firstEnd = boss.EventEndEpoch;

            clock = DateTimeOffset.FromUnixTimeSeconds(firstEnd + 60); // Monday 00:00:59
            await liveOps.EvaluateWorldBossEventWindowAsync();

            Assert.True(boss.IsEventActive);
            Assert.Equal(firstEnd + 7 * 86_400, boss.EventEndEpoch);
        }

        [Fact]
        public void TheWeekRunsMondayToSundayUtc()
        {
            // 2026-09-10 is a Thursday.
            Assert.Equal(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero), WorldBossCalendar.WeekStart(DormantDay));
            Assert.Equal(new DateTimeOffset(2026, 9, 13, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds(), WorldBossCalendar.WeekEndEpoch(DormantDay));

            // The edges: Sunday's last second is the old week, Monday's first is the new.
            var sundayLast = new DateTimeOffset(2026, 9, 13, 23, 59, 59, TimeSpan.Zero);
            var mondayFirst = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal(WorldBossCalendar.WeekEndEpoch(DormantDay), WorldBossCalendar.WeekEndEpoch(sundayLast));
            Assert.Equal(WorldBossCalendar.WeekEndEpoch(DormantDay) + 7 * 86_400, WorldBossCalendar.WeekEndEpoch(mondayFirst));

            // A Monday is the start of its own week, not the end of the last one.
            Assert.Equal(mondayFirst, WorldBossCalendar.WeekStart(mondayFirst));
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

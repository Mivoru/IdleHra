using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The new-player funnel (task 39, plan item 4): the first time each player
    /// reaches each step lands in player_funnel_events, once, whichever path
    /// got them there.
    ///
    /// Modul: every test that starts the worker stops it in a finally. The
    /// queue is STATIC (CLAUDE.md "static queues"), so a worker left running
    /// drains other tests' events into this collection's database.
    /// </summary>
    [Collection("Postgres collection")]
    public class FunnelRecorderTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public FunnelRecorderTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private async Task<short[]> StepsOfAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerFunnelEvents.AsNoTracking()
                .Where(f => f.PlayerId == playerId)
                .OrderBy(f => f.Step)
                .Select(f => f.Step)
                .ToArrayAsync();
        }

        /// <summary>Runs a real worker until <paramref name="done"/> holds, then stops it.</summary>
        private async Task RunWorkerUntilAsync(long playerId, Func<short[], bool> done, string what)
        {
            var recorder = new FunnelRecorder(_fixture.ServiceProvider);
            try
            {
                recorder.StartCron();

                var deadline = DateTime.UtcNow.AddSeconds(30);
                short[] steps = Array.Empty<short>();
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250);
                    steps = await StepsOfAsync(playerId);
                    if (done(steps)) return;
                }

                Assert.Fail($"{what}: player {playerId} has steps [{string.Join(", ", steps)}] after 30 s");
            }
            finally
            {
                recorder.StopCron();
            }
        }

        /// <summary>Drains whatever is queued right now, synchronously, without a worker.</summary>
        private async Task DrainNowAsync()
        {
            var recorder = new FunnelRecorder(_fixture.ServiceProvider);
            // Bounded: other collections enqueue into the same static queue.
            for (int cycle = 0; cycle < 5 && await recorder.DrainOneCycleAsync() > 0; cycle++) { }
        }

        private async Task SeedPlayerAsync(long playerId, long epoch)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM player_funnel_events WHERE \"PlayerId\" = {0}", playerId);
            var existing = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
            if (existing != null) db.PlayerRecords.Remove(existing);
            await db.SaveChangesAsync();

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                LogicEpochCounter = epoch,
                BaseStrength = 50,
                BaseDexterity = 50,
                BaseConstitution = 50,
                BaseLuck = 25
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task AFreshRegistrationAndOneKillGiveStepsOneAndTwo()
        {
            string tag = Guid.NewGuid().ToString("N").Substring(0, 10);
            var registration = await AuthenticationEngine.RegisterWithEmailAsync(
                _fixture.RetryingOptions, $"funnel_{tag}@example.com", $"funnel_{tag}", "a good long password", null);
            Assert.Equal(EmailRegisterOutcome.Success, registration.Outcome);
            long playerId = registration.PlayerId;

            // A login follows a registration, and re-arms the per-session guard.
            FunnelRecorder.BeginSession(playerId);

            var payload = new TickStatePayload { PlayerId = playerId, CurrentLevel = 1, SelectedLineageId = 1 };
            ProgressionEngine.ProcessMonsterDeath(ref payload, baseExpReward: 10, xpMultiplier: 100, activeGlobalEventId: 0);

            await RunWorkerUntilAsync(playerId,
                steps => steps.Contains((short)FunnelStep.Registered) && steps.Contains((short)FunnelStep.FirstKill),
                "registration + first kill");

            Assert.Equal(new short[] { (short)FunnelStep.Registered, (short)FunnelStep.FirstKill }, await StepsOfAsync(playerId));
        }

        [Fact]
        public async Task ARepeatedStepDoesNotDuplicateAndKeepsTheFirstTime()
        {
            const long playerId = 983_039_001L;
            await SeedPlayerAsync(playerId, epoch: 1);
            FunnelRecorder.BeginSession(playerId);

            FunnelRecorder.Record(playerId, FunnelStep.FirstCraft);

            // Inside one session the guard swallows the repeat before it is
            // ever queued - the hot-path half of "once".
            FunnelRecorder.Record(playerId, FunnelStep.FirstCraft);
            Assert.Equal(1, FunnelRecorder.Queue.Count(e => e.PlayerId == playerId && e.Step == (short)FunnelStep.FirstCraft));

            await DrainNowAsync();

            DateTime firstAt;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                firstAt = (await db.PlayerFunnelEvents.AsNoTracking()
                    .SingleAsync(f => f.PlayerId == playerId && f.Step == (short)FunnelStep.FirstCraft)).At;
            }

            // A new session re-offers the step; the database's ON CONFLICT
            // DO NOTHING is the durable half of "once".
            await Task.Delay(20);
            FunnelRecorder.BeginSession(playerId);
            FunnelRecorder.Record(playerId, FunnelStep.FirstCraft);
            await DrainNowAsync();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var rows = await db.PlayerFunnelEvents.AsNoTracking()
                    .Where(f => f.PlayerId == playerId && f.Step == (short)FunnelStep.FirstCraft)
                    .ToArrayAsync();
                Assert.Single(rows);
                Assert.Equal(firstAt, rows[0].At);
            }
        }

        /// <summary>
        /// THREE PATHS GROW A LEVEL (CLAUDE.md), and the level steps are hooked
        /// in FlushState precisely so that one hook covers all of them. This pins
        /// that: a level reached offline and a level reached through the bulk
        /// (warp) path both reach the funnel through the checkpoint.
        /// </summary>
        [Fact]
        public async Task ALevelReachedOfflineRecordsThroughTheCheckpoint()
        {
            const long playerId = 983_039_002L;
            const long elapsedOfflineSeconds = 3600L;
            const int monsterId = 31;
            await SeedPlayerAsync(playerId, epoch: 1);
            FunnelRecorder.BeginSession(playerId);
            ContentRegistry.Initialize();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = 1,
                LastLogoutTimestamp = now - elapsedOfflineSeconds,
                ActiveActivityId = monsterId,
                CurrentLevel = 4,
                CurrentXp = ProgressionEngine.GetRequiredXpForLevel(4) - 1,
                SelectedLineageId = 1,
                InventorySpaceRemaining = 1000,
                Food1_ItemId = ContentRegistry.RawFishItemIds.First(),
                Food1_Count = 100000,
                STR = 50, DEX = 50, CON = 50, LCK = 25,
            };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                payload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, payload, now);
            }
            _output.WriteLine($"offline: level {payload.CurrentLevel}");
            Assert.True(payload.CurrentLevel >= 5, "the offline window did not reach level 5 - the test setup is wrong, not the funnel");

            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            Assert.True(await manager.FlushState(payload));

            await RunWorkerUntilAsync(playerId,
                steps => steps.Contains((short)FunnelStep.Level5) && steps.Contains((short)FunnelStep.FirstKill),
                "offline level 5");
        }

        [Fact]
        public async Task ALevelReachedThroughTheBulkPathRecordsThroughTheCheckpoint()
        {
            const long playerId = 983_039_003L;
            await SeedPlayerAsync(playerId, epoch: 1);
            FunnelRecorder.BeginSession(playerId);

            var payload = new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = 1,
                CurrentLevel = 1,
                SelectedLineageId = 1,
                InventorySpaceRemaining = 20,
                STR = 50, DEX = 50, CON = 50, LCK = 25,
            };

            long xpForTen = 0;
            for (int level = 1; level <= 10; level++) xpForTen += ProgressionEngine.GetRequiredXpForLevel(level);

            // Modul: private and, as of 2026-09-27, with no caller left (the
            // warp feature went with chrono). Reached by reflection so the
            // three-paths rule stays pinned if it is ever wired back.
            var bulk = typeof(SimulationEngine).GetMethod("ApplyBulkExperience", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(bulk);
            object[] args = { payload, xpForTen, 0 };
            bulk!.Invoke(null, args);
            payload = (TickStatePayload)args[0];
            _output.WriteLine($"bulk: level {payload.CurrentLevel}");
            Assert.True(payload.CurrentLevel >= 10);

            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            Assert.True(await manager.FlushState(payload));

            await RunWorkerUntilAsync(playerId,
                steps => steps.Contains((short)FunnelStep.Level5) && steps.Contains((short)FunnelStep.Level10),
                "bulk level 10");

            Assert.DoesNotContain((short)FunnelStep.Level20, await StepsOfAsync(playerId));
        }

        [Fact]
        public async Task ALoginADayAfterRegistrationIsADayOneReturn()
        {
            const long playerId = 983_039_004L;
            await SeedPlayerAsync(playerId, epoch: 1);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerFunnelEvents.Add(new PlayerFunnelEvent
                {
                    PlayerId = playerId,
                    Step = (short)FunnelStep.Registered,
                    At = DateTime.UtcNow.AddHours(-30),
                });
                await db.SaveChangesAsync();
            }

            FunnelRecorder.BeginSession(playerId);
            await DrainNowAsync();

            var steps = await StepsOfAsync(playerId);
            Assert.Contains((short)FunnelStep.ReturnedD1, steps);
            Assert.DoesNotContain((short)FunnelStep.ReturnedD7, steps);
        }

        /// <summary>
        /// ONE WRITER PER STEP. Two call sites for one step is how a funnel
        /// starts counting something nobody meant, so the count of each
        /// <c>FunnelStep.X</c> outside FunnelRecorder.cs is pinned here.
        /// </summary>
        [Fact]
        public void EachStepHasItsDocumentedWriters()
        {
            var expected = new Dictionary<string, int>
            {
                ["Registered"] = 2,      // AuthenticationEngine: device route + email route
                ["FirstKill"] = 2,       // ProgressionEngine (live) + OfflineSimulationEngine (away)
                ["FirstEquip"] = 1,
                ["FirstCraft"] = 1,
                ["OnboardingDone"] = 0,  // FunnelRecorder.RecordCheckpoint, from FlushState
                ["Region1Boss"] = 1,
                ["Level5"] = 0,          // RecordCheckpoint
                ["Level10"] = 0,
                ["Level20"] = 0,
                ["JoinedGuild"] = 1,     // GuildManagementEngine.PublishJoined
                ["ReturnedD1"] = 0,      // the worker, from BeginSession's login probe
                ["ReturnedD7"] = 0,
            };

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            string root = Path.Combine(dir!.FullName, "server", "FolkIdle.Server");

            var counts = expected.Keys.ToDictionary(k => k, _ => 0);
            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                    || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || Path.GetFileName(path) == "FunnelRecorder.cs")
                {
                    continue;
                }

                foreach (Match m in Regex.Matches(File.ReadAllText(path), @"FunnelStep\.(\w+)"))
                {
                    if (counts.ContainsKey(m.Groups[1].Value)) counts[m.Groups[1].Value]++;
                }
            }

            foreach (var (step, want) in expected)
            {
                _output.WriteLine($"{step,-16} {counts[step]} writer(s), expected {want}");
            }
            Assert.Equal(expected, counts);
            Assert.Equal(expected.Count, Enum.GetValues<FunnelStep>().Length);
        }
    }
}

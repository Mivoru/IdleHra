using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: TASK 51 - personal records. Pins the rules (a record only ever
    /// improves), the round trip (a relogin reads what the session set - the
    /// WorldBossAttemptCount lesson), the loot worker's conditional write, and
    /// that every writer is still wired where the event passes.
    /// </summary>
    [Collection("Postgres collection")]
    public class PersonalRecordsTests
    {
        private readonly PostgresTestFixture _fixture;

        public PersonalRecordsTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void AHitRecordOnlyRises()
        {
            var payload = new TickStatePayload();
            PersonalRecords.ObserveHit(ref payload, 120);
            PersonalRecords.ObserveHit(ref payload, 90);
            Assert.Equal(120, payload.BestHit);
            PersonalRecords.ObserveHit(ref payload, 121);
            Assert.Equal(121, payload.BestHit);
        }

        [Fact]
        public void ABossTimeOnlyFalls_AndZeroMeansNeverKilled()
        {
            var payload = new TickStatePayload();
            PersonalRecords.ObserveBossKill(ref payload, 3, 450);
            PersonalRecords.ObserveBossKill(ref payload, 3, 500);
            Assert.Equal(450, payload.BossBestKillTenthsR3);
            PersonalRecords.ObserveBossKill(ref payload, 3, 300);
            Assert.Equal(300, payload.BossBestKillTenthsR3);
            Assert.Equal(0, payload.BossBestKillTenthsR2);

            // Out of range and zero-length fights are ignored, not recorded.
            PersonalRecords.ObserveBossKill(ref payload, 6, 10);
            PersonalRecords.ObserveBossKill(ref payload, 1, 0);
            Assert.Equal(0, payload.BossBestKillTenthsR1);

            Assert.Equal(5, PersonalRecords.FasterOf(0, 5));
            Assert.Equal(5, PersonalRecords.FasterOf(5, 0));
            Assert.Equal(4, PersonalRecords.FasterOf(5, 4));
        }

        [Fact]
        public async Task RecordsSurviveAFlushAndARelogin_AndAnOlderSnapshotCannotUndoThem()
        {
            const long playerId = 950_151_001L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 10 });
                db.CharacterRecords.Add(new CharacterRecord { Id = Guid.NewGuid(), PlayerId = playerId, AgePhase = 1, SlotIndex = 0 });
                await db.SaveChangesAsync();
            }

            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);
            var live = await checkpoints.LoadPlayerState(playerId);
            PersonalRecords.ObserveHit(ref live, 777);
            PersonalRecords.ObserveBossKill(ref live, 2, 425);
            Assert.True(await checkpoints.FlushState(live));

            var relogin = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(777, relogin.BestHit);
            Assert.Equal(425, relogin.BossBestKillTenthsR2);

            // A payload that somehow carries worse numbers (a session that
            // began before the record) merges, it does not overwrite.
            relogin.BestHit = 10;
            relogin.BossBestKillTenthsR2 = 900;
            Assert.True(await checkpoints.FlushState(relogin));

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var row = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            Assert.Equal(777, row.BestHit);
            Assert.Equal(425, row.BossBestKillTenthsR2);
        }

        [Fact]
        public async Task TheBestDropIsWrittenOnlyWhenItBeatsTheStoredOne()
        {
            const long playerId = 950_151_002L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            var first = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(1, await PersonalRecords.RecordBestDropAsync(db, playerId, 7, "eq_first", first));
                Assert.Equal(0, await PersonalRecords.RecordBestDropAsync(db, playerId, 7, "eq_tie", first.AddHours(1)));
                Assert.Equal(0, await PersonalRecords.RecordBestDropAsync(db, playerId, 5, "eq_worse", first.AddHours(2)));
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var row = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            Assert.Equal(7, row.BestDropTier);
            Assert.Equal("eq_first", row.BestDropBaseId);
            Assert.Equal(first, row.BestDropAtUtc);
        }

        /// <summary>
        /// Each writer is one line in a large file. Deleting one compiles and
        /// passes every test above - so the call sites are pinned here.
        /// </summary>
        [Fact]
        public void EveryWriterIsStillWired()
        {
            string Read(params string[] parts) => File.ReadAllText(Path.Combine(ServerRoot(), Path.Combine(parts)));

            string sim = Read("Domain", "Combat", "SimulationEngine.cs");
            Assert.Contains("PersonalRecords.ObserveHit(ref payload", sim);
            Assert.Contains("PersonalRecords.ObserveBossKill(", sim);
            Assert.Contains("BestHit = currentPayload.BestHit", sim);

            string checkpoint = Read("Domain", "Shared", "StateCheckpointManager.cs");
            // The periodic flush AND the graceful-shutdown batch.
            Assert.Equal(2, Regex.Matches(checkpoint, @"PersonalRecords\.MergeInto\(player, in state\)").Count);
            // Hydration is pinned by StateUpdatePacketFieldCoverageTests.

            string loot = Read("Engine", "CombatLootEngine.cs");
            Assert.Contains("PersonalRecords.RecordBestDropAsync(", loot);

            // The read side. Its first edit was lost to a hook-blocked command and
            // everything above still passed - only exercise.mjs noticed.
            string router = Read("Network", "NetworkBroadcastSystem.cs");
            Assert.Contains("requestPath == \"/api/v1/player/records\"", router);
            Assert.Contains("private async Task HandlePlayerRecords(", router);
        }

        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server");
        }
    }
}

using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: "NEVER AUTO-EAT" CAME BACK ON AT THE NEXT LOGIN, 2026-11-26.
    ///
    /// The Larder offers 0 as "never auto-eat", but the column's 0 also
    /// meant "never touched the slider", and LoadPlayerState resolved it to
    /// the default. So a player who turned auto-eat off was eating again at
    /// 30% after a relogin. Off is now stored as -1 (AutoEatDefaults.StoredOff).
    /// </summary>
    [Collection("Postgres collection")]
    public class AutoEatOffPersistenceTests
    {
        private readonly PostgresTestFixture _fixture;

        public AutoEatOffPersistenceTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task SeedAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 10 });
            db.CharacterRecords.Add(new CharacterRecord { Id = Guid.NewGuid(), PlayerId = playerId, AgePhase = 1, SlotIndex = 0 });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task AnUntouchedSliderStillLoadsTheDefault()
        {
            const long playerId = 950_489_001L;
            await SeedAsync(playerId);
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var live = await checkpoints.LoadPlayerState(playerId);

            Assert.Equal(AutoEatDefaults.ThresholdPct, live.AutoEatThreshold);
        }

        [Fact]
        public async Task OffSetFromTheLarderSurvivesARelogin()
        {
            const long playerId = 950_489_002L;
            await SeedAsync(playerId);
            var larder = new LarderEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await larder.PersistAutoEatThresholdAsync(playerId, 0);
            var relogin = await new StateCheckpointManager(_fixture.ServiceProvider).LoadPlayerState(playerId);

            Assert.Equal(0, relogin.AutoEatThreshold);
        }

        [Fact]
        public async Task OffOnTheLivePayloadSurvivesAFlushAndARelogin()
        {
            const long playerId = 950_489_003L;
            await SeedAsync(playerId);
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var live = await checkpoints.LoadPlayerState(playerId);
            live.AutoEatThreshold = 0;
            Assert.True(await checkpoints.FlushState(live));
            var relogin = await checkpoints.LoadPlayerState(playerId);

            Assert.Equal(0, relogin.AutoEatThreshold);
        }

        [Fact]
        public async Task AChosenThresholdSurvivesAFlushAndARelogin()
        {
            const long playerId = 950_489_004L;
            await SeedAsync(playerId);
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var live = await checkpoints.LoadPlayerState(playerId);
            live.AutoEatThreshold = 45;
            Assert.True(await checkpoints.FlushState(live));
            var relogin = await checkpoints.LoadPlayerState(playerId);

            Assert.Equal(45, relogin.AutoEatThreshold);
        }
    }
}

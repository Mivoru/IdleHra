using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: CHILDREN NEVER GREW UP, 2026-10-23.
    ///
    /// The tick aged a working slot on the payload, and only the shutdown
    /// flush (once per deploy) wrote AgeTicks back. Every relogin and every
    /// mid-session reload rebuilt the payload from the stale row, so a child
    /// fielded for days stayed a child. See
    /// StateCheckpointManager.PersistFieldedAgesAsync.
    /// </summary>
    [Collection("Postgres collection")]
    public class FieldedAgePersistenceTests
    {
        private readonly PostgresTestFixture _fixture;

        public FieldedAgePersistenceTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task SeedAsync(long playerId, params CharacterRecord[] characters)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 10 });
            db.CharacterRecords.AddRange(characters);
            await db.SaveChangesAsync();
        }

        private async Task<CharacterRecord> RowAsync(Guid characterId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return (await db.CharacterRecords.FindAsync(characterId))!;
        }

        [Fact]
        public async Task EverySlotsAgeSurvivesAFlushAndAFreshLoad()
        {
            const long playerId = 950_026_001L;
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var child = Guid.NewGuid();
            await SeedAsync(playerId,
                new CharacterRecord { Id = first, PlayerId = playerId, AgePhase = AgePhaseCurve.Adult, AgeTicks = AgePhaseCurve.ChildEndTicks, SlotIndex = 0 },
                new CharacterRecord { Id = second, PlayerId = playerId, AgePhase = AgePhaseCurve.Adult, AgeTicks = AgePhaseCurve.ChildEndTicks, SlotIndex = 1 },
                new CharacterRecord { Id = child, PlayerId = playerId, AgePhase = AgePhaseCurve.Child, AgeTicks = 0L, SlotIndex = 2 });
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var live = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(child, live.Slot3_CharacterId);
            // An hour of work on each slot, as the tick leaves the payload.
            live.Slot1_AgeTicks += AgePhaseCurve.TicksPerHour;
            live.Slot2_AgeTicks += AgePhaseCurve.TicksPerHour;
            live.Slot3_AgeTicks = AgePhaseCurve.ChildEndTicks;
            live.Slot3_AgePhase = AgePhaseCurve.Adult;

            Assert.True(await checkpoints.FlushState(live));
            var relogin = await checkpoints.LoadPlayerState(playerId);

            Assert.Equal(AgePhaseCurve.ChildEndTicks + AgePhaseCurve.TicksPerHour, relogin.Slot1_AgeTicks);
            Assert.Equal(AgePhaseCurve.ChildEndTicks + AgePhaseCurve.TicksPerHour, relogin.Slot2_AgeTicks);
            Assert.Equal(AgePhaseCurve.ChildEndTicks, relogin.Slot3_AgeTicks);
            Assert.Equal(AgePhaseCurve.Adult, relogin.Slot3_AgePhase);
        }

        /// <summary>
        /// A stale snapshot is older than the row, so it must never take time
        /// back off a character.
        /// </summary>
        [Fact]
        public async Task AStaleFlushNeverMakesACharacterYounger()
        {
            const long playerId = 950_026_002L;
            var fielded = Guid.NewGuid();
            await SeedAsync(playerId,
                new CharacterRecord { Id = fielded, PlayerId = playerId, AgePhase = AgePhaseCurve.Child, AgeTicks = 1000L, SlotIndex = 0 });
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);
            var stale = await checkpoints.LoadPlayerState(playerId);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var row = await db.CharacterRecords.FindAsync(fielded);
                row!.AgeTicks = AgePhaseCurve.ChildEndTicks;
                row.AgePhase = AgePhaseCurve.Adult;
                await db.SaveChangesAsync();
            }

            Assert.True(await checkpoints.FlushState(stale));

            var after = await RowAsync(fielded);
            Assert.Equal(AgePhaseCurve.ChildEndTicks, after.AgeTicks);
            Assert.Equal(AgePhaseCurve.Adult, after.AgePhase);
        }
    }
}

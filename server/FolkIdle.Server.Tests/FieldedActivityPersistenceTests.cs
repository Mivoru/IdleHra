using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: A RELOGIN BROUGHT THE FIGHTER BACK IDLE, 2026-09-23.
    ///
    /// The single-character ChangeActivity path sets ActiveActivityId on the
    /// live payload only, and LoadPlayerState reads it from the characters
    /// row - which no flush wrote. Every relogin came back idle, and offline
    /// catch-up simulated the idle activity it loaded. See
    /// StateCheckpointManager.PersistFieldedActivityAsync.
    /// </summary>
    [Collection("Postgres collection")]
    public class FieldedActivityPersistenceTests
    {
        // LoadPlayerState copies the row; it does not validate the id.
        private const long FieldMouse = 91L;

        private readonly PostgresTestFixture _fixture;

        public FieldedActivityPersistenceTests(PostgresTestFixture fixture)
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

        private async Task<long> RowActivityAsync(Guid characterId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var row = await db.CharacterRecords.FindAsync(characterId);
            return row!.ActiveActivityId;
        }

        [Fact]
        public async Task AnInMemoryActivitySurvivesAFlushAndAFreshLoad()
        {
            const long playerId = 950_025_001L;
            var fielded = Guid.NewGuid();
            await SeedAsync(playerId, new CharacterRecord { Id = fielded, PlayerId = playerId, AgePhase = 1, SlotIndex = 0 });
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var live = await checkpoints.LoadPlayerState(playerId);
            live.ActiveActivityId = FieldMouse; // what the legacy ChangeActivity branch does

            Assert.True(await checkpoints.FlushState(live));
            var relogin = await checkpoints.LoadPlayerState(playerId);

            Assert.Equal(FieldMouse, relogin.ActiveActivityId);
        }

        /// <summary>
        /// A death sets the fielded character idle live; the row has to agree,
        /// or a relogin resumes a fight the player saw end.
        /// </summary>
        [Fact]
        public async Task AnIdleResetIsDurableToo()
        {
            const long playerId = 950_025_002L;
            var fielded = Guid.NewGuid();
            await SeedAsync(playerId, new CharacterRecord { Id = fielded, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, ActiveActivityId = FieldMouse });
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var live = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(FieldMouse, live.ActiveActivityId);
            live.ActiveActivityId = 0L;

            Assert.True(await checkpoints.FlushState(live));

            Assert.Equal(0L, await RowActivityAsync(fielded));
        }

        /// <summary>
        /// The Hall of Ancestors benches the fielded character idle and fields
        /// another, then the reload that follows flushes the STALE payload,
        /// which still names the benched character in slot 1 with its old
        /// fight. The flush must not write that fight back onto the bench.
        /// </summary>
        [Fact]
        public async Task AStaleFlushDoesNotUndoAHallSwap()
        {
            const long playerId = 950_025_003L;
            var benched = Guid.NewGuid();
            var newcomer = Guid.NewGuid();
            await SeedAsync(playerId,
                new CharacterRecord { Id = benched, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, ActiveActivityId = FieldMouse },
                new CharacterRecord { Id = newcomer, PlayerId = playerId, AgePhase = 1, SlotIndex = 1 });
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);
            var stale = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(benched, stale.Slot1_CharacterId);

            // What HallOfAncestorsEngine commits out-of-band.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var b = await db.CharacterRecords.FindAsync(benched);
                var n = await db.CharacterRecords.FindAsync(newcomer);
                b!.SlotIndex = 1;
                b.ActiveActivityId = 0L;
                n!.SlotIndex = 0;
                await db.SaveChangesAsync();
            }

            Assert.True(await checkpoints.FlushState(stale));

            Assert.Equal(0L, await RowActivityAsync(benched));
            Assert.Equal(0L, await RowActivityAsync(newcomer));
        }
    }
}

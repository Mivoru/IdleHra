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
        /// <summary>
        /// Slots 2 and 3 used to be skipped by the checkpoint: a death's reset
        /// and a task 85 order (fish when the larder runs dry, step down after
        /// a death) change Slot2Activity/Slot3Activity live only, so a relogin
        /// put that character back on the monster it was deployed to.
        /// </summary>
        [Fact]
        public async Task SlotTwoAndThreeActivitiesSurviveAFlushAndAFreshLoad()
        {
            const long playerId = 950_025_004L;
            const long OneEasier = 92L;
            const long FishingSpot = 3001L;
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var third = Guid.NewGuid();
            await SeedAsync(playerId,
                new CharacterRecord { Id = first, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, ActiveActivityId = FieldMouse },
                new CharacterRecord { Id = second, PlayerId = playerId, AgePhase = 1, SlotIndex = 1, ActiveActivityId = 93L },
                new CharacterRecord { Id = third, PlayerId = playerId, AgePhase = 1, SlotIndex = 2, ActiveActivityId = 94L });
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            var live = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(second, live.Slot2_CharacterId);
            Assert.Equal(third, live.Slot3_CharacterId);
            live.Slot2Activity.ActiveActivityId = OneEasier;   // stepped down after a death
            live.Slot3Activity.ActiveActivityId = FishingSpot; // sent fishing by an order

            Assert.True(await checkpoints.FlushState(live));
            var relogin = await checkpoints.LoadPlayerState(playerId);

            Assert.Equal(FieldMouse, relogin.ActiveActivityId);
            Assert.Equal(OneEasier, relogin.Slot2Activity.ActiveActivityId);
            Assert.Equal(FishingSpot, relogin.Slot3Activity.ActiveActivityId);
        }

        /// <summary>
        /// The same guard as slot 1, at rank 2: a stale payload naming a
        /// character that has since moved out of slot 2 writes nothing.
        /// </summary>
        [Fact]
        public async Task AStaleSlotTwoDoesNotWriteOntoAMovedCharacter()
        {
            const long playerId = 950_025_005L;
            var first = Guid.NewGuid();
            var moved = Guid.NewGuid();
            var newcomer = Guid.NewGuid();
            await SeedAsync(playerId,
                new CharacterRecord { Id = first, PlayerId = playerId, AgePhase = 1, SlotIndex = 0 },
                new CharacterRecord { Id = moved, PlayerId = playerId, AgePhase = 1, SlotIndex = 1, ActiveActivityId = FieldMouse },
                new CharacterRecord { Id = newcomer, PlayerId = playerId, AgePhase = 1, SlotIndex = 2 });
            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);
            var stale = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(moved, stale.Slot2_CharacterId);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var m = await db.CharacterRecords.FindAsync(moved);
                var n = await db.CharacterRecords.FindAsync(newcomer);
                m!.SlotIndex = 2;
                m.ActiveActivityId = 0L;
                n!.SlotIndex = 1;
                await db.SaveChangesAsync();
            }

            Assert.True(await checkpoints.FlushState(stale));

            Assert.Equal(0L, await RowActivityAsync(moved));
            Assert.Equal(0L, await RowActivityAsync(newcomer));
        }
    }
}

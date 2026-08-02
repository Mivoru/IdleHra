using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: dev fixture invariants, 2026-08-02.
    //
    // THE MAIN CHARACTER'S Id MUST EQUAL THE PLAYER'S PlayerGuid. That is a
    // real schema invariant, established by normal account provisioning and
    // relied on by EquipmentSlotEngine.ResolveCharacterForUpdateAsync, which
    // resolves Guid.Empty - what every equip and unequip command sends - by
    // looking up exactly that row. StateCheckpointManager hydrates the same
    // row into slot 1.
    //
    // DevFixtureSeeder used Guid.NewGuid() for all three slots, so the fixture
    // had no character matching its own PlayerGuid and EVERY equip and unequip
    // on it was rejected. Silently: that path logs nothing, and it reported no
    // result code either. The account that exists specifically for driving the
    // client by hand was the one account on which equipment did not work.
    //
    // These tests exist because the failure was invisible from both ends - the
    // client looked broken, the server said nothing - and cost a full
    // debugging session to trace.
    // Runs against its OWN database on the shared container. DevFixtureSeeder
    // keys off dev@folkidle.local, and DbSeeder assigns that same email to
    // PlayerLowId - so seeding into the shared database rewrites that player's
    // gold and level and breaks whichever market tax-bracket test runs next.
    // That is an order-dependent failure, which is strictly worse than an
    // outright one, so these tests take an isolated database instead.
    [Collection("Postgres collection")]
    public class DevFixtureInvariantTests : IAsyncLifetime
    {
        private readonly PostgresTestFixture _fixture;
        private string _databaseName = string.Empty;
        private DbContextOptions<FolkIdleDbContext> _options = null!;

        public DevFixtureInvariantTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        public async Task InitializeAsync()
        {
            _databaseName = $"devfixture_{Guid.NewGuid():N}";

            var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString);
            await using (var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            builder.Database = _databaseName;
            _options = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;

            await using var db = new FolkIdleDbContext(_options);
            await db.Database.MigrateAsync();
        }

        public async Task DisposeAsync()
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }

        private FolkIdleDbContext NewContext() => new(_options);

        [Fact]
        public async Task Seeder_GivesTheMainCharacterThePlayersGuid()
        {
            await using var db = NewContext();
            long playerId = await DevFixtureSeeder.SeedAsync(db);

            var player = await db.PlayerRecords.AsNoTracking().FirstAsync(p => p.Id == playerId);
            var characters = await db.CharacterRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .OrderBy(c => c.SlotIndex)
                .ToListAsync();

            Assert.Equal(3, characters.Count);
            Assert.Equal(player.PlayerGuid, characters[0].Id);

            // The other two must NOT collide with it, or two slots would
            // resolve to the same row.
            Assert.All(characters.Skip(1), c => Assert.NotEqual(player.PlayerGuid, c.Id));
            Assert.Equal(3, characters.Select(c => c.Id).Distinct().Count());
        }

        [Fact]
        public async Task Seeder_RepairsAFixtureSeededBeforeTheInvariantWasEnforced()
        {
            await using var db = NewContext();
            long playerId = await DevFixtureSeeder.SeedAsync(db);

            var player = await db.PlayerRecords.AsNoTracking().FirstAsync(p => p.Id == playerId);

            // Recreate the broken shape: a main character with a random id.
            var main = await db.CharacterRecords.FirstAsync(c => c.PlayerId == playerId && c.SlotIndex == 0);
            db.CharacterRecords.Remove(main);
            await db.SaveChangesAsync();
            db.CharacterRecords.Add(new CharacterRecord
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                Level = 40,
                AgePhase = 1,
                SlotIndex = 0,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // The seeder is documented as idempotent and re-runnable, so it
            // has to bring an already-broken fixture back into line rather
            // than leaving it permanently unable to equip anything.
            await DevFixtureSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();

            var repaired = await db.CharacterRecords.AsNoTracking()
                .FirstAsync(c => c.PlayerId == playerId && c.SlotIndex == 0);
            Assert.Equal(player.PlayerGuid, repaired.Id);
        }

        [Fact]
        public async Task Seeder_StaysIdempotentAcrossRepeatedRuns()
        {
            await using var db = NewContext();

            long first = await DevFixtureSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();
            long second = await DevFixtureSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();

            Assert.Equal(first, second);

            int characterCount = await db.CharacterRecords.CountAsync(c => c.PlayerId == first);
            Assert.Equal(3, characterCount);
        }
    }
}

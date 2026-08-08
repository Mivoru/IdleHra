using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
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

        // Modul: THE FIXTURE MUST BE ABLE TO BREED.
        //
        // Three separate absences each made the Breeding screen dead on the one
        // account that exists for driving the client by hand, and every one of
        // them fails silently: no Breeding Grounds (the engine rolls back), no
        // character_lineage_registry rows (the roster endpoint SKIPS a
        // character that has none, so the parent list came back empty and
        // looked like a loading state), and no sexes (every character defaulted
        // to male, and a pair needs one of each).
        [Fact]
        public async Task Seeder_CanActuallyBreed()
        {
            await using var db = NewContext();
            long playerId = await DevFixtureSeeder.SeedAsync(db);

            var characters = await db.CharacterRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .OrderBy(c => c.SlotIndex)
                .ToListAsync();

            // The hero gate for the standard pair.
            Assert.All(characters, c => Assert.True(c.Level >= 50, $"slot {c.SlotIndex} is level {c.Level}, below the breeding gate"));
            Assert.Contains(characters, c => !c.IsFemale);
            Assert.Contains(characters, c => c.IsFemale);

            var lineageIds = await db.CharacterLineages.AsNoTracking()
                .Where(l => characters.Select(c => c.Id).Contains(l.CharacterId))
                .Select(l => l.CharacterId)
                .ToListAsync();
            Assert.Equal(characters.Count, lineageIds.Count);

            int breedingLevel = await db.VillageInfrastructures.AsNoTracking()
                .Where(v => v.PlayerId == playerId
                         && v.BuildingId == Domain.Progression.VillageManagementEngine.BreedingGroundsBuildingId)
                .Select(v => v.CurrentLevel)
                .FirstOrDefaultAsync();
            Assert.True(breedingLevel > 0, "the fixture has no Breeding Grounds, so every pairing is refused");
        }

        /// <summary>
        /// SLOTS MOVE NOW. AssignCharacterSlot can swap a bred child into slot
        /// 0 and push the account's own character down the roster, and the
        /// repair above read "slot 0 is not the PlayerGuid character" as "the
        /// PlayerGuid character is missing" - then threw on the primary key
        /// re-inserting one that already existed. Re-seeding the fixture failed
        /// outright, on an account whose documented fix for everything is
        /// "re-run the seeder".
        /// </summary>
        [Fact]
        public async Task Seeder_SurvivesTheAccountCharacterHavingBeenSwappedOutOfSlotZero()
        {
            await using var db = NewContext();
            long playerId = await DevFixtureSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();

            var player = await db.PlayerRecords.AsNoTracking().FirstAsync(p => p.Id == playerId);

            // Exactly what a swap leaves behind: the account's own character
            // benched, somebody else at the front.
            var accountCharacter = await db.CharacterRecords.FirstAsync(c => c.Id == player.PlayerGuid);
            var other = await db.CharacterRecords.FirstAsync(c => c.PlayerId == playerId && c.Id != player.PlayerGuid);
            accountCharacter.SlotIndex = other.SlotIndex;
            other.SlotIndex = 0;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await DevFixtureSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();

            var front = await db.CharacterRecords.AsNoTracking()
                .FirstAsync(c => c.PlayerId == playerId && c.SlotIndex == 0);
            Assert.Equal(player.PlayerGuid, front.Id);

            // And nobody was duplicated or lost putting it back.
            var roster = await db.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync();
            Assert.Equal(roster.Count, roster.Select(c => c.Id).Distinct().Count());
            Assert.Equal(roster.Count, roster.Select(c => c.SlotIndex).Distinct().Count());
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

        /// <summary>
        /// THE FIXTURE MUST BE ABLE TO PLAY.
        ///
        /// It could not. Stocking every recipe material put 46 rows in
        /// CommodityRecords, and CountOccupiedBackpackSlotsAsync counts one
        /// backpack slot per ROW - so the fixture sat at 58 occupied against a
        /// capacity of 20. ProcessSubTick returns immediately when no space
        /// remains, so ChangeActivity was accepted, ActiveActivityId became 91,
        /// and CurrentMonsterId never left 0: the fixture could never fight or
        /// gather, and no amount of depositing could dig it out.
        ///
        /// The materials now go to VillageStashInstances, which the census does
        /// not count and which crafting still spends from. This test pins that,
        /// because "seed everything the recipes need" is a reasonable-sounding
        /// change to make again and it silently bricks the account.
        /// </summary>
        [Fact]
        public async Task Seeder_LeavesTheBackpackUnderCapacitySoTheFixtureCanActuallyPlay()
        {
            await using var db = NewContext();
            long playerId = await DevFixtureSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();

            int occupied = await CombatLootEngine.CountOccupiedBackpackSlotsAsync(db, playerId);

            // The default capacity, without any race-mastery bonus - the
            // fixture must fit in a plain backpack, not a lucky one.
            Assert.True(
                occupied < SimulationEngine.DefaultBackpackCapacity,
                $"fixture occupies {occupied} of {SimulationEngine.DefaultBackpackCapacity} backpack slots, " +
                "so ProcessSubTick will return before combat or gathering can run");

            // And it must still be able to craft, which is the whole reason the
            // materials are seeded at all. Stash + backpack is what a recipe
            // spends from.
            // Modul: asserted as what it MEANS - every distinct recipe input is
            // stocked - rather than as a row count. The count was "> 20", and
            // the recipe table now has exactly twenty distinct inputs (ten logs
            // and ten ores), so a correct fixture failed on an off-by-one in
            // the test rather than on anything about the fixture.
            var required = new System.Collections.Generic.HashSet<int>();
            foreach (var recipe in ContentRegistry.Recipes.ToArray())
            {
                if (recipe.Mat1Id > 0) required.Add(recipe.Mat1Id);
                if (recipe.Mat2Id > 0) required.Add(recipe.Mat2Id);
            }

            var stashed = (await db.VillageStashInstances
                .Where(s => s.PlayerId == playerId)
                .Select(s => s.ItemId)
                .ToListAsync()).ToHashSet();

            foreach (int materialId in required)
            {
                string baseId = ContentRegistry.GetItemBaseId(materialId);
                Assert.True(stashed.Contains(baseId), $"recipe material {baseId} is not stocked");
            }
        }
    }
}

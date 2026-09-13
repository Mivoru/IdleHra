using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE BREEDING AUDIT OF 2026-09-13, one test per defect it found
    /// in code that had passed its own suite:
    ///
    ///   - cousins bred as strangers (two inline copies of the relatedness
    ///     check stopped at siblings; the one that looked at grandparents was
    ///     called by nothing),
    ///   - the gold a pairing cost never reached the live session, so the header
    ///     kept the pre-breeding balance,
    ///   - the retired Czech names had to be replaced exactly once.
    /// </summary>
    [Collection("Postgres collection")]
    public class BreedingRoundOneTests
    {
        private readonly PostgresTestFixture _fixture;

        public BreedingRoundOneTests(PostgresTestFixture fixture) => _fixture = fixture;

        private static long HumanGenome()
        {
            var genome = new GeneticVector(0);
            genome.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };
            return genome.RawValue;
        }

        private static async Task SeedPlayerAsync(FolkIdleDbContext db, long playerId)
        {
            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
            db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 1 });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 10000L });
            await Task.CompletedTask;
        }

        [Fact]
        public async Task CousinsAreBredAsRelated()
        {
            const long playerId = 970013001L;
            Guid grandfather = Guid.NewGuid();
            Guid uncle = Guid.NewGuid();
            Guid aunt = Guid.NewGuid();
            Guid cousinMan = Guid.NewGuid();
            Guid cousinWoman = Guid.NewGuid();
            long genome = HumanGenome();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await SeedPlayerAsync(db, playerId);

                // The two parents share a father. Every lineage row needs its
                // character (a foreign key), so the elders are benched
                // characters too; their lineage rows are what the grandparent
                // lookup reads.
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = grandfather, PlayerId = playerId, AgePhase = 1, SlotIndex = 10, IsFemale = false },
                    new CharacterRecord { Id = uncle, PlayerId = playerId, AgePhase = 1, SlotIndex = 11, IsFemale = false },
                    new CharacterRecord { Id = aunt, PlayerId = playerId, AgePhase = 1, SlotIndex = 12, IsFemale = true });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = grandfather, GenerationIndex = 0, GeneticVector = genome },
                    new CharacterLineageRegistry { CharacterId = uncle, ParentPaternalId = grandfather, GenerationIndex = 1, GeneticVector = genome },
                    new CharacterLineageRegistry { CharacterId = aunt, ParentPaternalId = grandfather, GenerationIndex = 1, GeneticVector = genome },
                    new CharacterLineageRegistry { CharacterId = cousinMan, ParentPaternalId = uncle, GenerationIndex = 2, GeneticVector = genome },
                    new CharacterLineageRegistry { CharacterId = cousinWoman, ParentMaternalId = aunt, GenerationIndex = 2, GeneticVector = genome });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = cousinMan, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, IsFemale = false },
                    new CharacterRecord { Id = cousinWoman, PlayerId = playerId, AgePhase = 1, SlotIndex = 1, IsFemale = true });
                await db.SaveChangesAsync();
            }

            var engine = new BreedingEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            await engine.ExecuteBreedingAsync(playerId, cousinMan, cousinWoman);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var child = await verify.CharacterLineages.AsNoTracking()
                .SingleAsync(l => l.ParentPaternalId == cousinMan && l.ParentMaternalId == cousinWoman);
            Assert.True(child.IsInbred);
        }

        [Fact]
        public async Task ABirthTellsTheSessionWhatItCost()
        {
            const long playerId = 970013002L;
            Guid hero = Guid.NewGuid();
            long villagerId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await SeedPlayerAsync(db, playerId);
                db.CharacterRecords.Add(new CharacterRecord { Id = hero, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, IsFemale = false });
                var lineage = new CharacterLineageRegistry { CharacterId = hero, GenerationIndex = 2, GeneticVector = HumanGenome() };
                lineage.SetAptitudeVector(new[] { 4, 4, 4, 4 });
                db.CharacterLineages.Add(lineage);

                var newcomer = new VillageNewcomer { PlayerId = playerId, RaceId = RaceIds.Human, IsFemale = true };
                newcomer.SetAptitudeVector(new[] { 6, 6, 6, 6 });
                db.VillageNewcomers.Add(newcomer);
                await db.SaveChangesAsync();
                villagerId = newcomer.Id;
            }

            var registry = new PlayerSessionRegistry();
            var engine = new BreedingEngine(_fixture.ServiceProvider, registry);
            await engine.ExecuteHeroVillagerBreedingAsync(playerId, hero, villagerId);

            Assert.True(registry.BirthNotificationQueue.TryDequeue(out var birth));
            Assert.Equal(playerId, birth.PlayerId);
            Assert.Equal(BreedingEngine.CostFor(2), birth.GoldSpent);

            Assert.Contains(registry.CommandResultQueue,
                r => r.ResultCode == (byte)CommandResultCode.BreedingSucceeded);

            // And the row the notification describes really was debited by the
            // same amount - the tick moves the live balance, never the row again.
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var gold = await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            Assert.Equal(10000L - birth.GoldSpent, gold.Quantity);
        }

        [Fact]
        public async Task TheBackfillReplacesRetiredCzechNamesOnceAndNothingElse()
        {
            const long playerId = 970013003L;
            Guid czech = Guid.NewGuid();
            Guid unnamed = Guid.NewGuid();
            Guid custom = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = czech, PlayerId = playerId, Name = "Vojtěch", SlotIndex = 0, IsFemale = false },
                    new CharacterRecord { Id = unnamed, PlayerId = playerId, Name = string.Empty, SlotIndex = 1, IsFemale = true },
                    new CharacterRecord { Id = custom, PlayerId = playerId, Name = "Somebody Chose This", SlotIndex = 2, IsFemale = true });
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(await CharacterNameBackfill.RunAsync(db) >= 2);
            }

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var rows = await verify.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == playerId).ToDictionaryAsync(c => c.Id);
                Assert.Equal(FolkNameRegistry.For(czech, isFemale: false), rows[czech].Name);
                Assert.Equal(FolkNameRegistry.For(unnamed, isFemale: true), rows[unnamed].Name);
                Assert.Equal("Somebody Chose This", rows[custom].Name);
            }

            // The second run finds nothing of this player's to change.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await CharacterNameBackfill.RunAsync(db);
            }
            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var name = await verify.CharacterRecords.AsNoTracking().Where(c => c.Id == czech).Select(c => c.Name).SingleAsync();
                Assert.Equal(FolkNameRegistry.For(czech, isFemale: false), name);
            }
        }
    }
}

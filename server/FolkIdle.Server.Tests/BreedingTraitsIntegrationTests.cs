using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FolkIdle.Server.Domain.Progression;

namespace FolkIdle.Server.Tests
{
    [Collection("Postgres collection")]
    public class BreedingTraitsIntegrationTests
    {
        private readonly PostgresTestFixture _fixture;

        public BreedingTraitsIntegrationTests(PostgresTestFixture fixture) => _fixture = fixture;

        private static long HumanGenome()
        {
            var genome = new GeneticVector(0);
            genome.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };
            return genome.RawValue;
        }

        [Fact]
        public async Task TraitMasksRoundTrip()
        {
            const long playerId = 970013201L;
            var characterId = Guid.NewGuid();
            long newcomerId;
            long mask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.ThinBlood);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.Add(new CharacterRecord { Id = characterId, PlayerId = playerId, AgePhase = 1 });
                db.CharacterLineages.Add(new CharacterLineageRegistry { CharacterId = characterId, GeneticVector = HumanGenome(), TraitMask = mask });
                var newcomer = new VillageNewcomer { PlayerId = playerId, RaceId = RaceIds.Human, TraitMask = TraitRegistry.MaskOf(TraitRegistry.HawkEye) };
                db.VillageNewcomers.Add(newcomer);
                await db.SaveChangesAsync();
                newcomerId = newcomer.Id;
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(mask, (await verify.CharacterLineages.AsNoTracking().SingleAsync(l => l.CharacterId == characterId)).TraitMask);
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.HawkEye), (await verify.VillageNewcomers.AsNoTracking().SingleAsync(v => v.Id == newcomerId)).TraitMask);
        }

        [Fact]
        public async Task ABirthStoresATraitMaskDrawnFromItsParents()
        {
            const long playerId = 970013202L;
            var father = Guid.NewGuid();
            var mother = Guid.NewGuid();
            long fatherMask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.BloodOfKings);
            long motherMask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 1 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 10_000L });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = father, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, IsFemale = false },
                    new CharacterRecord { Id = mother, PlayerId = playerId, AgePhase = 1, SlotIndex = 1, IsFemale = true });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = father, GeneticVector = HumanGenome(), TraitMask = fatherMask },
                    new CharacterLineageRegistry { CharacterId = mother, GeneticVector = HumanGenome(), TraitMask = motherMask });
                await db.SaveChangesAsync();
            }

            var engine = new BreedingEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            await engine.ExecuteBreedingAsync(playerId, father, mother);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var child = await verify.CharacterLineages.AsNoTracking()
                .SingleAsync(l => l.ParentPaternalId == father && l.ParentMaternalId == mother);

            Assert.Equal(0L, child.TraitMask & ~TraitRegistry.KnownBitsMask);
            Assert.InRange(TraitRegistry.CountOf(child.TraitMask), 0, TraitRegistry.MaxTraitsPerCharacter);
            // Anything outside the parents' traits can only have come from the
            // mutation or epic roll, and those add non-flaw traits only.
            foreach (int bit in TraitRegistry.BitsOf(child.TraitMask & ~(fatherMask | motherMask)))
            {
                Assert.False(TraitRegistry.IsFlaw(bit));
            }
        }

        [Fact]
        public async Task ANewcomerArrivesWithAtMostOneTrait()
        {
            const long playerId = 970013203L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000L });
                await db.SaveChangesAsync();
            }

            for (int i = 0; i < 5; i++)
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                var player = await db.PlayerRecords.SingleAsync(p => p.Id == playerId);
                var (refusal, _) = await VillageArrivalEngine.RecruitAsync(db, player, innLevel: 10, nowEpoch: 1_800_000_000L + i);
                Assert.Null(refusal);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var masks = await verify.VillageNewcomers.AsNoTracking().Where(v => v.PlayerId == playerId).Select(v => v.TraitMask).ToListAsync();
            Assert.Equal(5, masks.Count);
            Assert.All(masks, m => Assert.InRange(TraitRegistry.CountOf(m), 0, 1));
        }
    }
}

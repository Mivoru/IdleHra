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
    }
}

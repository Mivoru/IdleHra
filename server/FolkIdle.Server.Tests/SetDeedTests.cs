using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: "Wear two pieces of one set" COULD NOT BE DONE (owner,
    /// 2026-09-28). The deed counted equal EquipmentInstance.SetId values, a
    /// column nothing has ever written - 0 of 945 items in production. It
    /// counts ArmourSetRegistry families now, on the character whose Id is
    /// PlayerGuid. Through the real DeedProgressSource, on a real database.
    /// </summary>
    [Collection("Postgres collection")]
    public class SetDeedTests
    {
        private readonly PostgresTestFixture _fixture;

        public SetDeedTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        [Fact]
        public async Task TwoPiecesOfOneFamilyOnTheMainCharacterCount_WithNoSetIdAnywhere()
        {
            const long playerId = 950_052_001L;
            var mainId = Guid.NewGuid();

            // The registry decides what a family is; the test only asks it.
            const string hood = "eq_linen_hood_helmet_armor_slot_base";
            const string shroud = "eq_linen_shroud_chest_armor_slot_base";
            Assert.Equal(ArmourSetRegistry.FamilyOf(hood), ArmourSetRegistry.FamilyOf(shroud));
            Assert.NotEqual(string.Empty, ArmourSetRegistry.FamilyOf(hood));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var helmet = new EquipmentInstance { PlayerId = playerId, BaseItemId = hood, QualityTier = 1, AffixPayload = "{}" };
                var chest = new EquipmentInstance { PlayerId = playerId, BaseItemId = shroud, QualityTier = 1, AffixPayload = "{}" };
                db.EquipmentInstances.AddRange(helmet, chest);
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = mainId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();

                // The main character sits in slot 1 and an unequipped ancestor in
                // slot 0, so "lowest SlotIndex" would read the wrong person.
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = Guid.NewGuid(), PlayerId = playerId, AgePhase = 1, SlotIndex = 0 },
                    new CharacterRecord
                    {
                        Id = mainId, PlayerId = playerId, AgePhase = 1, SlotIndex = 1,
                        EquippedHelmetId = helmet.Id, EquippedChestId = chest.Id,
                    });
                await db.SaveChangesAsync();
                Assert.Equal(0, helmet.SetId);
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var context = await DeedProgressSource.LoadAsync(read, playerId);
            Assert.Equal(2, context.LargestActiveSetBonus);
        }
    }
}

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

        /// <summary>
        /// Modul: "Gather 100 wood" counted the legacy "wood" row, which
        /// gathering never wrote and live village production stopped writing on
        /// 2026-09-30 - so it could not advance. It counts every _log now, and
        /// no other material.
        /// </summary>
        [Fact]
        public async Task WoodDeedCountsEveryCatalogueLog_AndNothingElse()
        {
            const long playerId = 950_052_002L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = playerId, ItemId = "birch_log", Quantity = 40 },
                    new CommodityRecord { PlayerId = playerId, ItemId = "willow_log", Quantity = 25 },
                    new CommodityRecord { PlayerId = playerId, ItemId = "copper_ore", Quantity = 500 },
                    new CommodityRecord { PlayerId = playerId, ItemId = "wood", Quantity = 500 });
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var context = await DeedProgressSource.LoadAsync(read, playerId);
            Assert.Equal(65, context.WoodStock);
        }

        /// <summary>
        /// Modul: "Wear a weapon" read 0 / 1 for the owner (2026-10-09) with a
        /// weapon on all three fielded characters, because the PlayerGuid
        /// character had moved to slot 14 - the pool - and the deed asked only
        /// it. The fielded roster answers now; the pool does not.
        /// </summary>
        [Fact]
        public async Task WeaponAndSetDeeds_ReadTheFieldedRoster_NotAMainCharacterInThePool()
        {
            const long playerId = 950_052_003L;
            var mainId = Guid.NewGuid();
            const string hood = "eq_linen_hood_helmet_armor_slot_base";
            const string shroud = "eq_linen_shroud_chest_armor_slot_base";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var weapon = new EquipmentInstance { PlayerId = playerId, BaseItemId = "eq_test_weapon", QualityTier = 1, AffixPayload = "{}" };
                var helmet = new EquipmentInstance { PlayerId = playerId, BaseItemId = hood, QualityTier = 1, AffixPayload = "{}" };
                var chest = new EquipmentInstance { PlayerId = playerId, BaseItemId = shroud, QualityTier = 1, AffixPayload = "{}" };
                db.EquipmentInstances.AddRange(weapon, helmet, chest);
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = mainId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();

                db.CharacterRecords.AddRange(
                    // The main character, undressed, in the pool.
                    new CharacterRecord { Id = mainId, PlayerId = playerId, AgePhase = 1, SlotIndex = 14 },
                    new CharacterRecord { Id = Guid.NewGuid(), PlayerId = playerId, AgePhase = 1, SlotIndex = 0, EquippedWeaponId = weapon.Id },
                    new CharacterRecord
                    {
                        Id = Guid.NewGuid(), PlayerId = playerId, AgePhase = 1, SlotIndex = 2,
                        EquippedHelmetId = helmet.Id, EquippedChestId = chest.Id,
                    });
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var context = await DeedProgressSource.LoadAsync(read, playerId);
            Assert.True(context.HasWeaponEquipped);
            Assert.Equal(2, context.LargestActiveSetBonus);
        }

        /// <summary>
        /// A weapon worn only by someone in the pool is not a weapon worn.
        /// </summary>
        [Fact]
        public async Task WeaponDeed_IgnoresAWeaponInThePool()
        {
            const long playerId = 950_052_004L;
            var mainId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var weapon = new EquipmentInstance { PlayerId = playerId, BaseItemId = "eq_test_weapon", QualityTier = 1, AffixPayload = "{}" };
                db.EquipmentInstances.Add(weapon);
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = mainId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();

                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = mainId, PlayerId = playerId, AgePhase = 1, SlotIndex = 0 },
                    new CharacterRecord { Id = Guid.NewGuid(), PlayerId = playerId, AgePhase = 1, SlotIndex = 7, EquippedWeaponId = weapon.Id });
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var context = await DeedProgressSource.LoadAsync(read, playerId);
            Assert.False(context.HasWeaponEquipped);
        }
    }
}

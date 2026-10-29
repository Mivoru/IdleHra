using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Gear belongs to the three playable SLOTS, not to whoever once stood in
    /// them (owner, 2026-10-20).
    ///
    /// A player fielded a new character and found the axe, pickaxe and rod
    /// "taken" - listed as worn by another character, Move disabled, nothing on
    /// anyone they could see. The character who had worn them was on the
    /// bench, still wearing them. Measured live: ten benched characters across
    /// three accounts held full tool sets and armour.
    /// </summary>
    [Collection("Postgres collection")]
    public class SlotGearTests
    {
        private readonly PostgresTestFixture _fixture;

        public SlotGearTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private static EquipmentInstance Piece(long playerId, string baseId) =>
            new() { PlayerId = playerId, BaseItemId = baseId, QualityTier = 3, AffixPayload = "{}" };

        [Fact]
        public async Task FieldingABenchedCharacterHandsItTheSlotsGear_AndTheBenchWearsNothing()
        {
            const long playerId = 970_201_001L;
            Guid founder = Guid.NewGuid();
            Guid child = Guid.NewGuid();
            long swordId, axeId, ringId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var sword = Piece(playerId, "test_sword");
                var axe = Piece(playerId, "test_axe");
                var ring = Piece(playerId, "test_ring");
                db.EquipmentInstances.AddRange(sword, axe, ring);
                await db.SaveChangesAsync();
                (swordId, axeId, ringId) = (sword.Id, axe.Id, ring.Id);

                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = founder, AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = founder, PlayerId = playerId, AgePhase = 1, SlotIndex = 0, EquippedWeaponId = swordId, EquippedAxeId = axeId },
                    // A legacy benched wearer: what the bench held before this fix.
                    new CharacterRecord { Id = child, PlayerId = playerId, AgePhase = 1, SlotIndex = 7, EquippedRingId = ringId });
                await db.SaveChangesAsync();
            }

            var hall = new HallOfAncestorsEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await hall.AssignSlotAsync(playerId, child, 0);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var fielded = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == child);
                var benched = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == founder);

                Assert.Equal(0, fielded.SlotIndex);
                Assert.Equal(swordId, fielded.EquippedWeaponId);
                // Slot 8 - the list that used to stop at eight.
                Assert.Equal(axeId, fielded.EquippedAxeId);
                // The bench gear the child came with goes back to the chest.
                Assert.Null(fielded.EquippedRingId);

                Assert.Equal(7, benched.SlotIndex);
                Assert.Null(benched.EquippedWeaponId);
                Assert.Null(benched.EquippedAxeId);
                Assert.Null(benched.EquippedRingId);

                // Nothing deleted: every piece is still the player's.
                Assert.Equal(3, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
            }
        }

        [Fact]
        public async Task PetsFollowTheSlotToo_AndABenchedOneRests()
        {
            const long playerId = 970_201_004L;
            Guid founder = Guid.NewGuid();
            Guid child = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = founder, AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = founder, PlayerId = playerId, AgePhase = 1, SlotIndex = 0 },
                    new CharacterRecord { Id = child, PlayerId = playerId, AgePhase = 1, SlotIndex = 7 });
                db.PlayerPets.AddRange(
                    new PlayerPet { PlayerId = playerId, PetId = "test_pet_a", CharacterId = founder, AcquiredAtUtc = DateTime.UtcNow },
                    new PlayerPet { PlayerId = playerId, PetId = "test_pet_b", CharacterId = child, AcquiredAtUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            await new HallOfAncestorsEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry).AssignSlotAsync(playerId, child, 0);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var pets = await verify.PlayerPets.AsNoTracking().Where(p => p.PlayerId == playerId).ToDictionaryAsync(p => p.PetId);
            // Slot 1's pet now follows whoever stands in slot 1...
            Assert.Equal(child, pets["test_pet_a"].CharacterId);
            // ...and the one that came off the bench with the child rests.
            Assert.Null(pets["test_pet_b"].CharacterId);
        }

        [Fact]
        public async Task TheLoginSweepFreesWhatABenchedCharacterStillWears()
        {
            const long playerId = 970_201_002L;
            Guid fieldedId = Guid.NewGuid();
            Guid benchedId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var helm = Piece(playerId, "test_helm");
                var rod = Piece(playerId, "test_rod");
                var pick = Piece(playerId, "test_pick");
                db.EquipmentInstances.AddRange(helm, rod, pick);
                await db.SaveChangesAsync();

                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = benchedId, AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = fieldedId, PlayerId = playerId, SlotIndex = 0, EquippedHelmetId = helm.Id },
                    new CharacterRecord { Id = benchedId, PlayerId = playerId, SlotIndex = 3, EquippedRodId = rod.Id, EquippedPickaxeId = pick.Id });
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(2, await EquipmentSlotEngine.ClearDanglingEquipReferencesAsync(db, playerId));
            }

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var fielded = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == fieldedId);
                var benched = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == benchedId);
                Assert.NotNull(fielded.EquippedHelmetId);
                Assert.Null(benched.EquippedRodId);
                Assert.Null(benched.EquippedPickaxeId);

                // And an empty-character equip dresses slot 1, not the benched
                // founder (PlayerGuid) - the Chest's Wear sends no character.
                Assert.Equal(fieldedId, await EquipmentSlotEngine.FieldedCharacterIdAsync(verify, playerId, 0));
            }
        }

        [Fact]
        public async Task TheSweepClearsADanglingToolPointer()
        {
            // The sweep walked eight slots, so a tool whose row was gone kept
            // its pointer.
            const long playerId = 970_201_003L;
            Guid id = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = id, AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.Add(new CharacterRecord { Id = id, PlayerId = playerId, SlotIndex = 0, EquippedAxeId = 987_654_321L });
                await db.SaveChangesAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(1, await EquipmentSlotEngine.ClearDanglingEquipReferencesAsync(db, playerId));
            }
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null((await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == id)).EquippedAxeId);
        }

        [Fact]
        public void EachSlotGathersWithItsOwnTools()
        {
            // The tool tiers were one account-wide set on the payload, so the
            // woodcutter in slot 2 cut with slot 1's axe. They ride the
            // register now, like the armour.
            var payload = new TickStatePayload { AxeToolTier = 2, ToolAxeSpeedPct = 5 };
            payload.Slot2Activity.AxeToolTier = 9;
            payload.Slot2Activity.ToolAxeSpeedPct = 40;

            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            Assert.Equal(9, payload.AxeToolTier);
            Assert.Equal(40, payload.ToolAxeSpeedPct);
            Assert.Equal(9, SimulationEngine.GatherSpeedTermsFor(ref payload, 0).ToolTier);

            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 1);
            Assert.Equal(2, payload.AxeToolTier);
            Assert.Equal(9, payload.Slot2Activity.AxeToolTier);
        }
    }
}

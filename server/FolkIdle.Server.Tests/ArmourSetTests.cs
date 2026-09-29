using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Which armour set a piece belongs to.
    ///
    /// The catalogue has no set field, so membership is derived from the naming
    /// convention - which means the convention has to be verified rather than
    /// trusted. These assert the OUTCOME (two families of five at every tier,
    /// ten in all) instead of the rule that produces it, so an item renamed in
    /// items.json fails here rather than quietly landing in a family of one.
    /// </summary>
    public class ArmourSetTests
    {
        private readonly ITestOutputHelper _output;

        public ArmourSetTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EveryTierAuthorsTwoSetsOfFive(int regionTier)
        {
            var families = ArmourSetRegistry.FamiliesAt(regionTier);
            Assert.Equal(ArmourSetRegistry.SetsPerTier, families.Count);

            foreach (string family in families)
            {
                var slots = ContentRegistry.ItemDefinitions.ToArray()
                    .Where(i => i.RegionTier == regionTier)
                    .Select(i => ContentRegistry.GetItemBaseId(i.Id))
                    .Where(b => ArmourSetRegistry.FamilyOf(b) == family)
                    .Select(EquipmentSlotEngine.ResolveSlotIndex)
                    .ToList();

                _output.WriteLine($"tier {regionTier} {family}: {slots.Count} pieces");

                // Five pieces, one per armour slot - no duplicates, no gaps.
                Assert.Equal(5, slots.Count);
                Assert.Equal(5, slots.Distinct().Count());
                Assert.DoesNotContain(EquipmentSlotEngine.SlotWeapon, slots);
            }
        }

        /// <summary>
        /// Task 63: every tier has exactly one OFFENSIVE (odd id) and one
        /// DEFENSIVE (even id) set, numbered (tier - 1) * 2 + 1 / + 2, and each
        /// of the ten ids maps back to its family. SetBonusEngine pays by that
        /// parity, so a family missing from the offensive list would silently
        /// turn a light set into a second heavy one.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EveryTierHasOneOffensiveAndOneDefensiveSet(int regionTier)
        {
            var families = ArmourSetRegistry.FamiliesAt(regionTier);
            Assert.Single(families, ArmourSetRegistry.IsOffensiveFamily);

            foreach (string family in families)
            {
                string piece = ContentRegistry.ItemDefinitions.ToArray()
                    .Where(i => i.RegionTier == regionTier)
                    .Select(i => ContentRegistry.GetItemBaseId(i.Id))
                    .First(b => ArmourSetRegistry.FamilyOf(b) == family);

                int setId = ArmourSetRegistry.SetIdOf(piece);
                int expected = (regionTier - 1) * 2 + (ArmourSetRegistry.IsOffensiveFamily(family) ? 1 : 2);
                Assert.Equal(expected, setId);
                Assert.Equal(ArmourSetRegistry.IsOffensiveFamily(family), SetBonusEngine.IsOffensiveSet(setId));
                Assert.Equal(family, ArmourSetRegistry.FamilyOfSetId(setId));
            }
        }

        [Fact]
        public void NonArmourHasSetIdZero()
        {
            Assert.Equal(0, ArmourSetRegistry.SetIdOf("bronze_dagger_melee_weapon_slot_base"));
            Assert.Equal(0, ArmourSetRegistry.SetIdOf(""));
            Assert.Equal(SetBonusEngine.EternalDreadnoughtSetId, ArmourSetRegistry.SetIdOf("eq_dreadnought_helm_helmet_armor_slot_base"));
            Assert.Equal(SetBonusEngine.LinenSetId, ArmourSetRegistry.SetIdOf("eq_linen_hood_helmet_armor_slot_base"));
        }

        /// <summary>
        /// Every one of the ten sets pays: five pieces reach the top tier and
        /// its effect, through StatsCalculator - the path the live tick, the
        /// relogin, the parked slot and the offline catch-up all share.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        public void AFullSetOfEveryFamilyPaysThroughStatsCalculator(int setId)
        {
            int piece = EquippedSetIds.Pack(setId, SetBonusEngine.ReferenceQualityTier);
            var worn = new EquippedSetIds { Helmet = piece, Chest = piece, Gloves = piece, Boots = piece, Leggings = piece };

            var bare = StatsCalculator.Calculate(50, 50, 50, 10);
            var dressed = StatsCalculator.Calculate(50, 50, 50, 10, equippedSetIds: worn);

            if (SetBonusEngine.IsOffensiveSet(setId))
            {
                Assert.True(dressed.SetFireDamageMultiplierPct > 0f);
                Assert.True(dressed.SetBurnApplicationActive);
            }
            else
            {
                Assert.True(dressed.SetThornsReflectionActive);
                Assert.True(dressed.SetDamageCapActive);
                Assert.True(dressed.FlatPhysicalArmor >= bare.FlatPhysicalArmor);
            }

            var described = SetBonusEngine.DescribeActive(worn);
            Assert.Single(described);
            Assert.Equal(5, described[0].Pieces);
            Assert.Equal(3, described[0].Tier);
        }

        /// <summary>
        /// THE ONE NAME THAT DOES NOT FOLLOW THE CONVENTION. Tier 5's dread
        /// helmet is authored `eq_dreadnought_helm_...` while its other four
        /// pieces are `eq_dread_...`, so a first-token rule would file it as a
        /// set of one and leave `dread` a set of four. Named explicitly because
        /// it is the case the merge exists for, and a future rename that broke
        /// it would otherwise only surface as an odd drop table.
        /// </summary>
        [Fact]
        public void TheDreadnoughtHelmBelongsToTheDreadSet()
        {
            Assert.Equal("dread", ArmourSetRegistry.FamilyOf("eq_dreadnought_helm_helmet_armor_slot_base"));
            Assert.Equal("dread", ArmourSetRegistry.FamilyOf("eq_dread_carapace_chest_armor_slot_base"));
            Assert.Equal("doom", ArmourSetRegistry.FamilyOf("eq_doom_crown_helmet_armor_slot_base"));
        }

        /// <summary>
        /// Anything that is not authored armour has no family, and must say so
        /// rather than guessing - the drop dealer uses an empty family to mean
        /// "leave this alone", so a weapon that claimed one would be sorted
        /// into a set rotation it does not belong in.
        /// </summary>
        [Fact]
        public void WeaponsAmuletsAndRingsHaveNoSet()
        {
            Assert.Equal(string.Empty, ArmourSetRegistry.FamilyOf("eq_steel_claymore_melee_weapon_slot_base"));
            Assert.Equal(string.Empty, ArmourSetRegistry.FamilyOf("eq_linen_pendant_amulet_slot_base"));
            Assert.Equal(string.Empty, ArmourSetRegistry.FamilyOf(string.Empty));
            Assert.Equal(string.Empty, ArmourSetRegistry.FamilyOf("not_an_item"));
        }
    }
}

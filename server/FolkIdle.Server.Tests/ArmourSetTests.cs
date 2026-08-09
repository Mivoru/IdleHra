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

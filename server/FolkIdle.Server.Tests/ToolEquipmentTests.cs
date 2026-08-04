using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Tools are gear: worn in their own slots, rolled with rarity and affixes,
    /// and worth something because of what they rolled.
    ///
    /// They used to be stackable materials in the chest. A stack has no room
    /// for a rarity or an affix payload, so every axe of a given wood was worth
    /// exactly the same as every other one, and the only thing that could ever
    /// vary was which wood you had.
    /// </summary>
    public class ToolEquipmentTests
    {
        public ToolEquipmentTests()
        {
            ContentRegistry.Initialize();
        }

        [Theory]
        [InlineData("birch_axe_tool", EquipmentSlotEngine.SlotAxe)]
        [InlineData("voidbark_axe_tool", EquipmentSlotEngine.SlotAxe)]
        [InlineData("willow_pickaxe_tool", EquipmentSlotEngine.SlotPickaxe)]
        [InlineData("ebon_fishing_rod_tool", EquipmentSlotEngine.SlotRod)]
        public void EveryToolHasItsOwnSlot(string baseId, int expectedSlot)
        {
            Assert.Equal(expectedSlot, EquipmentSlotEngine.ResolveSlotIndex(baseId));
        }

        [Fact]
        public void AToolRollsGatheringAffixesAndNothingElse()
        {
            var rolled = new Dictionary<string, int>();
            AffixRegistry.RollAffixes("frostpine_pickaxe_tool", regionTier: 4, itemRarityTier: 8,
                affixCount: 5, destination: rolled);

            Assert.NotEmpty(rolled);
            foreach (var key in rolled.Keys)
            {
                string id = AffixRegistry.StripStackSuffix(key);
                Assert.True(
                    id == ToolLoadoutResolver.GatherSpeedAffix
                        || id == ToolLoadoutResolver.GatherYieldAffix
                        || id == ToolLoadoutResolver.RareFindAffix,
                    $"a tool rolled {id}, which is not a gathering affix");
            }
        }

        [Fact]
        public void ASwordDoesNotRollGatheringAffixes()
        {
            // The other half of the same rule: gathering bonuses must not leak
            // onto combat gear, which is what a mask that defaulted to "all"
            // would do.
            var rolled = new Dictionary<string, int>();
            AffixRegistry.RollAffixes("eq_steel_claymore_melee_weapon_slot_base", regionTier: 1,
                itemRarityTier: 5, affixCount: 5, destination: rolled);

            foreach (var key in rolled.Keys)
            {
                string id = AffixRegistry.StripStackSuffix(key);
                Assert.NotEqual(ToolLoadoutResolver.GatherSpeedAffix, id);
                Assert.NotEqual(ToolLoadoutResolver.GatherYieldAffix, id);
                Assert.NotEqual(ToolLoadoutResolver.RareFindAffix, id);
            }
        }

        [Fact]
        public void TheLoadoutReadsTierAndAffixesOffTheEquippedTools()
        {
            var axe = new EquipmentInstance
            {
                Id = 1,
                BaseItemId = "voidbark_axe_tool",
                QualityTier = 13,
                // Stacked and rarity-tagged, because that is what a real
                // payload looks like - a reader that forgets to strip either
                // suffix scores every one of these as zero.
                AffixPayload = "{\"gather_speed_pct@4\":30,\"gather_speed_pct#2@2\":12,\"gather_yield_pct@5\":45}",
            };
            var rod = new EquipmentInstance
            {
                Id = 2,
                BaseItemId = "birch_fishing_rod_tool",
                QualityTier = 1,
                AffixPayload = "{\"gather_rare_find_pct@3\":25}",
            };

            var character = new CharacterRecord { EquippedAxeId = 1, EquippedRodId = 2 };
            var rows = new Dictionary<long, EquipmentInstance> { { 1, axe }, { 2, rod } };

            var loadout = ToolLoadoutResolver.Resolve(character, rows);

            Assert.Equal(10, loadout.AxeTier);
            Assert.Equal(1, loadout.RodTier);
            Assert.Equal(0, loadout.PickaxeTier);
            Assert.Equal(42, loadout.GatherSpeedPct);
            Assert.Equal(45, loadout.GatherYieldPct);
            Assert.Equal(25, loadout.RareFindPct);
        }

        [Fact]
        public void AnEmptyLoadoutIsZeroRatherThanAThrow()
        {
            Assert.Equal(0, ToolLoadoutResolver.Resolve(null, new Dictionary<long, EquipmentInstance>()).AxeTier);
            Assert.Equal(0, ToolLoadoutResolver.Resolve(new CharacterRecord(), new Dictionary<long, EquipmentInstance>()).GatherSpeedPct);
        }

        [Fact]
        public void EveryToolRecipeProducesEquipmentRatherThanAStack()
        {
            // ProfessionType 3 is what CraftingEngine routes to an
            // EquipmentInstance. Anything else and the craft lands in
            // CommodityRecords as a stack, with no rarity and no affixes -
            // which is exactly the state this whole pass exists to leave.
            int toolRecipes = 0;
            foreach (var recipe in ContentRegistry.Recipes.ToArray())
            {
                string baseId = ContentRegistry.GetItemBaseId(recipe.ResultItemId);
                if (!baseId.EndsWith("_tool", System.StringComparison.Ordinal)) continue;

                toolRecipes++;
                Assert.Equal(3, recipe.ProfessionType);
            }

            Assert.Equal(30, toolRecipes);
        }
    }
}

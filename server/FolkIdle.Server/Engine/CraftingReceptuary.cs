using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public struct CraftingRecipe
    {
        public int RecipeId;
        public int MaterialId;
        public int MaterialCost;
        public string ResultBaseItemId;
        public int TierIndex;
        public int ProfessionType;
    }

    public static class CraftingReceptuary
    {
        // Modul: repointed onto canonical items. These three produced
        // copper_greatsword / iron_breastplate / transcendent_cuirass, all
        // legacy pieces removed with the other 106 - a recipe whose output does
        // not exist crafts an item the player cannot wear, name or sell.
        //
        // They are a SECOND crafting system beside ContentRegistry's 104-recipe
        // tree, reachable through CraftingEngine and the command validator.
        // Repointed rather than deleted because deleting them is a wider change
        // than this pass is about; the duplication itself is worth resolving
        // separately.
        private static readonly Dictionary<int, CraftingRecipe> _recipes = new Dictionary<int, CraftingRecipe>
        {
            { 1, new CraftingRecipe { RecipeId = 1, MaterialId = 1, MaterialCost = 10, ResultBaseItemId = "eq_steel_claymore_melee_weapon_slot_base", TierIndex = 1, ProfessionType = 1 } },
            { 2, new CraftingRecipe { RecipeId = 2, MaterialId = 3, MaterialCost = 25, ResultBaseItemId = "eq_obsidian_plate_chest_armor_slot_base", TierIndex = 3, ProfessionType = 1 } },
            { 3, new CraftingRecipe { RecipeId = 3, MaterialId = 75, MaterialCost = 100, ResultBaseItemId = "eq_dread_carapace_chest_armor_slot_base", TierIndex = 10, ProfessionType = 1 } }
        };

        public static bool TryGetRecipe(int recipeId, out CraftingRecipe recipe)
        {
            return _recipes.TryGetValue(recipeId, out recipe);
        }

        public static IEnumerable<CraftingRecipe> AllRecipes => _recipes.Values;
    }
}

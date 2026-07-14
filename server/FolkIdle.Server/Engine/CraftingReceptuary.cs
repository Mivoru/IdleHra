using System.Collections.Generic;

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
        private static readonly Dictionary<int, CraftingRecipe> _recipes = new Dictionary<int, CraftingRecipe>
        {
            { 1, new CraftingRecipe { RecipeId = 1, MaterialId = 1, MaterialCost = 10, ResultBaseItemId = "copper_greatsword_melee_weapon_slot_base", TierIndex = 1, ProfessionType = 1 } },
            { 2, new CraftingRecipe { RecipeId = 2, MaterialId = 3, MaterialCost = 25, ResultBaseItemId = "iron_breastplate_chest_armor_slot_base", TierIndex = 3, ProfessionType = 1 } },
            { 3, new CraftingRecipe { RecipeId = 3, MaterialId = 75, MaterialCost = 100, ResultBaseItemId = "transcendent_cuirass_chest_armor_slot_base", TierIndex = 10, ProfessionType = 1 } }
        };

        public static bool TryGetRecipe(int recipeId, out CraftingRecipe recipe)
        {
            return _recipes.TryGetValue(recipeId, out recipe);
        }

        public static IEnumerable<CraftingRecipe> AllRecipes => _recipes.Values;
    }
}

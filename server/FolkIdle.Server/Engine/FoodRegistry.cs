using System;

namespace FolkIdle.Server.Engine
{
    // Modul: larder. The single authority on what counts as auto-eat food and
    // how much each tier heals, per GDD Module "Cooking (Sustain & Auto-Eat
    // Economy)" section 3.2, whose ten recipes food_t01..food_t10 are exactly
    // ContentRegistry's ten ProfessionType == 4 recipes producing items
    // 194..203 (cooked_pond_minnow_t1_food .. cooked_astral_whale_t10_food).
    //
    // This file exists because two things were wrong before it:
    //
    // 1. AlchemyCompendium.IsValidConsumable classified food by the BaseId
    //    marker "_food_consumable". None of the ten real foods carry that
    //    marker - they end in "_food" - so every one of them was an invalid
    //    consumable. Since ValidateConsumableRequest failure calls
    //    TerminateSessionForSecurity, eating any actual crafted food would
    //    have force-disconnected the player. (The four items that DO carry
    //    "_food_consumable" - roasted_perch, viper_stew, bear_stew,
    //    yeti_platter, ids 372-375 - are not produced by any recipe or drop,
    //    so they were the only foods the game accepted and the only foods
    //    nobody could obtain.) Both families are honoured here.
    //
    // 2. SimulationEngine's auto-eat step scored every occupied slot at a
    //    hardcoded 50000 milli-HP. Its "pick the highest-healing food" logic
    //    was therefore a tie on every comparison, so it always drained slot 1
    //    first, and a tier-10 Astral Ambrosia Roast (82000 HP per the GDD)
    //    restored the same 50 HP as a tier-1 minnow.
    public static class FoodRegistry
    {
        // The GDD's flat HP recovery per tier, in the same milli-HP units the
        // combat simulation uses for PlayerHp everywhere else (the engine
        // works in thousandths, hence PlayerHp / 1000 on the outbound packet).
        private static readonly int[] _healPayoutFlatHp =
        {
            40,      // food_t01 Seared Minnow Platter
            120,     // food_t02 Bog Nightshade Stew
            310,     // food_t03 Salted Mud Carp
            750,     // food_t04 Mountain Pike Roast
            1720,    // food_t05 Steppe Salmon Bake
            3840,    // food_t06 Maple Glazed Cod
            8450,    // food_t07 Deep Mire Eel Broth
            18200,   // food_t08 Karst Catfish Gumbo
            38900,   // food_t09 Glacial Shark Steak
            82000    // food_t10 Astral Ambrosia Roast
        };

        // The contiguous id block ContentRegistry's cooking recipes produce.
        public const int FirstCookedFoodItemId = 194;
        public const int LastCookedFoodItemId = 203;

        public static int TierCount => _healPayoutFlatHp.Length;

        // True for anything the larder will accept. Deliberately checks the id
        // block first (a pure integer compare, so the hot auto-eat path never
        // touches a string) and only falls back to the BaseId marker for the
        // legacy "_food_consumable" family.
        public static bool IsFood(int itemId)
        {
            if (itemId >= FirstCookedFoodItemId && itemId <= LastCookedFoodItemId)
            {
                return true;
            }

            if (itemId <= 0 || itemId > ContentRegistry.ItemDefinitions.Length)
            {
                return false;
            }

            // Modul: raw fish is food HERE TOO.
            //
            // GetHealMilliHp below learned this and IsFood did not, which is
            // worse than neither knowing: the larder asks IsFood, so stocking a
            // Sunlit Perch was refused outright while the eating code was
            // perfectly ready to consume one. Two predicates for one question,
            // and only one of them updated.
            if (ContentRegistry.IsRawFish(itemId))
            {
                return true;
            }

            return ContentRegistry.GetItemBaseId(itemId).Contains("_food");
        }

        // Milli-HP restored by one unit of this food. Zero for a non-food, so
        // the auto-eat comparison can score an empty or bogus slot at 0 and
        // never pick it.
        //
        // Zero allocation and no string work for the cooked block: the tier is
        // the id's offset within it.
        // Raw fish heals this share of what the same-tier cooked dish would.
        // Cooking is not in the design list, so this is not a penalty pushing
        // players towards a profession that does not exist - it is simply what
        // a fish is worth.
        private const int RawFishHealPercent = 60;

        public static int GetHealMilliHp(int itemId)
        {
            if (itemId >= FirstCookedFoodItemId && itemId <= LastCookedFoodItemId)
            {
                return _healPayoutFlatHp[itemId - FirstCookedFoodItemId] * 1000;
            }

            if (itemId <= 0 || itemId > ContentRegistry.ItemDefinitions.Length)
            {
                return 0;
            }

            // Modul: raw fish is food. A caught fish heals by the tier of the
            // water it came out of, on the same curve cooked food uses - just
            // lower, because nobody cooked it. Without this the larder refused
            // every fish in the game and the only edible item was a recipe
            // output from a profession the design does not have.
            if (ContentRegistry.IsRawFish(itemId))
            {
                int fishTier = ContentRegistry.ItemDefinitions[itemId - 1].RegionTier;
                int fishIndex = Math.Clamp(fishTier - 1, 0, _healPayoutFlatHp.Length - 1);
                return _healPayoutFlatHp[fishIndex] * RawFishHealPercent * 1000 / 100;
            }

            // Legacy "_food_consumable" items carry no authored heal value, so
            // their RegionTier stands in for a cooking tier. Clamped into the
            // table rather than extrapolated - inventing a curve for four
            // unobtainable items would be worse than reusing the real one.
            string baseId = ContentRegistry.GetItemBaseId(itemId);
            if (!baseId.Contains("_food"))
            {
                return 0;
            }

            int regionTier = ContentRegistry.ItemDefinitions[itemId - 1].RegionTier;
            int tierIndex = Math.Clamp(regionTier - 1, 0, _healPayoutFlatHp.Length - 1);
            return _healPayoutFlatHp[tierIndex] * 1000;
        }
    }
}

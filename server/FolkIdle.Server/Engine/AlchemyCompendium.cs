using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public static class AlchemyCompendium
    {
        // Modul: legacy hardcoded ids, kept as-is. None of these exist in
        // GameData/items.json (whose ids stop at 379) - they belong to the
        // older generic status-effect consumable path that
        // SimulationEngine's ConsumeConsumableAsset branch still falls
        // through to, so removing them would break that path.
        private static readonly HashSet<uint> _validConsumables = new HashSet<uint>
        {
            1001, // Minor Healing Potion
            1002, // Major Healing Potion
            1003, // Strength Elixir
            1004, // Swiftness Draft
            2001, // Apple
            2002, // Roasted Boar
            2003  // Kelpie Stew
        };

        // Modul: UI rework. This set used to be the WHOLE of the check,
        // which made every real consumable in the game unusable - and not
        // merely rejected: ValidateConsumableRequest is one of the gates
        // whose failure calls TerminateSessionForSecurity, so attempting to
        // eat a Roasted Perch force-disconnected the player.
        //
        // The eight real consumables (items.json ids 372-379) were added
        // later, alongside ConsumableEngine.TryApplyConsumable, which
        // classifies them by the "_food_consumable" /
        // "_offensive_potion_consumable" / "_defensive_potion_consumable"
        // BaseId markers. The engine that applies them was updated; the
        // validator guarding it was not - the same "feature grew, its
        // validator didn't" shape as the Legacy Shop perk and Mentorship
        // capacity bugs found in earlier passes.
        //
        // Classifying off the same BaseId markers ConsumableEngine itself
        // uses means the two can never drift apart again: anything that
        // engine can apply, this accepts, by construction.
        public static bool IsValidConsumable(uint itemId)
        {
            if (_validConsumables.Contains(itemId))
            {
                return true;
            }

            if (itemId == 0 || itemId > (uint)ContentRegistry.ItemDefinitions.Length)
            {
                return false;
            }

            string baseId = ContentRegistry.GetItemBaseId((int)itemId);
            return baseId.Contains("_food_consumable")
                || baseId.Contains("_offensive_potion_consumable")
                || baseId.Contains("_defensive_potion_consumable");
        }
    }
}

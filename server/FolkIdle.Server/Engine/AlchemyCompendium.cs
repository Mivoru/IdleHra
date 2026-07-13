using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    public static class AlchemyCompendium
    {
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

        public static bool IsValidConsumable(uint itemId)
        {
            return _validConsumables.Contains(itemId);
        }
    }
}

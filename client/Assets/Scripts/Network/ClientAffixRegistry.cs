using System;
using System.Collections.Generic;

namespace FolkIdle.Client.Network
{
    // Modul: Affix System Unification. Client mirror of the server's
    // AffixRegistry, for display only - the server remains the sole authority
    // on what an affix rolls and what it is worth.
    //
    // This exists because an affix payload is a bag of ids and magnitudes with
    // no human-readable content whatsoever ({"crit_dmg_pct":75}), so without a
    // display layer every inventory and forge row could only show raw JSON.
    // The percentage/flat distinction also has to be mirrored, because a
    // magnitude of 75 means "+75 HP" for a flat affix and "+7.5%" for a
    // percentage one - the same number rendered two different ways.
    public static class ClientAffixRegistry
    {
        public const char StackSeparator = '#';

        private readonly struct AffixDisplay
        {
            public readonly string Label;
            public readonly bool IsPercentage;

            public AffixDisplay(string label, bool isPercentage)
            {
                Label = label;
                IsPercentage = isPercentage;
            }
        }

        // Keys and the flat/percentage split mirror the server's AffixRegistry
        // definitions exactly; drift here shows a wrong number to the player
        // rather than corrupting anything, but it is still drift.
        private static readonly Dictionary<string, AffixDisplay> _displays = new Dictionary<string, AffixDisplay>(StringComparer.Ordinal)
        {
            { "flat_hp", new AffixDisplay("Health", false) },
            { "flat_armor", new AffixDisplay("Armour", false) },
            { "armor_pen_flat", new AffixDisplay("Armour Penetration", false) },
            { "melee_dmg_pct", new AffixDisplay("Melee Damage", true) },
            { "range_dmg_pct", new AffixDisplay("Ranged Damage", true) },
            { "magic_dmg_pct", new AffixDisplay("Magic Damage", true) },
            { "attack_speed_pct", new AffixDisplay("Attack Speed", true) },
            { "crit_chance_pct", new AffixDisplay("Critical Chance", true) },
            { "crit_dmg_pct", new AffixDisplay("Critical Damage", true) },
            { "lifesteal_pct", new AffixDisplay("Lifesteal", true) },
            { "dodge_chance_pct", new AffixDisplay("Dodge Chance", true) },
            { "block_chance_pct", new AffixDisplay("Block Chance", true) },

            // Legacy numeric keys, still present on any item generated before
            // the ids were unified. Shown rather than hidden, so an older item
            // does not look like it has no affixes at all.
            { "1", new AffixDisplay("Attack", false) },
            { "2", new AffixDisplay("Armour", false) },
            { "3", new AffixDisplay("Critical Chance", true) },
            { "4", new AffixDisplay("Loot Luck", true) },
            { "5", new AffixDisplay("Health", false) }
        };

        public static string StripStackSuffix(string payloadKey)
        {
            if (string.IsNullOrEmpty(payloadKey)) return string.Empty;

            int separatorIndex = payloadKey.IndexOf(StackSeparator);
            return separatorIndex < 0 ? payloadKey : payloadKey.Substring(0, separatorIndex);
        }

        // "Critical Damage +7.5%" / "Health +240". Percentage magnitudes are
        // carried in tenths of a percent server-side.
        public static string Describe(string payloadKey, int magnitude)
        {
            string affixId = StripStackSuffix(payloadKey);

            if (!_displays.TryGetValue(affixId, out AffixDisplay display))
            {
                // An unknown id is still worth showing with its raw value -
                // silently dropping it would hide a real stat the server is
                // applying.
                return affixId + " +" + magnitude;
            }

            if (!display.IsPercentage)
            {
                return display.Label + " +" + magnitude;
            }

            int whole = magnitude / 10;
            int tenths = magnitude % 10;
            return tenths == 0
                ? display.Label + " +" + whole + "%"
                : display.Label + " +" + whole + "." + tenths + "%";
        }

        // Modul: GDD Module 03 section 5.2. The rarity to affix-count table,
        // mirrored so the UI can explain the rule instead of leaving the
        // player to infer it from drops.
        public static int GetAffixCount(int rarityTier)
        {
            if (rarityTier <= 3) return 1;
            if (rarityTier <= 6) return 2;
            if (rarityTier <= 9) return 3;
            if (rarityTier <= 12) return 4;
            return 5;
        }

        // Modul: GDD Module 03 section 5.3 - Diamond_Cost = floor(5 * 1.35^(N-1)).
        // Mirrors AffixRegistry.CalculateRerollDiamondCost so the price shown
        // is the price charged.
        public static long GetRerollDiamondCost(int rarityTier)
        {
            if (rarityTier < 1) rarityTier = 1;
            return (long)Math.Floor(5.0 * Math.Pow(1.35, rarityTier - 1));
        }

        private static readonly string[] _rarityNames =
        {
            "Normal", "Common", "Uncommon", "Rare", "Ultra Rare", "Epic", "Legendary",
            "Mythic", "Relic", "Ancient", "Divine", "Demonic", "Godly", "Transcendent"
        };

        public static string GetRarityName(int rarityTier)
        {
            return rarityTier >= 1 && rarityTier <= _rarityNames.Length
                ? _rarityNames[rarityTier - 1]
                : "Normal";
        }
    }
}

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

        // Modul: affix rarity, 2026-08-01. Payload keys now carry "@N" after
        // any stack suffix. This mirror MUST strip it exactly as the server's
        // AffixRegistry does - a key it fails to strip resolves to no display
        // entry, and the affix silently vanishes from the item tooltip while
        // still counting in combat. That client/server drift is the same class
        // of bug that made the original affix payload disagreement invisible.
        public const char RaritySeparator = '@';

        // Modul: auto-reroll stop conditions, 2026-08-01.
        //
        // ORDER IS A WIRE CONTRACT. The packet carries a stop-affix target as a
        // 1-based index into the server's AffixRegistry.Definitions array, so
        // this list must match that array's ORDER exactly - not merely contain
        // the same ids. A reordering here silently retargets the stop condition
        // onto a different stat, which the player would experience as
        // auto-reroll ignoring what they asked for.
        //
        // An index rather than a string because ClientCommandPacket is
        // fixed-layout and cannot carry a variable-length affix id.
        private static readonly string[] _orderedAffixIds =
        {
            "flat_hp",
            "flat_armor",
            "melee_dmg_pct",
            "range_dmg_pct",
            "magic_dmg_pct",
            "attack_speed_pct",
            "crit_chance_pct",
            "crit_dmg_pct",
            "lifesteal_pct",
            "armor_pen_flat",
            "dodge_chance_pct",
            "block_chance_pct"
        };

        public static int DefinitionCount => _orderedAffixIds.Length;

        public static string GetAffixId(int zeroBasedIndex)
        {
            if (zeroBasedIndex < 0 || zeroBasedIndex >= _orderedAffixIds.Length) return string.Empty;
            return _orderedAffixIds[zeroBasedIndex];
        }

        // Display label for a definition index - "Critical Damage" rather than
        // "crit_dmg_pct".
        public static string GetAffixLabel(int zeroBasedIndex)
        {
            string id = GetAffixId(zeroBasedIndex);
            if (id.Length == 0) return "Any stat";

            return _displays.TryGetValue(id, out AffixDisplay display) ? display.Label : id;
        }

        public static string StripStackSuffix(string payloadKey)
        {
            if (string.IsNullOrEmpty(payloadKey)) return string.Empty;

            int end = payloadKey.Length;

            int rarityIndex = payloadKey.LastIndexOf(RaritySeparator);
            if (rarityIndex >= 0) end = rarityIndex;

            int separatorIndex = payloadKey.IndexOf(StackSeparator);
            if (separatorIndex >= 0 && separatorIndex < end) end = separatorIndex;

            return end == payloadKey.Length ? payloadKey : payloadKey.Substring(0, end);
        }

        // Mirrors AffixRegistry.ParseRarity, including its legacy default of
        // Rare (3) for keys written before affix rarity existed.
        public static int ParseAffixRarity(string payloadKey)
        {
            const int legacyRarity = 3;
            if (string.IsNullOrEmpty(payloadKey)) return legacyRarity;

            int at = payloadKey.LastIndexOf(RaritySeparator);
            if (at < 0 || at == payloadKey.Length - 1) return legacyRarity;

            if (!int.TryParse(payloadKey.Substring(at + 1), out int parsed)) return legacyRarity;

            if (parsed < 1) return 1;
            if (parsed > 5) return 5;
            return parsed;
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
        // Modul: reroll economy mirror, 2026-08-01. These duplicate
        // AffixRegistry's curves so the panel can quote a price before the
        // player commits. The SERVER remains authoritative - it recomputes and
        // charges independently, and a client that quoted low would simply see
        // the command rejected rather than get a discount.
        //
        // GetRerollDiamondCost below is retained ONLY for the rarity-upgrade
        // path; value and stat rerolls moved to gold.
        public const long RerollGoldBase = 250L;
        public const long RerollGoldMaxCost = 100_000_000L;

        public static long GetRerollGoldCost(int itemRarityTier, int consecutiveAttempts, bool rerollStatType)
        {
            if (itemRarityTier < 1) itemRarityTier = 1;
            if (consecutiveAttempts < 0) consecutiveAttempts = 0;

            double cost = RerollGoldBase
                * Math.Pow(1.9, itemRarityTier - 1)
                * Math.Pow(1.35, consecutiveAttempts);

            if (rerollStatType) cost *= 2.5;

            if (double.IsNaN(cost) || cost <= 0.0) return RerollGoldBase;
            if (cost >= RerollGoldMaxCost) return RerollGoldMaxCost;
            return (long)Math.Floor(cost);
        }

        // Keyed on the AFFIX's current rarity (1-5), not the item's tier.
        // Legendary is terminal and costs nothing because it cannot be bought.
        public static long GetRarityUpgradeDiamondCost(int currentAffixRarity)
        {
            if (currentAffixRarity >= 5) return 0L;
            if (currentAffixRarity < 1) currentAffixRarity = 1;

            return (long)Math.Floor(5.0 * Math.Pow(3.4, currentAffixRarity - 1));
        }

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

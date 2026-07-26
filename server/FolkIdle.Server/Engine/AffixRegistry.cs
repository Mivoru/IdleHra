using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    // Modul: Affix System Unification. The single authoritative implementation
    // of GDD Module 14 section 1.3 (Complete Affix Pool Registry) and its two
    // scaling laws from 1.1/1.2.
    //
    // Why this exists: the affix payload on EquipmentInstance was being written
    // in two mutually unintelligible formats and read in a third.
    // CombatLootEngine wrote numeric keys "1".."5"; AffixRerollEngine wrote
    // GDD-style ids with a random hex suffix ("flat_hp_a3f2");
    // EquipmentSlotEngine only ever read "1".."4". The consequences were real
    // and player-visible:
    //   - Rerolling an affix ALWAYS destroyed its stat value. The numeric key
    //     was removed and a GDD-named key added that nothing read, so a player
    //     paid diamonds to make an item strictly worse.
    //   - "5" (flat HP) was written by every drop and read by nothing, so the
    //     GDD's headline flat affix never applied at all.
    //   - Rerolls ignored slot legality entirely, so a shield-only
    //     block_chance_pct could land on a sword.
    // Everything now goes through this one registry, keyed by the GDD's own
    // affix ids.
    public enum EquipmentSlotKind
    {
        Unknown = 0,
        Weapon = 1,
        Helmet = 2,
        Chest = 3,
        Leggings = 4,
        Boots = 5,
        Gloves = 6,
        Shield = 7
    }

    [Flags]
    public enum EquipmentSlotMask
    {
        None = 0,
        Weapon = 1 << 1,
        Helmet = 1 << 2,
        Chest = 1 << 3,
        Leggings = 1 << 4,
        Boots = 1 << 5,
        Gloves = 1 << 6,
        Shield = 1 << 7
    }

    // Which scaling law from GDD 1.1/1.2 produces this affix's magnitude.
    public enum AffixScalingLaw
    {
        // floor(15 * R * 1.22^(N-1)) - region and rarity.
        FlatHp = 0,
        // floor(2 * R * 1.18^(N-1)) - region and rarity.
        FlatStat = 1,
        // BaseValue + Growth * (N-1) - rarity only, deliberately region
        // independent so early regions cannot roll out-of-bounds percentages.
        Percentage = 2
    }

    public readonly struct AffixDefinition
    {
        public readonly string Id;
        public readonly EquipmentSlotMask AllowedSlots;
        public readonly AffixScalingLaw Law;

        // Percentage law only, in tenths of a percent so the whole pipeline
        // stays integer - GDD values like 0.5% and 1.5% per tier cannot be
        // represented in whole percent without losing the growth curve.
        public readonly int BaseValueTenthsPct;
        public readonly int GrowthTenthsPctPerTier;

        public AffixDefinition(string id, EquipmentSlotMask allowedSlots, AffixScalingLaw law, int baseValueTenthsPct = 0, int growthTenthsPctPerTier = 0)
        {
            Id = id;
            AllowedSlots = allowedSlots;
            Law = law;
            BaseValueTenthsPct = baseValueTenthsPct;
            GrowthTenthsPctPerTier = growthTenthsPctPerTier;
        }
    }

    public static class AffixRegistry
    {
        // Modul: GDD 1.3 verbatim, with one documented deviation. The GDD's
        // slot lists reference Amulet, Ring 1 and Ring 2, none of which exist
        // as equippable slots in this game (the wire protocol has exactly
        // Weapon, Armor/Chest and Leggings, and the item catalogue adds
        // Helmet, Boots, Gloves and Helper/offhand). Those entries are
        // therefore dropped rather than silently remapped onto a slot the GDD
        // did not name. "Shield" maps onto the Helper/offhand slot, which is
        // this game's shield equivalent (eq_*_helper_offhand_base).
        private static readonly AffixDefinition[] _definitions =
        {
            new AffixDefinition("flat_hp",
                EquipmentSlotMask.Helmet | EquipmentSlotMask.Chest | EquipmentSlotMask.Leggings | EquipmentSlotMask.Boots | EquipmentSlotMask.Shield,
                AffixScalingLaw.FlatHp),

            new AffixDefinition("flat_armor",
                EquipmentSlotMask.Helmet | EquipmentSlotMask.Chest | EquipmentSlotMask.Leggings | EquipmentSlotMask.Boots | EquipmentSlotMask.Shield,
                AffixScalingLaw.FlatStat),

            new AffixDefinition("melee_dmg_pct", EquipmentSlotMask.Weapon, AffixScalingLaw.Percentage, 20, 15),
            new AffixDefinition("range_dmg_pct", EquipmentSlotMask.Weapon, AffixScalingLaw.Percentage, 20, 15),
            new AffixDefinition("magic_dmg_pct", EquipmentSlotMask.Weapon, AffixScalingLaw.Percentage, 20, 15),

            new AffixDefinition("attack_speed_pct",
                EquipmentSlotMask.Weapon | EquipmentSlotMask.Gloves | EquipmentSlotMask.Boots,
                AffixScalingLaw.Percentage, 10, 5),

            new AffixDefinition("crit_chance_pct",
                EquipmentSlotMask.Weapon | EquipmentSlotMask.Helmet,
                AffixScalingLaw.Percentage, 5, 5),

            new AffixDefinition("crit_dmg_pct", EquipmentSlotMask.Weapon, AffixScalingLaw.Percentage, 50, 25),

            new AffixDefinition("lifesteal_pct", EquipmentSlotMask.Weapon, AffixScalingLaw.Percentage, 5, 3),

            new AffixDefinition("armor_pen_flat",
                EquipmentSlotMask.Weapon | EquipmentSlotMask.Gloves,
                AffixScalingLaw.FlatStat),

            new AffixDefinition("dodge_chance_pct",
                EquipmentSlotMask.Boots | EquipmentSlotMask.Helmet | EquipmentSlotMask.Leggings,
                AffixScalingLaw.Percentage, 5, 4),

            new AffixDefinition("block_chance_pct", EquipmentSlotMask.Shield, AffixScalingLaw.Percentage, 10, 8)
        };

        private static readonly Dictionary<string, int> _indexById = BuildIndex();

        private static Dictionary<string, int> BuildIndex()
        {
            var index = new Dictionary<string, int>(_definitions.Length, StringComparer.Ordinal);
            for (int i = 0; i < _definitions.Length; i++)
            {
                index[_definitions[i].Id] = i;
            }
            return index;
        }

        public static ReadOnlySpan<AffixDefinition> Definitions => _definitions;

        // Modul: a stacked affix is stored as "id#2", "id#3" and so on - see
        // RollAffixes for why duplicates are possible at all. Every reader
        // must strip the suffix before looking the definition up.
        public const char StackSeparator = '#';

        public static string StripStackSuffix(string payloadKey)
        {
            if (string.IsNullOrEmpty(payloadKey)) return string.Empty;

            int separatorIndex = payloadKey.IndexOf(StackSeparator);
            return separatorIndex < 0 ? payloadKey : payloadKey.Substring(0, separatorIndex);
        }

        public static bool TryGetDefinition(string affixId, out AffixDefinition definition)
        {
            if (affixId != null && _indexById.TryGetValue(affixId, out int index))
            {
                definition = _definitions[index];
                return true;
            }

            definition = default;
            return false;
        }

        // Resolves the equip slot from the BaseItemId suffix convention the
        // item catalogue already uses. Note "_range_weapon_slot_" - items.json
        // spells ranged weapons that way, not "_ranged_", and getting this
        // wrong would silently make every bow ineligible for weapon affixes.
        public static EquipmentSlotKind ResolveSlot(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return EquipmentSlotKind.Unknown;

            if (baseItemId.Contains("_melee_weapon_slot_", StringComparison.Ordinal)
                || baseItemId.Contains("_range_weapon_slot_", StringComparison.Ordinal)
                || baseItemId.Contains("_ranged_weapon_slot_", StringComparison.Ordinal)
                || baseItemId.Contains("_magic_weapon_slot_", StringComparison.Ordinal))
            {
                return EquipmentSlotKind.Weapon;
            }

            if (baseItemId.Contains("_helmet_armor_slot_", StringComparison.Ordinal)) return EquipmentSlotKind.Helmet;
            if (baseItemId.Contains("_chest_armor_slot_", StringComparison.Ordinal)) return EquipmentSlotKind.Chest;
            if (baseItemId.Contains("_leggings_armor_slot_", StringComparison.Ordinal)) return EquipmentSlotKind.Leggings;
            if (baseItemId.Contains("_boots_armor_slot_", StringComparison.Ordinal)) return EquipmentSlotKind.Boots;
            if (baseItemId.Contains("_gloves_armor_slot_", StringComparison.Ordinal)) return EquipmentSlotKind.Gloves;
            if (baseItemId.Contains("_helper_offhand_", StringComparison.Ordinal)) return EquipmentSlotKind.Shield;

            return EquipmentSlotKind.Unknown;
        }

        public static EquipmentSlotMask ToMask(EquipmentSlotKind slot)
        {
            switch (slot)
            {
                case EquipmentSlotKind.Weapon: return EquipmentSlotMask.Weapon;
                case EquipmentSlotKind.Helmet: return EquipmentSlotMask.Helmet;
                case EquipmentSlotKind.Chest: return EquipmentSlotMask.Chest;
                case EquipmentSlotKind.Leggings: return EquipmentSlotMask.Leggings;
                case EquipmentSlotKind.Boots: return EquipmentSlotMask.Boots;
                case EquipmentSlotKind.Gloves: return EquipmentSlotMask.Gloves;
                case EquipmentSlotKind.Shield: return EquipmentSlotMask.Shield;
                default: return EquipmentSlotMask.None;
            }
        }

        // Collects the affix ids legal for one slot into the caller's buffer.
        // Buffer-based rather than returning a list so the drop path stays
        // allocation-free: a caller can stackalloc or reuse an array.
        public static int GetLegalAffixIndices(EquipmentSlotKind slot, Span<int> destination)
        {
            EquipmentSlotMask mask = ToMask(slot);
            if (mask == EquipmentSlotMask.None) return 0;

            int count = 0;
            for (int i = 0; i < _definitions.Length && count < destination.Length; i++)
            {
                if ((_definitions[i].AllowedSlots & mask) != 0)
                {
                    destination[count++] = i;
                }
            }
            return count;
        }

        // GDD 1.1/1.2. Percentage results are returned in tenths of a percent,
        // flat results in whole points, so the caller must know which law an
        // affix uses to interpret the number - hence Law being public.
        public static int CalculateMagnitude(in AffixDefinition definition, int regionTier, int rarityTier)
        {
            if (regionTier < 1) regionTier = 1;
            if (rarityTier < 1) rarityTier = 1;

            switch (definition.Law)
            {
                case AffixScalingLaw.FlatHp:
                    return (int)Math.Floor(15.0 * regionTier * Math.Pow(1.22, rarityTier - 1));
                case AffixScalingLaw.FlatStat:
                    return (int)Math.Floor(2.0 * regionTier * Math.Pow(1.18, rarityTier - 1));
                default:
                    return definition.BaseValueTenthsPct + definition.GrowthTenthsPctPerTier * (rarityTier - 1);
            }
        }

        // Modul: GDD Module 03 section 5.3. Diamond_Cost = floor(5 * 1.35^(N-1)).
        //
        // The GDD also lists illustrative prices (Tier 7 = 24, Tier 14 = 229)
        // which do NOT match its own formula (the formula gives 30 and 247).
        // No single geometric base reproduces both examples, so the explicit
        // formula is treated as authoritative and the examples as drafting
        // errors. Flagged here rather than silently picking one.
        public static long CalculateRerollDiamondCost(int rarityTier)
        {
            if (rarityTier < 1) rarityTier = 1;
            return (long)Math.Floor(5.0 * Math.Pow(1.35, rarityTier - 1));
        }

        // Rolls a full affix set for a freshly dropped or crafted item.
        //
        // Duplicates are permitted, and have to be: GDD 5.2 grants up to 5
        // affixes at Tier 13-14, but with Ring and Amulet slots absent from
        // this game a Chest piece has only two legal affixes (flat_hp,
        // flat_armor). Capping the count would quietly flatten the rarity
        // power curve for armour, so a slot whose legal pool is exhausted
        // stacks another instance of an affix it already has instead. Stacked
        // instances are keyed "id#2", "id#3" and summed by every reader.
        public static void RollAffixes(string baseItemId, int regionTier, int rarityTier, int affixCount, IDictionary<string, int> destination)
        {
            if (destination == null || affixCount <= 0) return;

            EquipmentSlotKind slot = ResolveSlot(baseItemId);

            Span<int> legal = stackalloc int[16];
            int legalCount = GetLegalAffixIndices(slot, legal);
            if (legalCount == 0)
            {
                // An unrecognised slot suffix should not produce a silently
                // affix-less item; fall back to the two universal flat
                // affixes so the item still scales with rarity.
                AddOrStack(destination, _definitions[0], regionTier, rarityTier);
                return;
            }

            Span<int> stackCounts = stackalloc int[16];

            for (int rolled = 0; rolled < affixCount; rolled++)
            {
                // Prefer an affix this item does not have yet, so low
                // rarities read as varied rather than as one stat repeated.
                int chosen = -1;
                int unusedCount = 0;
                for (int i = 0; i < legalCount; i++)
                {
                    if (stackCounts[i] == 0) unusedCount++;
                }

                if (unusedCount > 0)
                {
                    int target = Random.Shared.Next(unusedCount);
                    int seen = 0;
                    for (int i = 0; i < legalCount; i++)
                    {
                        if (stackCounts[i] != 0) continue;
                        if (seen == target) { chosen = i; break; }
                        seen++;
                    }
                }
                else
                {
                    chosen = Random.Shared.Next(legalCount);
                }

                if (chosen < 0) chosen = 0;

                stackCounts[chosen]++;
                AddOrStack(destination, _definitions[legal[chosen]], regionTier, rarityTier, stackCounts[chosen]);
            }
        }

        private static void AddOrStack(IDictionary<string, int> destination, in AffixDefinition definition, int regionTier, int rarityTier, int stackIndex = 1)
        {
            string key = stackIndex <= 1
                ? definition.Id
                : definition.Id + StackSeparator + stackIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            destination[key] = CalculateMagnitude(definition, regionTier, rarityTier);
        }

        // Picks a replacement affix for a reroll: legal for the slot, and
        // different from the one being replaced whenever the slot has more
        // than one legal option (rerolling into the identical affix would
        // charge diamonds for nothing).
        public static bool TryRollReplacement(string baseItemId, string replacedAffixId, out AffixDefinition replacement)
        {
            EquipmentSlotKind slot = ResolveSlot(baseItemId);

            Span<int> legal = stackalloc int[16];
            int legalCount = GetLegalAffixIndices(slot, legal);
            if (legalCount == 0)
            {
                replacement = default;
                return false;
            }

            if (legalCount > 1 && !string.IsNullOrEmpty(replacedAffixId))
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    var candidate = _definitions[legal[Random.Shared.Next(legalCount)]];
                    if (!string.Equals(candidate.Id, replacedAffixId, StringComparison.Ordinal))
                    {
                        replacement = candidate;
                        return true;
                    }
                }
            }

            replacement = _definitions[legal[Random.Shared.Next(legalCount)]];
            return true;
        }
    }
}

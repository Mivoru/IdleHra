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
        // Modul: Shield was this game's name for the offhand, and there is no
        // offhand - see EquipmentSlotEngine. Amulet and Ring replace it, which
        // is what the GDD named in the first place.
        Amulet = 7,
        Ring = 8,

        // Modul: tools are worn, not carried.
        //
        // A tool was a stackable material in the chest, so it could not hold a
        // rarity or an affix - every axe in the game was identical to every
        // other axe of the same wood. One kind rather than three: a
        // gathering affix reads the same on an axe, a pickaxe and a rod, and
        // splitting them would mean three copies of every definition to say
        // the same thing.
        Tool = 9
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
        Amulet = 1 << 7,
        Ring = 1 << 8,
        Tool = 1 << 9
    }

    // Which scaling law from GDD 1.1/1.2 produces this affix's magnitude.
    public enum AffixScalingLaw
    {
        // floor(15 * R * rarityMultiplier) - region and AFFIX rarity.
        FlatHp = 0,
        // floor(2 * R * rarityMultiplier) - region and AFFIX rarity.
        FlatStat = 1,
        // BaseValue + Growth * (A-1) - affix rarity only, deliberately region
        // independent so early regions cannot roll out-of-bounds percentages.
        Percentage = 2
    }

    // Modul: affix rarity. A second, deliberately SMALLER rarity axis than the
    // 14 item tiers in GDD Module 03.
    //
    // The split, decided 2026-08-01: the ITEM's rarity tier (1-14) decides HOW
    // MANY affixes it carries - GDD 5.2's 1/2/3/4/5 table is unchanged, and 5
    // remains the hard cap. This AFFIX rarity decides HOW STRONG each of those
    // affixes is. So a Transcendent drop is still the best item in the game
    // because it rolls five affixes, but a Rare item whose two affixes both
    // came up Legendary can compete - which is what makes rerolling worth
    // spending on at every tier rather than only at the top.
    //
    // Region tier still multiplies flat magnitudes, so progression through the
    // five regions keeps driving raw power independently of both rarity axes.
    public enum AffixRarity
    {
        Common = 1,
        Uncommon = 2,
        Rare = 3,
        Epic = 4,
        Legendary = 5
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
        // Modul: GDD 1.3, and the deviation is now GONE.
        //
        // This used to say the GDD's Amulet and Ring slots "do not exist as
        // equippable slots in this game", drop their entries, and remap Shield
        // onto a helper/offhand slot instead. That was backwards on both
        // counts: the offhand was the invented slot, and Amulet and Ring are
        // real - one of each per tier has been in the catalogue all along. The
        // GDD named them correctly and the code disagreed.
        //
        // Ring 1 and Ring 2 collapse to one Ring, because the catalogue authors
        // exactly one ring per tier.
        private static readonly AffixDefinition[] _definitions =
        {
            new AffixDefinition("flat_hp",
                EquipmentSlotMask.Helmet | EquipmentSlotMask.Chest | EquipmentSlotMask.Leggings | EquipmentSlotMask.Boots | EquipmentSlotMask.Amulet,
                AffixScalingLaw.FlatHp),

            new AffixDefinition("flat_armor",
                EquipmentSlotMask.Helmet | EquipmentSlotMask.Chest | EquipmentSlotMask.Leggings | EquipmentSlotMask.Boots | EquipmentSlotMask.Amulet,
                AffixScalingLaw.FlatStat),

            // Modul: what a tool can roll.
            //
            // Deliberately three, and deliberately percentages: a tool's job is
            // to make gathering faster and richer, and a flat bonus would mean
            // nothing against a node whose yield is a weighted table. Each
            // rolls its own affix rarity like every other affix, so a Godly
            // axe carries five of these at independently rolled magnitudes.
            //
            // Speed is the smallest because it compounds with the tool TIER,
            // which is already a large multiplier; rare-find is the largest per
            // point because it moves the least often.
            new AffixDefinition("gather_speed_pct", EquipmentSlotMask.Tool, AffixScalingLaw.Percentage, 15, 10),
            new AffixDefinition("gather_yield_pct", EquipmentSlotMask.Tool, AffixScalingLaw.Percentage, 25, 18),
            new AffixDefinition("gather_rare_find_pct", EquipmentSlotMask.Tool, AffixScalingLaw.Percentage, 30, 22),

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

            // Modul: block_chance_pct was Shield-only, and the shield is gone. It
            // moves to the Ring rather than being deleted: an affix pool of one
            // legal slot that no longer exists rolls on nothing, and the GDD's
            // twelve-affix pool is the part of this that was never wrong.
            new AffixDefinition("block_chance_pct", EquipmentSlotMask.Ring, AffixScalingLaw.Percentage, 10, 8)
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

        // Modul: affix rarity. Encoded in the payload key as "@N" AFTER any
        // stack suffix, so "flat_hp", "flat_hp@4", "flat_hp#2@4" are all valid
        // and every existing reader that only strips '#' still resolves the
        // definition correctly once it also strips '@'.
        //
        // A key with no "@" is a legacy affix rolled before this system existed.
        // Those are reported as Rare - the middle of the scale - deliberately:
        // the stored magnitude is left untouched (payload values are absolute,
        // not recomputed on read), so calling them Common would misrepresent
        // strong old gear as junk and calling them Legendary would misrepresent
        // junk as trophies. Neither the totals nor combat change either way.
        public const char RaritySeparator = '@';
        public const AffixRarity LegacyAffixRarity = AffixRarity.Rare;

        public const int MinAffixCount = 1;

        // GDD 5.2 caps affix count at 5 (Godly/Transcendent). Reaffirmed as the
        // hard ceiling on 2026-08-01 when affix rarity was added - the extra
        // power budget went into per-affix rarity instead of more affix slots.
        public const int MaxAffixCount = 5;

        // Multiplier applied to flat magnitudes by affix rarity: 1.6^(A-1).
        //
        // Chosen so Legendary is 6.55x Common - a spread big enough that a
        // Legendary roll is visibly a trophy, small enough that a full set of
        // Common affixes is still a functioning item rather than dead weight.
        // A steeper curve made unrerolled drops worthless; a flatter one made
        // rerolling pointless.
        private const double AffixRarityGrowth = 1.6;

        public static double GetAffixRarityMultiplier(AffixRarity rarity)
        {
            int index = (int)rarity;
            if (index < 1) index = 1;
            if (index > (int)AffixRarity.Legendary) index = (int)AffixRarity.Legendary;
            return Math.Pow(AffixRarityGrowth, index - 1);
        }

        // Drop weights for a freshly rolled affix, out of 1000. Legendary at
        // 1.5% means a five-affix item averages roughly one Legendary per
        // thirteen items, so the reroll system - not the drop table - is the
        // realistic path to a full Legendary set.
        private static readonly int[] _rarityWeightsPerMille = { 520, 280, 150, 40, 10 };

        public static AffixRarity RollAffixRarity()
        {
            int roll = Random.Shared.Next(1000);
            int cumulative = 0;
            for (int i = 0; i < _rarityWeightsPerMille.Length; i++)
            {
                cumulative += _rarityWeightsPerMille[i];
                if (roll < cumulative)
                {
                    return (AffixRarity)(i + 1);
                }
            }
            return AffixRarity.Common;
        }

        // Splits a payload key into its definition id and affix rarity.
        public static AffixRarity ParseRarity(string payloadKey)
        {
            if (string.IsNullOrEmpty(payloadKey)) return LegacyAffixRarity;

            int at = payloadKey.LastIndexOf(RaritySeparator);
            if (at < 0 || at == payloadKey.Length - 1) return LegacyAffixRarity;

            if (!int.TryParse(payloadKey.AsSpan(at + 1), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            {
                return LegacyAffixRarity;
            }

            if (parsed < 1) parsed = 1;
            if (parsed > (int)AffixRarity.Legendary) parsed = (int)AffixRarity.Legendary;
            return (AffixRarity)parsed;
        }

        public static string BuildPayloadKey(string affixId, int stackIndex, AffixRarity rarity)
        {
            string key = stackIndex <= 1
                ? affixId
                : affixId + StackSeparator + stackIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return key + RaritySeparator + ((int)rarity).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Affix COUNT by item rarity is NOT redefined here. RarityTier
        // .GetAffixCount (CombatLootEngine.cs) already implements GDD 5.2's
        // 1/2/3/4/5 table and every drop path calls it. A second copy here
        // would be a second authority over the same rule, which is precisely
        // the failure this file's own history documents - three namespaces
        // once disagreed about affix payload keys. MaxAffixCount above is a
        // ceiling assertion, not a parallel implementation.

        // Strips BOTH the stack suffix and the affix-rarity marker, so
        // "flat_hp#2@4" resolves to "flat_hp". Every payload reader goes
        // through here; missing the '@' case would have made every
        // rarity-tagged affix fail definition lookup and silently contribute
        // nothing to combat totals - the exact failure mode that made the
        // original three-way payload disagreement so hard to see.
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
            if (baseItemId.Contains("_amulet_", StringComparison.Ordinal)) return EquipmentSlotKind.Amulet;
            if (baseItemId.Contains("_ring_", StringComparison.Ordinal)) return EquipmentSlotKind.Ring;

            // Axes, pickaxes and fishing rods, all authored with a "_tool"
            // suffix - see ContentRegistry.GetToolKind, which is the same
            // convention read for the other half of the question.
            if (baseItemId.EndsWith("_tool", StringComparison.Ordinal)) return EquipmentSlotKind.Tool;

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
                case EquipmentSlotKind.Amulet: return EquipmentSlotMask.Amulet;
                case EquipmentSlotKind.Ring: return EquipmentSlotMask.Ring;
                case EquipmentSlotKind.Tool: return EquipmentSlotMask.Tool;
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
        // Magnitude is a function of REGION and AFFIX rarity - not of the
        // item's 14-tier rarity, which now only decides how many affixes the
        // item gets (GetAffixCountForItemRarity).
        //
        // Region keeps the growth term it always had, so progressing through
        // the five regions still raises raw power on its own. What changed is
        // that the second growth term is the affix's own rarity, which is the
        // thing a reroll can actually move.
        public static int CalculateMagnitude(in AffixDefinition definition, int regionTier, AffixRarity affixRarity)
        {
            if (regionTier < 1) regionTier = 1;

            double rarityMultiplier = GetAffixRarityMultiplier(affixRarity);
            int rarityIndex = (int)affixRarity;
            if (rarityIndex < 1) rarityIndex = 1;

            // Modul: AFFIXES GREW LINEARLY AGAINST GEAR THAT GREW GEOMETRICALLY,
            // so rarity stopped mattering exactly where it was meant to matter
            // most.
            //
            // These were `15 * regionTier` and `2 * regionTier` - five times
            // larger at region 5 than at region 1. The items they sit on triple
            // every region, so best-in-slot armour runs 8 at region 1 and 648 at
            // region 5, EIGHTY-ONE times. A Legendary armour affix was worth
            // more than the item it was on in region 1 and a tenth of it in
            // region 5, so a player at depth could reroll all day and change
            // nothing they could feel.
            //
            // Each law now grows with the quantity it adds to. Armour and the
            // other flat stats follow the gear curve at 3x a region; health
            // follows the health pool, which runs about 100 to 2,500 across the
            // game, so 2.2x a region. A Legendary roll is then worth more than
            // the base item carries, at every depth - which is what makes a
            // reroll a decision rather than a formality.
            double gearCurve = Math.Pow(3.0, regionTier - 1);
            double poolCurve = Math.Pow(2.2, regionTier - 1);

            switch (definition.Law)
            {
                case AffixScalingLaw.FlatHp:
                    return (int)Math.Floor(15.0 * poolCurve * rarityMultiplier);
                case AffixScalingLaw.FlatStat:
                    // Modul: base 6, not 2. THE SPREAD BETWEEN LOADOUTS IS THIS
                    // NUMBER, and nothing else.
                    //
                    // Damage taken scales as K/(K+armour), so the ratio between
                    // a starter loadout and a finished one is (K+rich)/(K+poor).
                    // K is the base armour a region authors, which means the
                    // gap is decided entirely by how far AFFIXES can push a set
                    // past its base. At base 2 a full Legendary set carried 130
                    // armour against a base of 40 - a three-fold spread, which
                    // is the difference between dying slowly and dying less
                    // slowly rather than between dying and living.
                    //
                    // At 6 the same set carries 393 against 40: roughly an
                    // eight-fold spread. A boss that near one-shots a player in
                    // three common pieces is a real fight for one in a full
                    // rolled set, and the reroll is what moves you between
                    // those two states.
                    return (int)Math.Floor(6.0 * gearCurve * rarityMultiplier);
                default:
                    return definition.BaseValueTenthsPct + definition.GrowthTenthsPctPerTier * (rarityIndex - 1);
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

        // Modul: magnitude variance. A reroll that keeps both the stat and the
        // rarity has to be able to change SOMETHING, or "reroll this affix's
        // value" would be a no-op the player pays for. CalculateMagnitude is
        // deterministic, so it defines the CENTRE of a band and rolls land
        // anywhere within +/-20% of it.
        //
        // The band is deliberately narrower than one rarity step (1.6x), so a
        // lucky Common roll can never beat an unlucky Uncommon - rarity stays
        // strictly dominant over luck, and upgrading rarity is always the
        // bigger lever than rerolling value.
        private const double MagnitudeVariance = 0.20;

        public static (int Min, int Max) CalculateMagnitudeRange(in AffixDefinition definition, int regionTier, AffixRarity affixRarity)
        {
            int centre = CalculateMagnitude(definition, regionTier, affixRarity);
            int min = (int)Math.Floor(centre * (1.0 - MagnitudeVariance));
            int max = (int)Math.Ceiling(centre * (1.0 + MagnitudeVariance));
            if (min < 1) min = 1;
            if (max < min) max = min;
            return (min, max);
        }

        public static int RollMagnitude(in AffixDefinition definition, int regionTier, AffixRarity affixRarity)
        {
            (int min, int max) = CalculateMagnitudeRange(definition, regionTier, affixRarity);
            return min >= max ? min : Random.Shared.Next(min, max + 1);
        }

        // Modul: reroll economy, decided 2026-08-01.
        //
        // Value and stat rerolls cost GOLD; only a rarity UPGRADE costs
        // Diamonds. The reason is auto-reroll: it burns attempts in bulk, and
        // pricing that in premium currency would have made the headline
        // quality-of-life feature a pay-to-win treadmill. Gold also badly
        // needed an endgame sink - combat income measured around 1500 gold per
        // minute with nothing at the top of the curve to spend it on.
        //
        // Base cost scales on the ITEM's rarity tier, so rerolling a
        // Transcendent is meaningfully expensive.
        public const long RerollGoldBase = 250L;
        private const double RerollGoldItemTierGrowth = 1.9;

        // Modul: THE STREAK MULTIPLIER IS GONE, and it had to go the moment the
        // reroll started rolling rarity at random.
        //
        // It was 1.35 per consecutive attempt, which was defensible when a
        // rarity upgrade was DETERMINISTIC - one guaranteed step per purchase,
        // so a "run of failures" meant the player was chasing a value or a stat
        // and escalation kept that from being free. It is not defensible now:
        // rarity is a weighted roll where Legendary is 1 in 100, so repeated
        // attempts are not a failure state, they are how the system works, and
        // an exponential charge on them prices its own headline outcome out of
        // the game.
        //
        // Measured, at item tier 7: ten attempts cost 642,000 gold and twenty
        // cost 13.5 MILLION, against roughly 564,000 earned across an entire
        // levels 1-100 playthrough. The average chase for a Legendary is a
        // hundred attempts. The multiplier did not make the chase expensive, it
        // made it arithmetically impossible.
        //
        // Flat per reroll now. At tier 7 that is 11,761 a roll, so a Legendary
        // averages about 1.2M - roughly ten hours at region 5's income, which
        // is an endgame chase rather than a wall. See ProgressionRateTests,
        // which prints both curves against the measured gold rate.
        private const double RerollGoldStreakGrowth = 1.0;

        // Modul: no stat-type surcharge. There is one reroll operation now, so
        // there is nothing for a multiplier to distinguish - see
        // RerollOperation. Retained as 1.0 rather than deleted because
        // CalculateRerollGoldCost's signature is public and its third argument
        // is passed by name at every call site.
        private const double RerollStatTypeMultiplier = 1.0;

        // Hard ceiling so the curve cannot overflow or price a reroll beyond
        // what any player could hold. With the streak flat this is only
        // reachable by item tier alone, which tops out around 1.05M at tier 14.
        public const long RerollGoldMaxCost = 100_000_000L;

        public static long CalculateRerollGoldCost(int itemRarityTier, int consecutiveAttempts, bool rerollStatType)
        {
            if (itemRarityTier < 1) itemRarityTier = 1;
            if (consecutiveAttempts < 0) consecutiveAttempts = 0;

            double cost = RerollGoldBase
                * Math.Pow(RerollGoldItemTierGrowth, itemRarityTier - 1)
                * Math.Pow(RerollGoldStreakGrowth, consecutiveAttempts);

            if (rerollStatType) cost *= RerollStatTypeMultiplier;

            if (double.IsNaN(cost) || cost <= 0.0) return RerollGoldBase;
            if (cost >= RerollGoldMaxCost) return RerollGoldMaxCost;
            return (long)Math.Floor(cost);
        }

        // Upgrading an affix one rarity step. Priced in Diamonds on the GDD's
        // own curve, but keyed on the AFFIX's current rarity rather than the
        // item's - upgrading Epic to Legendary should cost the same whatever it
        // is sitting on, since the resulting magnitude gain is the same.
        //
        // Legendary is terminal and returns 0; callers must reject the request
        // rather than charging for a no-op.
        public static long CalculateRarityUpgradeDiamondCost(AffixRarity currentRarity)
        {
            if (currentRarity >= AffixRarity.Legendary) return 0L;

            int step = (int)currentRarity;
            if (step < 1) step = 1;

            // 5, 18, 61, 205 diamonds for Common->Uncommon->Rare->Epic->Legendary.
            return (long)Math.Floor(5.0 * Math.Pow(3.4, step - 1));
        }

        public static bool TryGetNextRarity(AffixRarity current, out AffixRarity next)
        {
            if (current >= AffixRarity.Legendary)
            {
                next = AffixRarity.Legendary;
                return false;
            }

            int index = (int)current;
            if (index < 1) index = 1;
            next = (AffixRarity)(index + 1);
            return true;
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
        public static void RollAffixes(string baseItemId, int regionTier, int itemRarityTier, int affixCount, IDictionary<string, int> destination)
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
                AddOrStack(destination, _definitions[0], regionTier, RollAffixRarity());
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
                // Each affix rolls its OWN rarity independently, so a single
                // item can carry a Legendary next to a Common. That variance is
                // the point: it gives the reroll system per-affix targets
                // instead of one item-wide verdict.
                AddOrStack(destination, _definitions[legal[chosen]], regionTier, RollAffixRarity(), stackCounts[chosen]);
            }
        }

        private static void AddOrStack(IDictionary<string, int> destination, in AffixDefinition definition, int regionTier, AffixRarity affixRarity, int stackIndex = 1)
        {
            string key = BuildPayloadKey(definition.Id, stackIndex, affixRarity);
            destination[key] = RollMagnitude(definition, regionTier, affixRarity);
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

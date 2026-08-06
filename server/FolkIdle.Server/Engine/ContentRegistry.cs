using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    // Modul: mirrors GatheringNodeDefinition.ProfessionType's own numbering
    // (0 = Woodcutting, 1 = Mining) for the raw gathering material id space
    // (GetMaterialString/GetMaterialId/GetMaterialProfessionType) - that
    // space only ever contains Woodcutting or Mining materials, unlike the
    // broader gathering_nodes.json ProfessionType field which also covers
    // Fishing and Herbalism.
    public enum GatheringProfessionType
    {
        Woodcutting = 0,
        Mining = 1
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Locus
    {
        public byte Dominant;
        public byte Recessive;
    }

    public static class RaceIds
    {
        public const byte Human = 1;
        public const byte Vila = 2;
        public const byte Draugr = 3;
        public const byte Kobold = 4;
        public const byte Vodnik = 5;
        public const byte Moosleute = 6;
    }

    public enum GlobalEventType
    {
        None = 0,
        GoldenHarvest = 1,
        BloodMoonVanguard = 2,
        MasterArtisan = 3,
        DiamondStar = 4
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GeneticVector
    {
        public long RawValue;

        public GeneticVector(long rawValue)
        {
            RawValue = rawValue;
        }

        public Locus LocusRace
        {
            get => new Locus { Dominant = (byte)(RawValue & 0xFF), Recessive = (byte)((RawValue >> 8) & 0xFF) };
            set
            {
                RawValue = (RawValue & unchecked((long)0xFFFFFFFFFFFF0000)) | (long)value.Dominant | ((long)value.Recessive << 8);
            }
        }

        public Locus LocusSpeed
        {
            get => new Locus { Dominant = (byte)((RawValue >> 16) & 0xFF), Recessive = (byte)((RawValue >> 24) & 0xFF) };
            set
            {
                RawValue = (RawValue & unchecked((long)0xFFFFFFFF0000FFFF)) | ((long)value.Dominant << 16) | ((long)value.Recessive << 24);
            }
        }

        public Locus LocusCrit
        {
            get => new Locus { Dominant = (byte)((RawValue >> 32) & 0xFF), Recessive = (byte)((RawValue >> 40) & 0xFF) };
            set
            {
                RawValue = (RawValue & unchecked((long)0xFFFF0000FFFFFFFF)) | ((long)value.Dominant << 32) | ((long)value.Recessive << 40);
            }
        }

        public Locus LocusYield
        {
            get => new Locus { Dominant = (byte)((RawValue >> 48) & 0xFF), Recessive = (byte)((RawValue >> 56) & 0xFF) };
            set
            {
                RawValue = (RawValue & unchecked((long)0x0000FFFFFFFFFFFF)) | ((long)value.Dominant << 48) | ((long)value.Recessive << 56);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ItemDefinition
    {
        public int Id;
        public int RegionTier;
        public int BaseValueGold;
        public int FlatAttackPower;
        public int FlatDefenseRating;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MonsterDefinition
    {
        public int Id;
        public int MaxHp;
        public int AttackPower;
        public int BaseGoldReward;
        public int BaseXpReward;
        public int AttackIntervalMs;
        public int LootTableId;

        // Modul: data-driven difficulty region. Replaces the old
        // ((Id - 1) % 30) / 6 + 1 arithmetic convention, which silently
        // wrapped monster ids 31+ back onto tiers 1-5 regardless of their
        // actual stats - the region a monster belongs to is now an authored
        // content fact, not a property of its array position. 0 means "not
        // authored yet"; ContentRegistry.GetMonsterRegionTier falls back to
        // the legacy formula for such entries so stale content data
        // degrades to the old behavior instead of breaking.
        public int RegionTier;

        // Flat whole-HP damage reduction applied per hit against this
        // monster (see SimulationEngine's combat mitigation step).
        public int Armor;

        // Additive dodge score: hit chance against this monster is
        // attackerAccuracy / (100 + DodgeRating), so 0 means base hit
        // chance and higher values make the monster harder to connect with.
        public int DodgeRating;
    }

    // Modul: tunable balancing constants previously hardcoded as C# consts
    // in individual engines (GuildRaidEngine, GuildContributionEngine).
    // Loaded from GameData/GameBalanceConfig.json by
    // ContentRegistry.Initialize so a balance change is a content-data
    // deploy, not a code deploy. Field defaults mirror the exact literals
    // the engines used before externalization, so a missing optional field
    // in the JSON changes nothing.
    public sealed class GameBalanceDefinition
    {
        public long GuildRaidBossBaseHp { get; set; } = 1_000_000L;
        public int GuildRaidDpsPerLevel { get; set; } = 10;
        public int GuildRaidTickIntervalSeconds { get; set; } = 5;
        public long GuildRaidVictoryContributionPoints { get; set; } = 100L;
        public long GuildContributionEquipmentExpPerTier { get; set; } = 100L;
        public long GuildContributionGoldToExpDivisor { get; set; } = 10L;

        // Modul: previously a hardcoded switch statement inside
        // BillingVerificationEngine.ResolvePremiumDiamondsForProduct - moved
        // here so a price change is the same content-data deploy as every
        // other balance constant on this class, not a code deploy. Defaults
        // mirror the exact literals that switch statement used, so a
        // missing/absent config file changes nothing.
        public System.Collections.Generic.Dictionary<string, int> IapProductPrices { get; set; } = new System.Collections.Generic.Dictionary<string, int>
        {
            ["gems_pack_small"] = 500,
            ["gems_pack_medium"] = 1100,
            ["gems_pack_large"] = 2400,
            ["gems_pack_mega"] = 5200
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LootTableEntry
    {
        public int ItemId;
        public int Weight;

        // Modul: Architecture Overhaul, Part 3. Independent multi-drop
        // roller quantity bounds. Zero-valued on every entry authored
        // before this pass - consumers must treat MaxQuantity <= 0 as the
        // legacy "one unit per successful roll" behavior rather than
        // rolling an empty [0,0] range.
        public int MinQuantity;
        public int MaxQuantity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GatheringNodeDefinition
    {
        public int ActivityId;
        public int ProfessionType; // 0 = Woodcutting, 1 = Mining, 2 = Fishing, 3 = Herbalism
        public int BaseTickThreshold;
        public int BaseMasteryXpReward;
    }

    public static class ContentRegistry
    {
        public static string GetMaterialString(int id)
        {
            return id switch
            {
                1 => "copper_ore",
                2 => "raw_log",
                3 => "iron_ore",
                4 => "oak_log",
                5 => "gold_ore",
                6 => "magic_log",
                _ => "unknown"
            };
        }

        public static int GetMaterialId(string name)
        {
            return name switch
            {
                "copper_ore" => 1,
                "raw_log" => 2,
                "iron_ore" => 3,
                "oak_log" => 4,
                "gold_ore" => 5,
                "magic_log" => 6,
                _ => 0
            };
        }

        // Modul: metadata-driven profession classification for the raw
        // gathering material id space above (GetMaterialString/GetMaterialId) -
        // replaces GuildLogisticsEngine.ApplyMonolithProgressionAsync's
        // previous itemDefinitionId % 2 != 0 parity heuristic ("let's just
        // use odd IDs for ore, even for logs... for now"), which broke
        // silently the moment this id space was ever renumbered or
        // extended. Each material's profession is now an explicit,
        // authored fact rather than an inferred numeric coincidence. Zero
        // allocation - a switch expression over primitive int/enum values.
        public static GatheringProfessionType GetMaterialProfessionType(int materialId)
        {
            return materialId switch
            {
                1 => GatheringProfessionType.Mining,      // copper_ore
                2 => GatheringProfessionType.Woodcutting, // raw_log
                3 => GatheringProfessionType.Mining,      // iron_ore
                4 => GatheringProfessionType.Woodcutting, // oak_log
                5 => GatheringProfessionType.Mining,      // gold_ore
                6 => GatheringProfessionType.Woodcutting, // magic_log
                _ => GatheringProfessionType.Woodcutting  // unknown material - matches GetMaterialString's own "unknown" fallback
            };
        }

        private static string[] _monsterNames = Array.Empty<string>();
        private static string[] _monsterEnemyIds = Array.Empty<string>();
        private static string[] _itemBaseIds = Array.Empty<string>();

        private static MonsterDefinition[] _monsters = Array.Empty<MonsterDefinition>();
        private static GameBalanceDefinition _balance = new GameBalanceDefinition();

        // Modul: balancing constants formerly hardcoded as C# consts in
        // GuildRaidEngine/GuildContributionEngine, now sourced from
        // GameData/GameBalanceConfig.json so a tuning change is a content
        // deploy, not a code deploy. Defaults on GameBalanceDefinition
        // itself match the exact literals those engines used before
        // externalization, so a missing config file (or a missing field
        // within it) changes no behavior.
        public static GameBalanceDefinition Balance => _balance;

        // Modul: Production Release Hardening, Part 3. Keyed by the same
        // Key string localizations.json uses (matches client
        // LocalizationMatrix's LocalizationKey enum member names) mapped
        // to each of the four supported language codes. Server-side
        // exposure exists for content-QA/testability, not because any
        // gameplay logic reads localized text at runtime (nothing does -
        // this is client-rendering-only data); see TryGetLocalization for
        // the fallback-safe (default to "en", never throws) lookup this
        // whole registry exists to prove correct.
        private static Dictionary<string, LocalizationJson> _localizations = new Dictionary<string, LocalizationJson>();

        public static bool TryGetLocalization(string key, string languageCode, out string value)
        {
            if (!_localizations.TryGetValue(key, out LocalizationJson? entry))
            {
                value = string.Empty;
                return false;
            }

            string? resolved = languageCode switch
            {
                "en" => entry.En,
                "cs" => entry.Cs,
                "de" => entry.De,
                "pl" => entry.Pl,
                _ => entry.En
            };

            if (string.IsNullOrEmpty(resolved))
            {
                resolved = entry.En;
            }

            value = resolved ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }

        // Modul: gathering-yield loot data for the Fishing (ActivityId
        // 301-309) and Herbalism (401-412) gathering nodes added to close
        // the material acquisition loop for the Cooking and Alchemy
        // recipe chains (RecipeDefinition ProfessionType 4 and 5 below) -
        // every ItemId here is one of the specific items.json material ids
        // those recipes' Mat1Id/Mat2Id fields actually reference, verified
        // by direct cross-reference against the recipe list. Each table is
        // a single guaranteed entry (Weight is irrelevant with only one
        // candidate, kept at 100 for consistency with a normal weighted
        // table in case a second drop is ever added).
        //
        // Pre-existing monster (LootTableId 1-90) and Woodcutting/Mining
        // (101-105/201-205) loot tables remain intentionally untouched and
        // still resolve to an empty table via GetLootTable's dictionary
        // miss path below - populating those is a separate, larger
        // content-authoring gap outside this pass's scope (closing
        // specifically the Alchemy/Cooking loop), not a regression
        // introduced by this change.
        private static readonly LootTableEntry[] _lootEntries = new LootTableEntry[]
        {
            new LootTableEntry { ItemId = 11, Weight = 100 },  // index 0: coastline_cod_raw_fishing_material
            new LootTableEntry { ItemId = 30, Weight = 100 },  // index 1: deep_mire_eel_raw_fishing_material
            new LootTableEntry { ItemId = 48, Weight = 100 },  // index 2: canyon_catfish_raw_fishing_material
            new LootTableEntry { ItemId = 66, Weight = 100 },  // index 3: fjord_shark_raw_fishing_material
            new LootTableEntry { ItemId = 84, Weight = 100 },  // index 4: astral_whale_raw_fishing_material
            new LootTableEntry { ItemId = 102, Weight = 100 }, // index 5: river_trout_raw_fishing_material
            new LootTableEntry { ItemId = 120, Weight = 100 }, // index 6: mud_carp_raw_fishing_material
            new LootTableEntry { ItemId = 138, Weight = 100 }, // index 7: chasm_pike_raw_fishing_material
            new LootTableEntry { ItemId = 156, Weight = 100 }, // index 8: steppe_salmon_raw_fishing_material
            new LootTableEntry { ItemId = 5, Weight = 100 },   // index 9: salt_lotus_herbalism_material
            new LootTableEntry { ItemId = 9, Weight = 100 },   // index 10: condensation_essence_alchemy_material
            new LootTableEntry { ItemId = 14, Weight = 100 },  // index 11: peat_clump_rare_alchemy_ingredient
            new LootTableEntry { ItemId = 24, Weight = 100 },  // index 12: screaming_mandrake_herbalism_material
            new LootTableEntry { ItemId = 28, Weight = 100 },  // index 13: spore_pod_alchemy_material
            new LootTableEntry { ItemId = 31, Weight = 100 },  // index 14: heartwood_core_alchemy_material
            new LootTableEntry { ItemId = 33, Weight = 100 },  // index 15: schrat_horn_rare_alchemy_ingredient
            new LootTableEntry { ItemId = 42, Weight = 100 },  // index 16: jagged_bloodgrass_herbalism_material
            new LootTableEntry { ItemId = 49, Weight = 100 },  // index 17: gargoyle_heart_shard_alchemy_material
            new LootTableEntry { ItemId = 51, Weight = 100 },  // index 18: subterranean_sawdust_rare_alchemy_ingredient
            new LootTableEntry { ItemId = 60, Weight = 100 },  // index 19: frost_moonflower_herbalism_material
            new LootTableEntry { ItemId = 69, Weight = 100 },  // index 20: berserker_blood_essence_rare_alchemy_ingredient
            new LootTableEntry { ItemId = 129, Weight = 100 }, // index 21: coal_node_crafting_material - see Mining node 201 below

            // Modul: Full-Stack Expansion, Part 2. Monster material drop
            // tables for the 25 new regional monsters (monster/loot-table
            // ids 91-115) - the first populated MONSTER loot tables in the
            // codebase (ids 1-90 remain intentionally empty, see the
            // documented scope boundary above). One authored material per
            // monster; Weight carries the design drop rate in percent
            // (meaningful relative weight if these tables ever gain more
            // entries). Quantity ranges (1-3 etc.) are not representable
            // in this weight-only entry struct - each roll yields one
            // unit, the same semantics every gathering table above has.
            new LootTableEntry { ItemId = 250, Weight = 25, MinQuantity = 1, MaxQuantity = 3 },  // index 22: mat_mouse_fur (Field Mouse, LootTableId 501)
            new LootTableEntry { ItemId = 253, Weight = 20 },  // index 23: mat_rabbit_foot
            new LootTableEntry { ItemId = 256, Weight = 15 },  // index 24: mat_viper_venom
            new LootTableEntry { ItemId = 259, Weight = 20 },  // index 25: mat_boar_tusk
            new LootTableEntry { ItemId = 262, Weight = 100 }, // index 26: mat_wolf_essence
            new LootTableEntry { ItemId = 274, Weight = 25 },  // index 27: mat_sharp_thorn
            new LootTableEntry { ItemId = 277, Weight = 20 },  // index 28: mat_wolf_hide
            new LootTableEntry { ItemId = 280, Weight = 15 },  // index 29: mat_magic_bark
            new LootTableEntry { ItemId = 283, Weight = 20 },  // index 30: mat_bear_claw
            new LootTableEntry { ItemId = 286, Weight = 100 }, // index 31: mat_lynx_eye
            new LootTableEntry { ItemId = 298, Weight = 20 },  // index 32: mat_chitin_shell
            new LootTableEntry { ItemId = 301, Weight = 15 },  // index 33: mat_basilisk_scale
            new LootTableEntry { ItemId = 304, Weight = 20 },  // index 34: mat_flame_core
            new LootTableEntry { ItemId = 307, Weight = 15, MinQuantity = 2, MaxQuantity = 3 },  // index 35: mat_lodestone (Sandstone Golem, LootTableId 514)
            new LootTableEntry { ItemId = 310, Weight = 100 }, // index 36: mat_lava_heart
            new LootTableEntry { ItemId = 323, Weight = 20 },  // index 37: mat_frozen_wing
            new LootTableEntry { ItemId = 326, Weight = 15 },  // index 38: mat_yeti_pelt
            new LootTableEntry { ItemId = 329, Weight = 15 },  // index 39: mat_spectral_ice
            new LootTableEntry { ItemId = 332, Weight = 20 },  // index 40: mat_rime_crystal
            new LootTableEntry { ItemId = 335, Weight = 100 }, // index 41: mat_eternal_ice
            new LootTableEntry { ItemId = 347, Weight = 20 },  // index 42: mat_plague_flesh
            new LootTableEntry { ItemId = 350, Weight = 15 },  // index 43: mat_gargoyle_stone
            new LootTableEntry { ItemId = 353, Weight = 15 },  // index 44: mat_necrotic_core
            new LootTableEntry { ItemId = 356, Weight = 20 },  // index 45: mat_broken_blade
            new LootTableEntry { ItemId = 359, Weight = 100 }, // index 46: mat_demon_heart

            // Modul: gathering loot tables. Woodcutting (101-105) and Mining
            // (202-205) were the last empty gathering tables in the game - node
            // 201 held one hand-placed coal entry so Cooking's second material
            // was obtainable, and everything else dropped literally nothing.
            // A player could chop trees for hours and receive only mastery XP,
            // which meant all ten Smelting recipes, and through them every
            // Equipment-assembly recipe, were unreachable: the whole gear
            // progression had no entry point.
            //
            // Two axes of design here. Each node spans a small band of adjacent
            // material tiers rather than one material apiece, so a node stays
            // worth visiting as the player levels past its floor; and each
            // band's top material is rarer than its floor material, so
            // progression comes from moving up nodes rather than grinding the
            // first one. Weights are relative within a table.
            //
            // Woodcutting: ten tree types across five nodes, overlapping by one
            // so consecutive nodes share a material.
            new LootTableEntry { ItemId = 99, Weight = 55, MinQuantity = 1, MaxQuantity = 3 },  // index 47: oak_logs        (node 101)
            new LootTableEntry { ItemId = 174, Weight = 30, MinQuantity = 1, MaxQuantity = 2 }, // index 48: beech_logs_raw  (node 101)
            new LootTableEntry { ItemId = 117, Weight = 15 },                                   // index 49: willow_logs     (node 101)

            new LootTableEntry { ItemId = 117, Weight = 50, MinQuantity = 1, MaxQuantity = 3 }, // index 50: willow_logs     (node 102)
            new LootTableEntry { ItemId = 153, Weight = 33, MinQuantity = 1, MaxQuantity = 2 }, // index 51: birch_trees     (node 102)
            new LootTableEntry { ItemId = 135, Weight = 17 },                                   // index 52: pine_trees      (node 102)

            new LootTableEntry { ItemId = 135, Weight = 50, MinQuantity = 1, MaxQuantity = 3 }, // index 53: pine_trees      (node 103)
            new LootTableEntry { ItemId = 8, Weight = 33, MinQuantity = 1, MaxQuantity = 2 },   // index 54: maple_trees     (node 103)
            new LootTableEntry { ItemId = 27, Weight = 17 },                                    // index 55: yew_trees       (node 103)

            new LootTableEntry { ItemId = 27, Weight = 50, MinQuantity = 1, MaxQuantity = 3 },  // index 56: yew_trees       (node 104)
            new LootTableEntry { ItemId = 45, Weight = 33, MinQuantity = 1, MaxQuantity = 2 },  // index 57: elder_trees     (node 104)
            new LootTableEntry { ItemId = 63, Weight = 17 },                                    // index 58: ancient_wood    (node 104)

            new LootTableEntry { ItemId = 63, Weight = 62, MinQuantity = 1, MaxQuantity = 3 },  // index 59: ancient_wood    (node 105)
            new LootTableEntry { ItemId = 81, Weight = 38, MinQuantity = 1, MaxQuantity = 2 },  // index 60: yggdrasil_burl  (node 105)

            // Mining: the nine ores the ten Smelting recipes consume, plus coal
            // on the first three nodes. Coal is the single most demanded
            // material in the game - Mat2Id on every Smelting AND every Cooking
            // recipe - so it stays common and available early rather than being
            // gated behind a high-tier node.
            new LootTableEntry { ItemId = 165, Weight = 45, MinQuantity = 1, MaxQuantity = 3 }, // index 61: copper_ore      (node 201)
            new LootTableEntry { ItemId = 129, Weight = 35, MinQuantity = 1, MaxQuantity = 2 }, // index 62: coal_node       (node 201)
            new LootTableEntry { ItemId = 93, Weight = 20 },                                    // index 63: tin_ore         (node 201)

            new LootTableEntry { ItemId = 93, Weight = 45, MinQuantity = 1, MaxQuantity = 3 },  // index 64: tin_ore         (node 202)
            new LootTableEntry { ItemId = 129, Weight = 33, MinQuantity = 1, MaxQuantity = 2 }, // index 65: coal_node       (node 202)
            new LootTableEntry { ItemId = 111, Weight = 22 },                                   // index 66: iron_ore        (node 202)

            new LootTableEntry { ItemId = 111, Weight = 45, MinQuantity = 1, MaxQuantity = 3 }, // index 67: iron_ore        (node 203)
            new LootTableEntry { ItemId = 129, Weight = 30, MinQuantity = 1, MaxQuantity = 2 }, // index 68: coal_node       (node 203)
            new LootTableEntry { ItemId = 147, Weight = 25 },                                   // index 69: silver_ore      (node 203)

            new LootTableEntry { ItemId = 147, Weight = 45, MinQuantity = 1, MaxQuantity = 3 }, // index 70: silver_ore      (node 204)
            new LootTableEntry { ItemId = 1, Weight = 35, MinQuantity = 1, MaxQuantity = 2 },   // index 71: gold_ore        (node 204)
            new LootTableEntry { ItemId = 21, Weight = 20 },                                    // index 72: mithril_ore     (node 204)

            // The endgame node carries all four top ores. Adamantite, obsidian
            // and celestial appear nowhere else, so this is the only source for
            // the last three Smelting recipes.
            new LootTableEntry { ItemId = 21, Weight = 34, MinQuantity = 1, MaxQuantity = 3 },  // index 73: mithril_ore     (node 205)
            new LootTableEntry { ItemId = 39, Weight = 28, MinQuantity = 1, MaxQuantity = 2 },  // index 74: adamantite_ore  (node 205)
            new LootTableEntry { ItemId = 57, Weight = 22, MinQuantity = 1, MaxQuantity = 2 },  // index 75: obsidian_ore    (node 205)
            new LootTableEntry { ItemId = 75, Weight = 16 },                                    // index 76: celestial_ore   (node 205)

            // Modul: gathering drops the CANON materials.
            //
            // The tables used to point at invented items - "oak logs",
            // "river trout", nine herbs - none of which the art, the
            // design list or the item catalogue agree exist. Every entry
            // below is an item that was already authored and had a picture
            // drawn for it, in the location that picture belongs to.
            //
            // Two per node: the common at 90% and the rare at 10%.
            // --- Woodcutting ---
            new LootTableEntry { ItemId = 267, Weight = 90 },  // index 77: birch_log - Sunlit Plains (Woodcutting, common)
            new LootTableEntry { ItemId = 269, Weight = 10 },  // index 78: golden_birch_log - Sunlit Plains (Woodcutting, rare)
            new LootTableEntry { ItemId = 291, Weight = 90 },  // index 79: willow_log - Whispering Woods (Woodcutting, common)
            new LootTableEntry { ItemId = 401, Weight = 10 },  // index 80: golden_willow_log - Whispering Woods (Woodcutting, rare)
            new LootTableEntry { ItemId = 315, Weight = 90 },  // index 81: acacia_log - Scorched Wasteland (Woodcutting, common)
            new LootTableEntry { ItemId = 402, Weight = 10 },  // index 82: golden_acacia_log - Scorched Wasteland (Woodcutting, rare)
            new LootTableEntry { ItemId = 340, Weight = 90 },  // index 83: frostpine_log - Frozen Peaks (Woodcutting, common)
            new LootTableEntry { ItemId = 403, Weight = 10 },  // index 84: golden_frostpine_log - Frozen Peaks (Woodcutting, rare)
            new LootTableEntry { ItemId = 364, Weight = 90 },  // index 85: ebon_log - Shadow Citadel (Woodcutting, common)
            new LootTableEntry { ItemId = 404, Weight = 10 },  // index 86: golden_ebon_log - Shadow Citadel (Woodcutting, rare)
            // --- Mining ---
            new LootTableEntry { ItemId = 165, Weight = 90 },  // index 87: copper_ore - Sunlit Plains (Mining, common)
            new LootTableEntry { ItemId = 271, Weight = 10 },  // index 88: malachite_ore - Sunlit Plains (Mining, rare)
            new LootTableEntry { ItemId = 111, Weight = 90 },  // index 89: iron_ore - Whispering Woods (Mining, common)
            new LootTableEntry { ItemId = 295, Weight = 10 },  // index 90: hematite_ore - Whispering Woods (Mining, rare)
            new LootTableEntry { ItemId = 319, Weight = 90 },  // index 91: sulfur_ore - Scorched Wasteland (Mining, common)
            new LootTableEntry { ItemId = 57, Weight = 10 },  // index 92: obsidian_ore - Scorched Wasteland (Mining, rare)
            new LootTableEntry { ItemId = 147, Weight = 90 },  // index 93: silver_ore - Frozen Peaks (Mining, common)
            new LootTableEntry { ItemId = 344, Weight = 10 },  // index 94: cobalt_ore - Frozen Peaks (Mining, rare)
            new LootTableEntry { ItemId = 368, Weight = 90 },  // index 95: darksteel_ore - Shadow Citadel (Mining, common)
            new LootTableEntry { ItemId = 369, Weight = 10 },  // index 96: astralite_ore - Shadow Citadel (Mining, rare)
            // --- Fishing ---
            new LootTableEntry { ItemId = 272, Weight = 90 },  // index 97: sunlit_perch - Sunlit Plains (Fishing, common)
            new LootTableEntry { ItemId = 273, Weight = 10 },  // index 98: shimmering_trout - Sunlit Plains (Fishing, rare)
            new LootTableEntry { ItemId = 296, Weight = 90 },  // index 99: moss_bass - Whispering Woods (Fishing, common)
            new LootTableEntry { ItemId = 297, Weight = 10 },  // index 100: ancient_eel - Whispering Woods (Fishing, rare)
            new LootTableEntry { ItemId = 321, Weight = 90 },  // index 101: lava_carp - Scorched Wasteland (Fishing, common)
            new LootTableEntry { ItemId = 322, Weight = 10 },  // index 102: hellfire_salmon - Scorched Wasteland (Fishing, rare)
            new LootTableEntry { ItemId = 345, Weight = 90 },  // index 103: frost_cod - Frozen Peaks (Fishing, common)
            new LootTableEntry { ItemId = 346, Weight = 10 },  // index 104: glacier_halibut - Frozen Peaks (Fishing, rare)
            new LootTableEntry { ItemId = 370, Weight = 90 },  // index 105: void_ray - Shadow Citadel (Fishing, common)
            new LootTableEntry { ItemId = 371, Weight = 10 },  // index 106: spectral_lanternfish - Shadow Citadel (Fishing, rare)
        };

        // Modul: LootTableId -> (Start, Count) into _lootEntries, keyed by
        // Dictionary rather than a fixed-size array indexed by
        // LootTableId-1. LootTableId spans both monster ids (1-90) and
        // gathering ActivityIds (101-412), a sparse range a dense array
        // would need hundreds of mostly-empty slots to cover safely - the
        // previous array was in fact sized for only 60 entries, silently
        // stale relative to the 90 monsters currently authored. A
        // dictionary miss (any LootTableId with no entry here) returns an
        // empty table via GetLootTable, identical in effect to the old
        // array's (0, 0) default for every id not listed below - this is a
        // representation change, not a behavior change, for every existing
        // monster and Woodcutting/Mining LootTableId.
        // Modul: activity id bands. The gathering keys in this table moved with
        // the nodes themselves - Woodcutting 101-105 became 1001-1005, Mining
        // 201-205 became 2001-2005, Fishing 301-309 became 3001-3009 and
        // Herbalism 401-412 became 4001-4012. Monster LootTableIds (1-90 legacy,
        // 501-525 canonical) are untouched; only the gathering half of this
        // shared key space moved. See ActivityIdBands for why.
        private static readonly Dictionary<int, (int Start, int Count)> _lootSegments = new()
        {
            // Modul: every single Cooking recipe (ProfessionType 4) also
            // requires Mat2Id = 129 (coal_node_crafting_material) - a
            // Mining-sourced item, not a fishing one. Mining node 201's
            // loot table (previously empty, like every other Woodcutting/
            // Mining node) gets exactly this one entry so Cooking's second
            // material is actually obtainable; the other four Mining nodes
            // and all five Woodcutting nodes remain untouched/empty,
            // matching the deliberate scope boundary documented above -
            // this is the minimum addition needed to satisfy the "fully
            // close the loop for Cooking" requirement, not a general
            // Woodcutting/Mining loot-table fix.
            // Modul: gathering loot tables. Node 201 previously held index 21
            // alone - a single coal entry hand-placed so Cooking's second
            // material was obtainable at all. It now has a real three-material
            // table like the rest, and index 21 is left in place rather than
            // removed so the indices of every entry authored after it stay
            // stable.
            { 1001, (77, 2) }, // Sunlit Plains
            { 1002, (79, 2) }, // Whispering Woods
            { 1003, (81, 2) }, // Scorched Wasteland
            { 1004, (83, 2) }, // Frozen Peaks
            { 1005, (85, 2) }, // Shadow Citadel
            { 2001, (87, 2) }, // Sunlit Plains
            { 2002, (89, 2) }, // Whispering Woods
            { 2003, (91, 2) }, // Scorched Wasteland
            { 2004, (93, 2) }, // Frozen Peaks
            { 2005, (95, 2) }, // Shadow Citadel
            { 3001, (97, 2) }, // Sunlit Plains
            { 3002, (99, 2) }, // Whispering Woods
            { 3003, (101, 2) }, // Scorched Wasteland
            { 3004, (103, 2) }, // Frozen Peaks
            { 3005, (105, 2) }, // Shadow Citadel

            // Modul: Full-Stack Expansion, Part 2 - monster loot tables
            // for the 25 new regional monsters (monster ids 91-115). The
            // LootTableId keys live in a dedicated 501-525 range rather
            // than reusing the monster ids: this dictionary's key space is
            // shared between monster LootTableIds and gathering
            // ActivityIds, and gathering nodes already occupy 101-105 and
            // 201-205 - keying the crab/basilisk/ember/golem/wyrm tables
            // by their monster ids (101-105) would have made every
            // Woodcutting node at those SAME activity ids start rolling
            // monster materials. Known remaining limitation, documented,
            // not fixed here: ChangeActivity routes 101-105 to the
            // gathering nodes first (TryGetGatheringNode wins), so
            // monsters 101-105 cannot currently be ENTERED via a plain
            // activity id - untangling the shared activity/monster id
            // space is an activity-routing redesign beyond this content
            // pass.
            { 501, (22, 1) },
            { 502, (23, 1) },
            { 503, (24, 1) },
            { 504, (25, 1) },
            { 505, (26, 1) },
            { 506, (27, 1) },
            { 507, (28, 1) },
            { 508, (29, 1) },
            { 509, (30, 1) },
            { 510, (31, 1) },
            { 511, (32, 1) },
            { 512, (33, 1) },
            { 513, (34, 1) },
            { 514, (35, 1) },
            { 515, (36, 1) },
            { 516, (37, 1) },
            { 517, (38, 1) },
            { 518, (39, 1) },
            { 519, (40, 1) },
            { 520, (41, 1) },
            { 521, (42, 1) },
            { 522, (43, 1) },
            { 523, (44, 1) },
            { 524, (45, 1) },
            { 525, (46, 1) },
        };

        private static ItemDefinition[] _itemDefinitions = Array.Empty<ItemDefinition>();

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct RecipeDefinition
        {
            public int ResultItemId;
            public int ProfessionType;
            public int RequiredLevel;
            public int Mat1Id;
            public int Mat1Count;
            public int Mat2Id;
            public int Mat2Count;
            public int CraftingTimeMs;
        }

        private static readonly RecipeDefinition[] _recipes = new RecipeDefinition[]
        {
            // Modul: CRAFTING IS TOOLS.
            //
            // The table used to hold ten Smelting, ten Cooking and twenty
            // Alchemy recipes plus sixty-three equipment ones, built on
            // materials the game does not contain - tin, coal, mithril,
            // adamantite and celestial ore for the bars; nine invented fish
            // for the meals; a dozen herbs and essences for the potions. After
            // gathering was repointed at the canon, NOT ONE of the hundred and
            // three recipes could be fulfilled: every input resolved to an
            // item that drops nowhere. The crafting screen listed them anyway.
            //
            // These are what the five locations can actually feed. Every tool
            // has had a picture drawn for it since the art pass and no item
            // behind it until now: three kinds, five tiers, and a common and a
            // rare wood at each - which is exactly the thirty files in
            // Tools&Equipment.
            //
            // Equipment is a DROP, not a craft, so it is not here.
            //
            // Modul: COSTS NOW SCALE WITH THE TIER. Every one of these thirty
            // recipes used to cost 8 + 4 units, so the entire ten-tier ladder
            // was 360 units of material - for the whole game. Against a season
            // curve where region 5 is hundreds of hours of combat, gathering
            // was 0.06% of the time spent, which is not a profession, it is a
            // formality on the way to one.
            //
            // Sized from the pacing model rather than guessed: each region's
            // full set costs roughly a tenth of that region's combat time at
            // that region's node threshold, with the rare wood at twice the
            // common one. Fishing carries the other half of the gathering
            // budget through the larder, which is why the target here is a
            // tenth and not a fifth.
            //
            // This is the loop the tools exist for: material buys a better
            // tool, a better tool gathers faster, and the time saved goes back
            // into combat. Test_Gathering_ShareOfPlaytimeStaysInBand keeps it
            // honest - it computes both halves against the real registries and
            // fails if gathering stops mattering or starts dominating.
            new RecipeDefinition { ResultItemId = 408, ProfessionType = 3, RequiredLevel = 1, Mat1Id = 267, Mat1Count = 20, Mat2Id = 165, Mat2Count = 10, CraftingTimeMs = 5000 }, // birch_axe_tool
            new RecipeDefinition { ResultItemId = 409, ProfessionType = 3, RequiredLevel = 1, Mat1Id = 269, Mat1Count = 40, Mat2Id = 271, Mat2Count = 20, CraftingTimeMs = 5000 }, // golden_birch_axe_tool
            new RecipeDefinition { ResultItemId = 410, ProfessionType = 3, RequiredLevel = 1, Mat1Id = 267, Mat1Count = 20, Mat2Id = 165, Mat2Count = 10, CraftingTimeMs = 5000 }, // birch_pickaxe_tool
            new RecipeDefinition { ResultItemId = 411, ProfessionType = 3, RequiredLevel = 1, Mat1Id = 269, Mat1Count = 40, Mat2Id = 271, Mat2Count = 20, CraftingTimeMs = 5000 }, // golden_birch_pickaxe_tool
            new RecipeDefinition { ResultItemId = 412, ProfessionType = 3, RequiredLevel = 1, Mat1Id = 267, Mat1Count = 20, Mat2Id = 165, Mat2Count = 10, CraftingTimeMs = 5000 }, // birch_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 413, ProfessionType = 3, RequiredLevel = 1, Mat1Id = 269, Mat1Count = 40, Mat2Id = 271, Mat2Count = 20, CraftingTimeMs = 5000 }, // golden_birch_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 414, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 291, Mat1Count = 136, Mat2Id = 111, Mat2Count = 68, CraftingTimeMs = 10000 }, // willow_axe_tool
            new RecipeDefinition { ResultItemId = 415, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 401, Mat1Count = 272, Mat2Id = 295, Mat2Count = 136, CraftingTimeMs = 10000 }, // whisper_willow_axe_tool
            new RecipeDefinition { ResultItemId = 416, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 291, Mat1Count = 136, Mat2Id = 111, Mat2Count = 68, CraftingTimeMs = 10000 }, // willow_pickaxe_tool
            new RecipeDefinition { ResultItemId = 417, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 401, Mat1Count = 272, Mat2Id = 295, Mat2Count = 136, CraftingTimeMs = 10000 }, // whisper_willow_pickaxe_tool
            new RecipeDefinition { ResultItemId = 418, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 291, Mat1Count = 136, Mat2Id = 111, Mat2Count = 68, CraftingTimeMs = 10000 }, // willow_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 419, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 401, Mat1Count = 272, Mat2Id = 295, Mat2Count = 136, CraftingTimeMs = 10000 }, // whisper_willow_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 420, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 315, Mat1Count = 613, Mat2Id = 319, Mat2Count = 307, CraftingTimeMs = 15000 }, // acacia_axe_tool
            new RecipeDefinition { ResultItemId = 421, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 402, Mat1Count = 1226, Mat2Id = 57, Mat2Count = 613, CraftingTimeMs = 15000 }, // ironwood_axe_tool
            new RecipeDefinition { ResultItemId = 422, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 315, Mat1Count = 613, Mat2Id = 319, Mat2Count = 307, CraftingTimeMs = 15000 }, // acacia_pickaxe_tool
            new RecipeDefinition { ResultItemId = 423, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 402, Mat1Count = 1226, Mat2Id = 57, Mat2Count = 613, CraftingTimeMs = 15000 }, // ironwood_pickaxe_tool
            new RecipeDefinition { ResultItemId = 424, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 315, Mat1Count = 613, Mat2Id = 319, Mat2Count = 307, CraftingTimeMs = 15000 }, // acacia_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 425, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 402, Mat1Count = 1226, Mat2Id = 57, Mat2Count = 613, CraftingTimeMs = 15000 }, // ironwood_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 426, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 340, Mat1Count = 4355, Mat2Id = 147, Mat2Count = 2177, CraftingTimeMs = 20000 }, // frostpine_axe_tool
            new RecipeDefinition { ResultItemId = 427, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 403, Mat1Count = 8710, Mat2Id = 344, Mat2Count = 4355, CraftingTimeMs = 20000 }, // glacier_pine_axe_tool
            new RecipeDefinition { ResultItemId = 428, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 340, Mat1Count = 4355, Mat2Id = 147, Mat2Count = 2177, CraftingTimeMs = 20000 }, // frostpine_pickaxe_tool
            new RecipeDefinition { ResultItemId = 429, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 403, Mat1Count = 8710, Mat2Id = 344, Mat2Count = 4355, CraftingTimeMs = 20000 }, // glacier_pine_pickaxe_tool
            new RecipeDefinition { ResultItemId = 430, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 340, Mat1Count = 4355, Mat2Id = 147, Mat2Count = 2177, CraftingTimeMs = 20000 }, // frostpine_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 431, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 403, Mat1Count = 8710, Mat2Id = 344, Mat2Count = 4355, CraftingTimeMs = 20000 }, // glacier_pine_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 432, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 364, Mat1Count = 23287, Mat2Id = 368, Mat2Count = 11644, CraftingTimeMs = 25000 }, // ebon_axe_tool
            new RecipeDefinition { ResultItemId = 433, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 404, Mat1Count = 46575, Mat2Id = 369, Mat2Count = 23287, CraftingTimeMs = 25000 }, // voidbark_axe_tool
            new RecipeDefinition { ResultItemId = 434, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 364, Mat1Count = 23287, Mat2Id = 368, Mat2Count = 11644, CraftingTimeMs = 25000 }, // ebon_pickaxe_tool
            new RecipeDefinition { ResultItemId = 435, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 404, Mat1Count = 46575, Mat2Id = 369, Mat2Count = 23287, CraftingTimeMs = 25000 }, // voidbark_pickaxe_tool
            new RecipeDefinition { ResultItemId = 436, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 364, Mat1Count = 23287, Mat2Id = 368, Mat2Count = 11644, CraftingTimeMs = 25000 }, // ebon_fishing_rod_tool
            new RecipeDefinition { ResultItemId = 437, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 404, Mat1Count = 46575, Mat2Id = 369, Mat2Count = 23287, CraftingTimeMs = 25000 }, // voidbark_fishing_rod_tool
        
        };
        public static ReadOnlySpan<RecipeDefinition> Recipes => _recipes;

        // Modul: crafting as an assignable job. Maps a crafting-band activity
        // id back to the recipe it names. The index is the identity here, so
        // reordering _recipes would reassign every character mid-craft - which
        // is why this is the only place that converts between the two and why
        // the band comment says "index", loudly.
        public static bool TryGetRecipeByActivityId(long activityId, out RecipeDefinition recipe)
        {
            if (!ActivityIdBands.IsCraftingActivity(activityId))
            {
                recipe = default;
                return false;
            }

            long index = activityId - ActivityIdBands.CraftingBand;
            if (index < 0 || index >= _recipes.Length)
            {
                recipe = default;
                return false;
            }

            recipe = _recipes[index];
            return true;
        }

        public static long GetActivityIdForRecipeIndex(int recipeIndex)
        {
            return ActivityIdBands.CraftingBand + recipeIndex;
        }

        public static bool TryGetRecipe(int resultItemId, out RecipeDefinition recipe)
        {
            for (int i = 0; i < _recipes.Length; i++)
            {
                if (_recipes[i].ResultItemId == resultItemId)
                {
                    recipe = _recipes[i];
                    return true;
                }
            }
            recipe = default;
            return false;
        }

        public static ReadOnlySpan<ItemDefinition> ItemDefinitions => _itemDefinitions;

        public static ReadOnlySpan<MonsterDefinition> Monsters => _monsters;
        public static string GetMonsterName(int id) => _monsterNames[id - 1];
        public static string GetMonsterEnemyId(int id) => _monsterEnemyIds[id - 1];
        /// <summary>
        /// This item's BaseId, or an empty string for an id that names nothing.
        ///
        /// Bounds-checked because the catalogue has HOLES now - see
        /// Initialize. An id can point at a removed item (a legacy piece, an
        /// invented offhand) and still be sitting in an old database row, an
        /// old loot table or an old client packet. Returning "" lets every
        /// caller's existing "is this a real item" string test answer
        /// correctly, where an unchecked index threw.
        /// </summary>
        public static string GetItemBaseId(int itemId)
        {
            if (itemId < 1 || itemId > _itemBaseIds.Length) return string.Empty;
            return _itemBaseIds[itemId - 1];
        }

        /// <summary>Whether this id names an item that still exists.</summary>
        public static bool ItemExists(int itemId) => GetItemBaseId(itemId).Length > 0;

        /// <summary>
        /// Which of the five locations this item's gear belongs to, or 0 when
        /// the slug names nothing.
        ///
        /// The market's tier filter needs this and only has a BaseItemId to go
        /// on - an order row carries the slug, not the numeric id. Note this is
        /// RegionTier, the LOCATION, not QualityTier, the 14-step rarity of an
        /// individual roll. Both get called "tier" in conversation and they are
        /// different axes: a player asking for tier 3 gear means the Scorched
        /// Wasteland set, not a Rare.
        ///
        /// Linear over a bounded static table, on a browse request rather than
        /// a tick.
        /// </summary>
        public static int GetRegionTierForBaseId(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return 0;

            for (int i = 0; i < _itemBaseIds.Length; i++)
            {
                if (string.Equals(_itemBaseIds[i], baseItemId, StringComparison.Ordinal))
                {
                    return _itemDefinitions[i].RegionTier;
                }
            }

            return 0;
        }

        // Modul: single source of truth for "which difficulty region does
        // this monster belong to" - replaces the ((Id - 1) % 30) / 6 + 1
        // arithmetic convention duplicated across NetworkBroadcastSystem,
        // CombatLootEngine, CodexEngine, OfflineSimulationEngine,
        // StateCheckpointManager, and SimulationEngine, which silently
        // wrapped monster ids 31+ back onto tiers 1-5 regardless of their
        // actual stats. Monsters authored with a RegionTier in content data
        // use it directly; a RegionTier of 0 (unauthored/legacy content)
        // falls back to the old formula so stale data degrades instead of
        // producing a tier of 0.
        public static int GetMonsterRegionTier(int monsterId)
        {
            if (monsterId < 1 || monsterId > _monsters.Length)
            {
                return 1;
            }

            int authored = _monsters[monsterId - 1].RegionTier;
            return authored > 0 ? authored : ((monsterId - 1) % 30) / 6 + 1;
        }

        /// <summary>The first of the 25 canonical monsters. Ids 1-90 are legacy.</summary>
        public const int FirstCanonicalMonsterId = 91;

        /// <summary>The last canonical monster.</summary>
        public const int LastCanonicalMonsterId = 115;

        /// <summary>Four ordinary monsters and a boss, five times over.</summary>
        public const int MonstersPerRegion = 5;

        /// <summary>
        /// Whether a monster is the boss of its region - the FIFTH of each
        /// group of five, so ids 95, 100, 105, 110 and 115.
        ///
        /// THIS REPLACES `monsterId % 6 == 0`, WHICH WAS WRONG IN BOTH
        /// DIRECTIONS AND DROVE THREE SEPARATE REWARDS.
        ///
        /// None of the five real bosses is divisible by six, so no boss ever
        /// paid a boss reward. Meanwhile 96, 102, 108 and 114 - four perfectly
        /// ordinary monsters - matched it, and every kill of one of them
        /// granted a guaranteed armour drop, 500 Guild War points instead of
        /// 10, and TEN PREMIUM DIAMONDS. Thorny Vine is 96, and at the measured
        /// kill rate that is roughly twenty thousand diamonds an hour of free
        /// premium currency.
        ///
        /// The old heuristic was copied between call sites with a comment
        /// saying it kept the meaning consistent everywhere, which is exactly
        /// how one wrong idea reached three rewards. There is now one function
        /// and the call sites ask it.
        ///
        /// Legacy monsters 1-90 are not part of any region and are never
        /// bosses; the old rule made every sixth one of those a boss too.
        /// </summary>
        // Modul: THE FIVE LOCATIONS HAVE NAMES.
        //
        // Every screen said "Region 1".."Region 5" and gathering said "tier 1"
        // .."tier 5", so the same place had two anonymous numbers and neither
        // matched the art, the monsters or the loot. These are the canon names
        // and this is the only place they are written down server-side.
        public static readonly string[] LocationNames =
        {
            "Sunlit Plains",
            "Whispering Woods",
            "Scorched Wasteland",
            "Frozen Peaks",
            "Shadow Citadel",
        };

        public const int LocationCount = 5;

        public static string GetLocationName(int locationIndex)
        {
            if (locationIndex < 1 || locationIndex > LocationCount)
            {
                return $"Location {locationIndex}";
            }

            return LocationNames[locationIndex - 1];
        }

        // Which of the five locations a canonical monster belongs to, 1-5.
        // Zero for the 90 legacy monsters, which belong to none of them.
        //
        // NOT GetMonsterRegionTier: that reads an authored RegionTier which
        // runs 1-10 and is a difficulty band, not a place - see the codex
        // endpoint reporting ten regions for a world that has five.
        public static int GetCanonicalLocation(int monsterId)
        {
            if (monsterId < FirstCanonicalMonsterId || monsterId > LastCanonicalMonsterId)
            {
                return 0;
            }

            return (monsterId - FirstCanonicalMonsterId) / MonstersPerRegion + 1;
        }

        // Which location a gathering node sits in, 1-5. The node id is
        // band + location, so this is the last digit and nothing more.
        public static int GetNodeLocation(long activityId)
        {
            if (!ActivityIdBands.IsGatheringActivity(activityId))
            {
                return 0;
            }

            int location = (int)(activityId % ActivityIdBands.BandSize);
            return location >= 1 && location <= LocationCount ? location : 0;
        }

        // Modul: YOU CAN EAT WHAT YOU CATCH.
        //
        // Food used to be identified by a "_food" marker in the BaseId, which
        // no raw fish carries - so a player could fish all day, watch the fish
        // land in the chest, and be told by the larder that they had no food.
        // The only edible things in the game were cooked, and cooking is a
        // profession the design list does not have.
        //
        // Derived from the fishing loot tables rather than a hand-written id
        // list: the rule is "anything a fishing node drops", so a new fish is
        // edible the moment it is authored, and a list cannot go stale the way
        // AlchemyCompendium's seven legacy ids did.
        // Modul: LAZY, not a static field initializer.
        //
        // Static initializers run in textual order, and this sits above both
        // _lootEntries and _lootSegments - so an eager version read empty
        // tables and produced a set of garbage ids that then indexed past the
        // end of ItemDefinitions. Deferring it means the first caller gets a
        // fully-loaded registry no matter where this line lives in the file.
        private static readonly System.Lazy<System.Collections.Generic.HashSet<int>> _rawFishItemIds =
            new(BuildRawFishSet, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        private static System.Collections.Generic.HashSet<int> BuildRawFishSet()
        {
            var fish = new System.Collections.Generic.HashSet<int>();
            for (int location = 1; location <= LocationCount; location++)
            {
                long nodeId = ActivityIdBands.FishingBand + location;
                foreach (var entry in GetLootTable((int)nodeId))
                {
                    fish.Add(entry.ItemId);
                }
            }

            return fish;
        }

        public static bool IsRawFish(int itemId) => _rawFishItemIds.Value.Contains(itemId);

        public static System.Collections.Generic.IReadOnlyCollection<int> RawFishItemIds => _rawFishItemIds.Value;

        // Modul: WHICH TOOL, AND HOW GOOD.
        //
        // CachedCurrentToolTier was set from the FORGE BUILDING LEVEL - so a
        // player who crafted a Void Bark Axe gathered at exactly the speed of
        // someone holding nothing, and the whole tool tier table in
        // GatheringToolEngine was driven by a building instead of by tools.
        //
        // The ten tiers are the ten woods, in the order the design list gives
        // them: birch, golden birch, willow, whisper willow, acacia, ironwood,
        // frostpine, glacier pine, ebon, void bark. Resolved from the BaseId
        // rather than from an id range so authoring a tool cannot silently
        // land it at tier 0.
        private static readonly string[] ToolWoodsByTier =
        {
            "birch_", "golden_birch_", "willow_", "whisper_willow_",
            "acacia_", "ironwood_", "frostpine_", "glacier_pine_",
            "ebon_", "voidbark_",
        };

        public const int ToolKindAxe = 0;
        public const int ToolKindPickaxe = 1;
        public const int ToolKindRod = 2;

        /// <summary>Which profession a tool serves, or -1 if it is not a tool.</summary>
        public static int GetToolKind(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId) || !baseItemId.EndsWith("_tool", StringComparison.Ordinal))
            {
                return -1;
            }

            if (baseItemId.Contains("_pickaxe_", StringComparison.Ordinal)) return ToolKindPickaxe;
            if (baseItemId.Contains("_fishing_rod_", StringComparison.Ordinal)) return ToolKindRod;
            if (baseItemId.Contains("_axe_", StringComparison.Ordinal)) return ToolKindAxe;
            return -1;
        }

        /// <summary>1-10 by the tool's wood, or 0 for the starter and non-tools.</summary>
        public static int GetToolTier(string baseItemId)
        {
            if (GetToolKind(baseItemId) < 0)
            {
                return 0;
            }

            // Longest prefix first: "golden_birch_" also starts with nothing
            // else, but "birch_" is a prefix of nothing while "willow_" IS a
            // suffix-match risk inside "whisper_willow_". Matching on the
            // leading token avoids both.
            for (int tier = ToolWoodsByTier.Length; tier >= 1; tier--)
            {
                if (baseItemId.StartsWith(ToolWoodsByTier[tier - 1], StringComparison.Ordinal))
                {
                    return tier;
                }
            }

            return 0;
        }

        public static bool IsRegionalBoss(int monsterId)
        {
            if (monsterId < FirstCanonicalMonsterId || monsterId > LastCanonicalMonsterId)
            {
                return false;
            }

            return (monsterId - FirstCanonicalMonsterId + 1) % MonstersPerRegion == 0;
        }

        // Modul: infinite endgame scaling - authored content currently only
        // defines RegionTier 1-10, so a player who out-levels the highest
        // authored region previously hit a hard content wall (RegionTier
        // never exceeds what content authors have manually placed).
        // Procedural endgame zones (RegionTier > 10) instead multiply the
        // monster's base MaxHp/AttackPower by 1.25^(RegionTier - 10),
        // compounding per tier past 10 so difficulty keeps climbing
        // indefinitely without requiring new authored content. Tiers 1-10
        // are unaffected (multiplier of exactly 1.0) - this only ever
        // scales UP, never down, so no existing authored balance changes.
        // A pure double computation over primitive ints - no heap
        // allocation, safe to call from the 10Hz combat-spawn path.
        public const int MaxAuthoredRegionTier = 10;
        private const double EndgameScalingBase = 1.25;

        public static double GetEndgameScalingMultiplier(int regionTier)
        {
            if (regionTier <= MaxAuthoredRegionTier)
            {
                return 1.0;
            }

            return Math.Pow(EndgameScalingBase, regionTier - MaxAuthoredRegionTier);
        }

        // Modul: single source of truth for a monster's endgame-scaled
        // combat stats, so every combat-resolution path (live tick,
        // instant-warp, offline projection) applies the identical
        // multiplier instead of each re-deriving RegionTier and re-calling
        // Math.Pow independently. Returns int, matching MonsterDefinition's
        // own field types and every existing call site's arithmetic
        // (* 1000 for milli-hp, etc.) - the scaled result is floored, never
        // rounded up, so it never exceeds the exact mathematical value.
        //
        // SATURATING, not wrapping. The endgame multiplier compounds without
        // bound (1.25 per tier past 10), so an unchecked (int) cast was
        // guaranteed to wrap negative for any player who progressed far
        // enough - and a monster with negative scaled HP spawns already dead,
        // handing out full kill rewards every tick. Saturating at int.MaxValue
        // makes the worst case "absurdly tough" instead of "free loot".
        //
        // The paired call sites must still widen before multiplying: milli-HP
        // is (long)GetScaledMonsterMaxHp(id) * 1000L, because even a legitimate
        // authored 3,000,000 HP boss overflows int once scaled to milli.
        public static int GetScaledMonsterMaxHp(int monsterId)
        {
            if (monsterId < 1 || monsterId > _monsters.Length)
            {
                return 0;
            }

            int baseMaxHp = _monsters[monsterId - 1].MaxHp;
            int regionTier = GetMonsterRegionTier(monsterId);
            return regionTier <= MaxAuthoredRegionTier
                ? baseMaxHp
                : SaturateToInt(baseMaxHp * GetEndgameScalingMultiplier(regionTier));
        }

        public static int GetScaledMonsterAttackPower(int monsterId)
        {
            if (monsterId < 1 || monsterId > _monsters.Length)
            {
                return 0;
            }

            int baseAttackPower = _monsters[monsterId - 1].AttackPower;
            int regionTier = GetMonsterRegionTier(monsterId);
            return regionTier <= MaxAuthoredRegionTier
                ? baseAttackPower
                : SaturateToInt(baseAttackPower * GetEndgameScalingMultiplier(regionTier));
        }

        // Floors toward zero and clamps into int range. A double-to-int cast
        // outside range is undefined behaviour in an unchecked context and in
        // practice yields int.MinValue on x64 - the exact sign flip this guards.
        private static int SaturateToInt(double value)
        {
            if (double.IsNaN(value) || value <= 0.0) return 0;
            if (value >= int.MaxValue) return int.MaxValue;
            return (int)value;
        }

        private static Dictionary<string, int> _baseIdToItemDefinitionIndex = new();

        private static Dictionary<string, int> BuildBaseIdIndex()
        {
            var map = new Dictionary<string, int>(_itemBaseIds.Length);
            for (int i = 0; i < _itemBaseIds.Length; i++)
            {
                map[_itemBaseIds[i]] = i;
            }
            return map;
        }

        // Modul 40/51: reverse lookup from the persisted BaseItemId slug back
        // to its ItemDefinition, used to derive a deterministic fallback
        // market price for items with no completed-trade history yet.
        public static bool TryGetItemDefinitionByBaseId(string baseItemId, out ItemDefinition definition)
        {
            if (_baseIdToItemDefinitionIndex.TryGetValue(baseItemId, out int index))
            {
                definition = _itemDefinitions[index];
                return true;
            }
            definition = default;
            return false;
        }

        private static GatheringNodeDefinition[] _gatheringNodes = Array.Empty<GatheringNodeDefinition>();

        public static ReadOnlySpan<GatheringNodeDefinition> GatheringNodes => _gatheringNodes;

        public static bool TryGetGatheringNode(long activityId, out GatheringNodeDefinition node)
        {
            for (int i = 0; i < _gatheringNodes.Length; i++)
            {
                if (_gatheringNodes[i].ActivityId == activityId)
                {
                    node = _gatheringNodes[i];
                    return true;
                }
            }
            node = default;
            return false;
        }

        // Modul: defensive bounds check - this is called unconditionally
        // from the 10 Hz tick thread on every completed gather and every
        // monster kill (SimulationEngine's gathering-yield and combat-death
        // blocks), which as of this pass has its own outer exception
        // isolation, but a content-authoring mistake (a gathering node or
        // monster referencing a LootTableId outside the authored range)
        // should not even need that safety net to fire - it degrades to
        // "no loot this drop" instead of throwing. Zero allocation on
        // either path: an out-of-range id returns ReadOnlySpan<LootTableEntry>.Empty,
        // a pre-existing static value, not a newly constructed span.
        public static ReadOnlySpan<LootTableEntry> GetLootTable(int lootTableId)
        {
            if (lootTableId <= 0 || !_lootSegments.TryGetValue(lootTableId, out var segment))
            {
                return ReadOnlySpan<LootTableEntry>.Empty;
            }

            return new ReadOnlySpan<LootTableEntry>(_lootEntries, segment.Start, segment.Count);
        }

        private sealed class MonsterJson
        {
            public int Id { get; set; }
            public int MaxHp { get; set; }
            public int AttackPower { get; set; }
            public int BaseGoldReward { get; set; }
            public int BaseXpReward { get; set; }
            public int AttackIntervalMs { get; set; }
            public int LootTableId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string EnemyId { get; set; } = string.Empty;

            // Optional (default 0) so content data authored before these
            // fields existed still parses - see MonsterDefinition's own
            // doc comments for their semantics.
            public int RegionTier { get; set; }
            public int Armor { get; set; }
            public int DodgeRating { get; set; }
        }

        private sealed class ItemJson
        {
            public int Id { get; set; }
            public int RegionTier { get; set; }
            public int BaseValueGold { get; set; }
            public int FlatAttackPower { get; set; }
            public int FlatDefenseRating { get; set; }
            public string BaseId { get; set; } = string.Empty;
        }

        private sealed class GatheringNodeJson
        {
            public int ActivityId { get; set; }
            public int ProfessionType { get; set; }
            public int BaseTickThreshold { get; set; }
            public int BaseMasteryXpReward { get; set; }
        }

        // Modul: Production Release Hardening, Part 3. Flat localization
        // schema - Key mapped directly to each of the four supported
        // languages, one entry per translatable string. Mirrors client
        // LocalizationMatrix.cs's own DTO exactly (Key matches that side's
        // LocalizationKey enum member names, validated there via
        // Enum.TryParse at boot - this server-side DTO deliberately does
        // not depend on that client-only enum, so validation here is
        // purely structural: every field present and non-empty).
        private sealed class LocalizationJson
        {
            public string Key { get; set; } = string.Empty;
            public string En { get; set; } = string.Empty;
            public string Cs { get; set; } = string.Empty;
            public string De { get; set; } = string.Empty;
            public string Pl { get; set; } = string.Empty;
        }

        // Modul: parses server/GameData/*.json into the flat struct arrays
        // above, replacing what used to be hardcoded C# array literals - see
        // the task's Content Pipeline requirement (data-driven balance
        // changes without recompilation). Deliberately builds everything into
        // LOCAL variables first and only assigns the static fields after
        // every file has been read, parsed, and validated successfully (an
        // atomic-style commit) - a failed call therefore leaves any
        // previously loaded good data completely untouched instead of
        // partially corrupting it, which is what makes this method safe to
        // call repeatedly (once per test fixture, or once against a
        // deliberately broken temp directory in a "malformed JSON" test)
        // without needing a caching/idempotency guard. Throws
        // InvalidOperationException on any malformed or missing data -
        // uncaught at the Program.cs call site, this is the intended
        // fast-fail/crash-on-boot behavior for corrupted content data.
        public static void Initialize(string? gameDataDirectory = null)
        {
            string dir = gameDataDirectory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "GameData");

            if (!System.IO.Directory.Exists(dir))
            {
                throw new InvalidOperationException($"ContentRegistry.Initialize: GameData directory not found at '{dir}'.");
            }

            List<MonsterJson> monsterJson = ReadAndValidateJsonFile<MonsterJson>(dir, "monsters.json");
            List<ItemJson> itemJson = ReadAndValidateJsonFile<ItemJson>(dir, "items.json");
            List<GatheringNodeJson> nodeJson = ReadAndValidateJsonFile<GatheringNodeJson>(dir, "gathering_nodes.json");
            GameBalanceDefinition balance = ReadOptionalBalanceConfig(dir);
            List<LocalizationJson> localizationJson = ReadOptionalLocalizationsConfig(dir);

            for (int i = 0; i < localizationJson.Count; i++)
            {
                LocalizationJson entry = localizationJson[i];
                if (string.IsNullOrEmpty(entry.Key))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: 'localizations.json' entry at index {i} has an empty Key.");
                }
                if (string.IsNullOrEmpty(entry.En) || string.IsNullOrEmpty(entry.Cs) || string.IsNullOrEmpty(entry.De) || string.IsNullOrEmpty(entry.Pl))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: 'localizations.json' entry Key='{entry.Key}' is missing a translation for one or more of En/Cs/De/Pl.");
                }
            }

            var newLocalizations = new Dictionary<string, LocalizationJson>(localizationJson.Count);
            for (int i = 0; i < localizationJson.Count; i++)
            {
                newLocalizations[localizationJson[i].Key] = localizationJson[i];
            }

            RequireContiguousIds(monsterJson.Count, monsterJson.Select(m => m.Id), "monsters.json", "Id");
            // Modul: items.json is deliberately NOT checked for contiguity.
            //
            // Monsters still are - their ids are the activity ids the client
            // sends, and a hole there is a monster nobody can fight. Items are
            // different: 111 of them were removed (106 legacy pieces and the
            // five invented offhands) and every surviving id had to keep its
            // meaning, because ids are referenced by loot tables, recipes and
            // by every owned row in the live database. Renumbering to close the
            // gaps would have repointed all of that at different objects.
            //
            // The duplicate check that this guard also performed is not lost -
            // it moved into the item load below, where the highest id is
            // computed anyway.

            var newMonsters = new MonsterDefinition[monsterJson.Count];
            var newMonsterNames = new string[monsterJson.Count];
            var newMonsterEnemyIds = new string[monsterJson.Count];
            for (int i = 0; i < monsterJson.Count; i++)
            {
                MonsterJson m = monsterJson[i];
                if (m.MaxHp <= 0)
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: monsters.json entry Id={m.Id} has non-positive MaxHp ({m.MaxHp}).");
                }
                if (m.AttackIntervalMs <= 0)
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: monsters.json entry Id={m.Id} has non-positive AttackIntervalMs ({m.AttackIntervalMs}).");
                }
                if (string.IsNullOrEmpty(m.Name) || string.IsNullOrEmpty(m.EnemyId))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: monsters.json entry Id={m.Id} is missing Name or EnemyId.");
                }

                if (m.RegionTier < 0 || m.Armor < 0 || m.DodgeRating < 0)
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: monsters.json entry Id={m.Id} has a negative RegionTier, Armor, or DodgeRating.");
                }

                int index = m.Id - 1;
                newMonsters[index] = new MonsterDefinition
                {
                    Id = m.Id,
                    MaxHp = m.MaxHp,
                    AttackPower = m.AttackPower,
                    BaseGoldReward = m.BaseGoldReward,
                    BaseXpReward = m.BaseXpReward,
                    AttackIntervalMs = m.AttackIntervalMs,
                    LootTableId = m.LootTableId,
                    RegionTier = m.RegionTier,
                    Armor = m.Armor,
                    DodgeRating = m.DodgeRating
                };
                newMonsterNames[index] = m.Name;
                newMonsterEnemyIds[index] = m.EnemyId;
            }

            // Modul: THE CATALOGUE MAY HAVE HOLES IN IT.
            //
            // These arrays are indexed by `Id - 1`, so they used to be sized by
            // the ENTRY COUNT - which silently required the ids to be a
            // contiguous 1..N with nothing ever removed. Deleting one item from
            // items.json would have thrown an IndexOutOfRange for the highest
            // id, and deleting one and renumbering the rest would have
            // repointed every loot table, recipe and owned row in the database
            // at a different object.
            //
            // Sized by the HIGHEST id instead. Removing an item now leaves a
            // hole, every surviving id keeps its meaning, and the holes are
            // inert: their BaseId is empty, which every lookup below already
            // treats as "not a thing". That is what made it possible to cut 111
            // items - 106 legacy pieces and the five invented offhands - out of
            // a live catalogue without touching a single reference to the 326
            // that stayed.
            int highestItemId = 0;
            var seenItemIds = new HashSet<int>(itemJson.Count);
            for (int i = 0; i < itemJson.Count; i++)
            {
                int id = itemJson[i].Id;
                if (id < 1)
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: items.json has an entry with a non-positive Id ({id}).");
                }

                if (!seenItemIds.Add(id))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: items.json has a duplicate Id ({id}). Ids are positional and a duplicate silently overwrites the other entry.");
                }

                if (id > highestItemId) highestItemId = id;
            }

            var newItems = new ItemDefinition[highestItemId];
            var newItemBaseIds = new string[highestItemId];

            // Holes read as an empty BaseId rather than null, so callers can
            // test them with the same string checks they already use.
            for (int i = 0; i < newItemBaseIds.Length; i++) newItemBaseIds[i] = string.Empty;

            for (int i = 0; i < itemJson.Count; i++)
            {
                ItemJson it = itemJson[i];
                if (string.IsNullOrEmpty(it.BaseId))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: items.json entry Id={it.Id} is missing BaseId.");
                }

                int index = it.Id - 1;
                newItems[index] = new ItemDefinition
                {
                    Id = it.Id,
                    RegionTier = it.RegionTier,
                    BaseValueGold = it.BaseValueGold,
                    FlatAttackPower = it.FlatAttackPower,
                    FlatDefenseRating = it.FlatDefenseRating
                };
                newItemBaseIds[index] = it.BaseId;
            }

            var seenActivityIds = new HashSet<int>(nodeJson.Count);
            var newGatheringNodes = new GatheringNodeDefinition[nodeJson.Count];
            for (int i = 0; i < nodeJson.Count; i++)
            {
                GatheringNodeJson n = nodeJson[i];
                if (!seenActivityIds.Add(n.ActivityId))
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: gathering_nodes.json has a duplicate ActivityId ({n.ActivityId}).");
                }

                newGatheringNodes[i] = new GatheringNodeDefinition
                {
                    ActivityId = n.ActivityId,
                    ProfessionType = n.ProfessionType,
                    BaseTickThreshold = n.BaseTickThreshold,
                    BaseMasteryXpReward = n.BaseMasteryXpReward
                };
            }

            var newBaseIdIndex = new Dictionary<string, int>(newItemBaseIds.Length);
            for (int i = 0; i < newItemBaseIds.Length; i++)
            {
                newBaseIdIndex[newItemBaseIds[i]] = i;
            }

            _monsters = newMonsters;
            _monsterNames = newMonsterNames;
            _monsterEnemyIds = newMonsterEnemyIds;
            _itemDefinitions = newItems;
            _itemBaseIds = newItemBaseIds;
            _gatheringNodes = newGatheringNodes;
            _baseIdToItemDefinitionIndex = newBaseIdIndex;
            _balance = balance;

            // Modul: Production Release Hardening, Part 1. Built once here
            // (not per-lookup) from the same IapProductPrices catalog
            // ResolvePremiumDiamondsForProduct already reads - see
            // ProductIdHasher's own doc comment for why FNV-1a instead of
            // the previous string.GetHashCode() (randomized per process,
            // so it could never match a hash computed on a different
            // process/machine, which is exactly why TargetProductIdHash
            // never resolved before this fix). A hash collision between
            // two real product ids would silently make the later one in
            // iteration order win the dictionary slot - acceptable for a
            // small, content-authored catalog (a handful of gem-pack
            // ids), where a collision would surface immediately as an
            // obviously wrong purchase during content QA, not silently in
            // production against attacker-chosen input.
            var newProductIdHashLookup = new Dictionary<uint, string>(balance.IapProductPrices.Count);
            foreach (string productId in balance.IapProductPrices.Keys)
            {
                newProductIdHashLookup[ProductIdHasher.HashProductId(productId)] = productId;
            }
            _productIdHashLookup = newProductIdHashLookup;
            _localizations = newLocalizations;
        }

        private static Dictionary<uint, string> _productIdHashLookup = new Dictionary<uint, string>();

        // Modul: never throws on an unresolved hash (TryGetValue, not the
        // throwing indexer) - an unrecognized TargetProductIdHash (a stale
        // client build, a hash collision, or simply an invalid/forged
        // value) must fail closed as "no product resolved," never as an
        // uncaught exception on the billing hot path.
        public static bool TryResolveProductIdFromHash(uint hash, out string productId)
        {
            return _productIdHashLookup.TryGetValue(hash, out productId!);
        }

        // Modul: GameBalanceConfig.json is deliberately optional, unlike
        // monsters/items/gathering_nodes - many existing ContentRegistry.Initialize
        // call sites (temp directories built for "malformed JSON" tests,
        // the benchmark harness) supply a minimal custom gameDataDirectory
        // with no reason to also carry a balance file. A missing file, or
        // a missing individual field within it, silently falls back to
        // GameBalanceDefinition's own defaults - which are the exact
        // literals GuildRaidEngine/GuildContributionEngine used before
        // externalization - so nothing behaves differently when this file
        // is absent. A malformed (present but unparseable) file still
        // fails loudly, the same fail-fast posture as every other content
        // file, since a present-but-broken config is far more likely to be
        // an authoring mistake than an intentional omission.
        private static GameBalanceDefinition ReadOptionalBalanceConfig(string directory)
        {
            string path = System.IO.Path.Combine(directory, "GameBalanceConfig.json");
            if (!System.IO.File.Exists(path))
            {
                return new GameBalanceDefinition();
            }

            string text = System.IO.File.ReadAllText(path);
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<GameBalanceDefinition>(text) ?? new GameBalanceDefinition();
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"ContentRegistry.Initialize: 'GameBalanceConfig.json' contains malformed JSON: {ex.Message}", ex);
            }
        }

        // Modul: Production Release Hardening, Part 3. Deliberately
        // optional, mirroring ReadOptionalBalanceConfig's exact posture
        // (and for the same reason - existing ContentRegistry.Initialize
        // call sites that build a minimal temporary GameData directory for
        // unrelated tests have no reason to also carry a localizations
        // file). Nothing server-side actually reads localized text at
        // runtime - this file is client-facing only (see client
        // LocalizationMatrix.cs, which parses the exact same file mirrored
        // into StreamingAssets/GameData) - so this method exists purely to
        // fail the build/boot fast on malformed or incomplete translation
        // data, the same content-QA safety net every other authored
        // content file gets.
        private static List<LocalizationJson> ReadOptionalLocalizationsConfig(string directory)
        {
            string path = System.IO.Path.Combine(directory, "localizations.json");
            if (!System.IO.File.Exists(path))
            {
                return new List<LocalizationJson>();
            }

            string text = System.IO.File.ReadAllText(path);
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<LocalizationJson>>(text) ?? new List<LocalizationJson>();
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"ContentRegistry.Initialize: 'localizations.json' contains malformed JSON: {ex.Message}", ex);
            }
        }

        private static List<T> ReadAndValidateJsonFile<T>(string directory, string fileName)
        {
            string path = System.IO.Path.Combine(directory, fileName);
            if (!System.IO.File.Exists(path))
            {
                throw new InvalidOperationException($"ContentRegistry.Initialize: required content file '{fileName}' was not found at '{path}'.");
            }

            string text = System.IO.File.ReadAllText(path);
            List<T>? parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<List<T>>(text);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"ContentRegistry.Initialize: '{fileName}' contains malformed JSON: {ex.Message}", ex);
            }

            if (parsed == null || parsed.Count == 0)
            {
                throw new InvalidOperationException($"ContentRegistry.Initialize: '{fileName}' parsed to null or an empty list - at least one content entry is required.");
            }

            return parsed;
        }

        private static void RequireContiguousIds(int count, IEnumerable<int> ids, string fileName, string idFieldName)
        {
            var seen = new bool[count];
            foreach (int id in ids)
            {
                if (id < 1 || id > count)
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: '{fileName}' has a {idFieldName} ({id}) outside the required contiguous range 1..{count} - IDs must be exactly 1..N with no gaps, since content is indexed directly by Id-1.");
                }
                if (seen[id - 1])
                {
                    throw new InvalidOperationException($"ContentRegistry.Initialize: '{fileName}' has a duplicate {idFieldName} ({id}).");
                }
                seen[id - 1] = true;
            }
        }
    }

}

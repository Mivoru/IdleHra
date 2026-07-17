using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

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
            new LootTableEntry { ItemId = 250, Weight = 25 },  // index 22: mat_mouse_fur
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
            new LootTableEntry { ItemId = 307, Weight = 15 },  // index 35: mat_lodestone
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
            { 201, (21, 1) },
            { 301, (0, 1) },
            { 302, (1, 1) },
            { 303, (2, 1) },
            { 304, (3, 1) },
            { 305, (4, 1) },
            { 306, (5, 1) },
            { 307, (6, 1) },
            { 308, (7, 1) },
            { 309, (8, 1) },
            { 401, (9, 1) },
            { 402, (10, 1) },
            { 403, (11, 1) },
            { 404, (12, 1) },
            { 405, (13, 1) },
            { 406, (14, 1) },
            { 407, (15, 1) },
            { 408, (16, 1) },
            { 409, (17, 1) },
            { 410, (18, 1) },
            { 411, (19, 1) },
            { 412, (20, 1) },

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
            new RecipeDefinition { ResultItemId = 184, ProfessionType = 2, RequiredLevel = 10, Mat1Id = 93, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // copper_bar_crafting_material
            new RecipeDefinition { ResultItemId = 185, ProfessionType = 2, RequiredLevel = 20, Mat1Id = 1, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // bronze_bar_crafting_material
            new RecipeDefinition { ResultItemId = 186, ProfessionType = 2, RequiredLevel = 30, Mat1Id = 111, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // iron_bar_crafting_material
            new RecipeDefinition { ResultItemId = 187, ProfessionType = 2, RequiredLevel = 40, Mat1Id = 1, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // steel_bar_crafting_material
            new RecipeDefinition { ResultItemId = 188, ProfessionType = 2, RequiredLevel = 50, Mat1Id = 147, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // silver_bar_crafting_material
            new RecipeDefinition { ResultItemId = 189, ProfessionType = 2, RequiredLevel = 60, Mat1Id = 1, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // gold_bar_crafting_material
            new RecipeDefinition { ResultItemId = 190, ProfessionType = 2, RequiredLevel = 70, Mat1Id = 21, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // mithril_bar_crafting_material
            new RecipeDefinition { ResultItemId = 191, ProfessionType = 2, RequiredLevel = 80, Mat1Id = 39, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // adamantite_bar_crafting_material
            new RecipeDefinition { ResultItemId = 192, ProfessionType = 2, RequiredLevel = 90, Mat1Id = 57, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // obsidian_bar_crafting_material
            new RecipeDefinition { ResultItemId = 193, ProfessionType = 2, RequiredLevel = 100, Mat1Id = 75, Mat1Count = 3, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 3000 }, // celestial_bar_crafting_material
            new RecipeDefinition { ResultItemId = 19, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 189, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // loch_crossbow_range_weapon_slot_base
            new RecipeDefinition { ResultItemId = 37, ProfessionType = 3, RequiredLevel = 70, Mat1Id = 190, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // dullahan_greatsword_melee_weapon_slot_base
            new RecipeDefinition { ResultItemId = 55, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 191, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // volcanic_warhammer_blunt_weapon_slot_base
            new RecipeDefinition { ResultItemId = 73, ProfessionType = 3, RequiredLevel = 90, Mat1Id = 192, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // northern_greataxe_melee_weapon_slot_base
            new RecipeDefinition { ResultItemId = 91, ProfessionType = 3, RequiredLevel = 100, Mat1Id = 193, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // peruns_stormcaller_structural_weapon_slot_base___adapts_to_matching_high_weapon_skill_archetype_upon_compilation
            new RecipeDefinition { ResultItemId = 95, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 185, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // bronze_dagger_melee_weapon_slot_base
            new RecipeDefinition { ResultItemId = 109, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // vodnk_harpoon_range_weapon_slot_base
            new RecipeDefinition { ResultItemId = 127, ProfessionType = 3, RequiredLevel = 30, Mat1Id = 186, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // kobold_sledge_blunt_weapon_slot_base
            new RecipeDefinition { ResultItemId = 128, ProfessionType = 3, RequiredLevel = 30, Mat1Id = 186, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // spike_spatha_melee_weapon_slot_base
            new RecipeDefinition { ResultItemId = 145, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 187, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // tatzel_crossbow_range_weapon_slot_base
            new RecipeDefinition { ResultItemId = 163, ProfessionType = 3, RequiredLevel = 50, Mat1Id = 188, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // poludnica_scythe_melee_weapon_slot_base
            new RecipeDefinition { ResultItemId = 173, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // wooden_bow_ranged_weapon_slot_base
            new RecipeDefinition { ResultItemId = 182, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // troll_club_blunt_weapon_slot_base
            new RecipeDefinition { ResultItemId = 183, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 8, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 8000 }, // copper_greatsword_melee_weapon_slot_base
            new RecipeDefinition { ResultItemId = 3, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 189, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // gilded_sabatons_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 10, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 189, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // gilded_hauberk_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 13, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 189, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // gilded_chausses_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 15, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 189, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // gilded_sallet_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 16, ProfessionType = 3, RequiredLevel = 60, Mat1Id = 189, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // gilded_round_shield_shield_slot_base
            new RecipeDefinition { ResultItemId = 23, ProfessionType = 3, RequiredLevel = 70, Mat1Id = 190, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // mithril_greaves_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 29, ProfessionType = 3, RequiredLevel = 70, Mat1Id = 190, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // mithril_cuirass_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 32, ProfessionType = 3, RequiredLevel = 70, Mat1Id = 190, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // mithril_platelegs_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 34, ProfessionType = 3, RequiredLevel = 70, Mat1Id = 190, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // mithril_armet_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 35, ProfessionType = 3, RequiredLevel = 70, Mat1Id = 190, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // mithril_scutum_shield_slot_base
            new RecipeDefinition { ResultItemId = 41, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 191, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // adamant_sollerets_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 47, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 191, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // adamant_plate_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 50, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 191, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // adamant_leggings_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 52, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 191, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // adamant_helm_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 53, ProfessionType = 3, RequiredLevel = 80, Mat1Id = 191, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // adamant_tower_shield_shield_slot_base
            new RecipeDefinition { ResultItemId = 59, ProfessionType = 3, RequiredLevel = 90, Mat1Id = 192, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // runed_boots_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 65, ProfessionType = 3, RequiredLevel = 90, Mat1Id = 192, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // runed_hauberk_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 68, ProfessionType = 3, RequiredLevel = 90, Mat1Id = 192, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // runed_cuisses_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 70, ProfessionType = 3, RequiredLevel = 90, Mat1Id = 192, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // runed_greathelm_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 71, ProfessionType = 3, RequiredLevel = 90, Mat1Id = 192, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // runed_aegis_shield_slot_base
            new RecipeDefinition { ResultItemId = 77, ProfessionType = 3, RequiredLevel = 100, Mat1Id = 193, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // transcendent_sollerets_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 83, ProfessionType = 3, RequiredLevel = 100, Mat1Id = 193, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // transcendent_cuirass_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 86, ProfessionType = 3, RequiredLevel = 100, Mat1Id = 193, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // transcendent_platelegs_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 88, ProfessionType = 3, RequiredLevel = 100, Mat1Id = 193, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // transcendent_armet_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 89, ProfessionType = 3, RequiredLevel = 100, Mat1Id = 193, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // transcendent_greatshield_shield_slot_base
            new RecipeDefinition { ResultItemId = 101, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 185, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // bronze_cuirass_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 104, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 185, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // bronze_greaves_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 106, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 185, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // bronze_helmet_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 107, ProfessionType = 3, RequiredLevel = 20, Mat1Id = 185, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // bronze_buckler_shield_slot_base
            new RecipeDefinition { ResultItemId = 113, ProfessionType = 3, RequiredLevel = 30, Mat1Id = 186, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // iron_sabatons_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 119, ProfessionType = 3, RequiredLevel = 30, Mat1Id = 186, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // iron_breastplate_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 122, ProfessionType = 3, RequiredLevel = 30, Mat1Id = 186, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // iron_platelegs_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 124, ProfessionType = 3, RequiredLevel = 30, Mat1Id = 186, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // iron_armet_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 125, ProfessionType = 3, RequiredLevel = 30, Mat1Id = 186, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // iron_kite_shield_shield_slot_base
            new RecipeDefinition { ResultItemId = 131, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 187, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // steel_sollerets_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 137, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 187, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // steel_hauberk_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 140, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 187, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // steel_chausses_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 142, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 187, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // steel_sallet_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 143, ProfessionType = 3, RequiredLevel = 40, Mat1Id = 187, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // steel_heater_shield_shield_slot_base
            new RecipeDefinition { ResultItemId = 149, ProfessionType = 3, RequiredLevel = 50, Mat1Id = 188, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // silvered_greaves_boots_armor_slot_base
            new RecipeDefinition { ResultItemId = 155, ProfessionType = 3, RequiredLevel = 50, Mat1Id = 188, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // silvered_cuirass_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 158, ProfessionType = 3, RequiredLevel = 50, Mat1Id = 188, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // silvered_platelegs_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 160, ProfessionType = 3, RequiredLevel = 50, Mat1Id = 188, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // silvered_helm_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 161, ProfessionType = 3, RequiredLevel = 50, Mat1Id = 188, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // silvered_pavise_shield_slot_base
            new RecipeDefinition { ResultItemId = 167, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // leather_boots_footwear_armor_slot_base
            new RecipeDefinition { ResultItemId = 170, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // leather_hood_helmet_armor_slot_base
            new RecipeDefinition { ResultItemId = 176, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // leather_tunic_chest_armor_slot_base
            new RecipeDefinition { ResultItemId = 178, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // leather_chaps_leggings_armor_slot_base
            new RecipeDefinition { ResultItemId = 179, ProfessionType = 3, RequiredLevel = 10, Mat1Id = 184, Mat1Count = 6, Mat2Id = 0, Mat2Count = 0, CraftingTimeMs = 5000 }, // crude_copper_shield_shield_slot_base
            // Modul: tiers 2-10 previously all repeated Mat1Id = 1
            // (gold_ore_crafting_material, a Smithing-tier bar-refining
            // input, not a fishing material at all) - an evident
            // copy-paste authoring error, since every result item name
            // already names the exact raw fish it should consume and
            // items.json has a *_raw_fishing_material entry matching each
            // name precisely. Corrected to the matching fish item id below;
            // this is what actually makes Cooking craftable end to end once
            // the Fishing gathering nodes (ActivityId 301-309) can supply
            // these materials - unlike Alchemy's Mat1Id/Mat2Id chain, which
            // was already internally consistent and required no fix.
            new RecipeDefinition { ResultItemId = 194, ProfessionType = 4, RequiredLevel = 10, Mat1Id = 11, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_pond_minnow_t1_food (coastline_cod_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 195, ProfessionType = 4, RequiredLevel = 20, Mat1Id = 102, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_river_trout_t2_food (river_trout_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 196, ProfessionType = 4, RequiredLevel = 30, Mat1Id = 120, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_mud_carp_t3_food (mud_carp_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 197, ProfessionType = 4, RequiredLevel = 40, Mat1Id = 138, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_chasm_pike_t4_food (chasm_pike_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 198, ProfessionType = 4, RequiredLevel = 50, Mat1Id = 156, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_steppe_salmon_t5_food (steppe_salmon_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 199, ProfessionType = 4, RequiredLevel = 60, Mat1Id = 11, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_coastline_cod_t6_food (coastline_cod_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 200, ProfessionType = 4, RequiredLevel = 70, Mat1Id = 30, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_deep_mire_eel_t7_food (deep_mire_eel_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 201, ProfessionType = 4, RequiredLevel = 80, Mat1Id = 48, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_canyon_catfish_t8_food (canyon_catfish_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 202, ProfessionType = 4, RequiredLevel = 90, Mat1Id = 66, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_fjord_shark_t9_food (fjord_shark_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 203, ProfessionType = 4, RequiredLevel = 100, Mat1Id = 84, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_astral_whale_t10_food (astral_whale_raw_fishing_material)
            new RecipeDefinition { ResultItemId = 204, ProfessionType = 5, RequiredLevel = 10, Mat1Id = 5, Mat1Count = 2, Mat2Id = 14, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t01
            new RecipeDefinition { ResultItemId = 205, ProfessionType = 5, RequiredLevel = 10, Mat1Id = 5, Mat1Count = 2, Mat2Id = 14, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t01
            new RecipeDefinition { ResultItemId = 206, ProfessionType = 5, RequiredLevel = 20, Mat1Id = 9, Mat1Count = 2, Mat2Id = 24, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t02
            new RecipeDefinition { ResultItemId = 207, ProfessionType = 5, RequiredLevel = 20, Mat1Id = 9, Mat1Count = 2, Mat2Id = 24, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t02
            new RecipeDefinition { ResultItemId = 208, ProfessionType = 5, RequiredLevel = 30, Mat1Id = 14, Mat1Count = 2, Mat2Id = 28, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t03
            new RecipeDefinition { ResultItemId = 209, ProfessionType = 5, RequiredLevel = 30, Mat1Id = 14, Mat1Count = 2, Mat2Id = 28, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t03
            new RecipeDefinition { ResultItemId = 210, ProfessionType = 5, RequiredLevel = 40, Mat1Id = 24, Mat1Count = 2, Mat2Id = 31, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t04
            new RecipeDefinition { ResultItemId = 211, ProfessionType = 5, RequiredLevel = 40, Mat1Id = 24, Mat1Count = 2, Mat2Id = 31, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t04
            new RecipeDefinition { ResultItemId = 212, ProfessionType = 5, RequiredLevel = 50, Mat1Id = 28, Mat1Count = 2, Mat2Id = 33, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t05
            new RecipeDefinition { ResultItemId = 213, ProfessionType = 5, RequiredLevel = 50, Mat1Id = 28, Mat1Count = 2, Mat2Id = 33, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t05
            new RecipeDefinition { ResultItemId = 214, ProfessionType = 5, RequiredLevel = 60, Mat1Id = 31, Mat1Count = 2, Mat2Id = 42, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t06
            new RecipeDefinition { ResultItemId = 215, ProfessionType = 5, RequiredLevel = 60, Mat1Id = 31, Mat1Count = 2, Mat2Id = 42, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t06
            new RecipeDefinition { ResultItemId = 216, ProfessionType = 5, RequiredLevel = 70, Mat1Id = 33, Mat1Count = 2, Mat2Id = 49, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t07
            new RecipeDefinition { ResultItemId = 217, ProfessionType = 5, RequiredLevel = 70, Mat1Id = 33, Mat1Count = 2, Mat2Id = 49, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t07
            new RecipeDefinition { ResultItemId = 218, ProfessionType = 5, RequiredLevel = 80, Mat1Id = 42, Mat1Count = 2, Mat2Id = 51, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t08
            new RecipeDefinition { ResultItemId = 219, ProfessionType = 5, RequiredLevel = 80, Mat1Id = 42, Mat1Count = 2, Mat2Id = 51, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t08
            new RecipeDefinition { ResultItemId = 220, ProfessionType = 5, RequiredLevel = 90, Mat1Id = 49, Mat1Count = 2, Mat2Id = 60, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t09
            new RecipeDefinition { ResultItemId = 221, ProfessionType = 5, RequiredLevel = 90, Mat1Id = 49, Mat1Count = 2, Mat2Id = 60, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t09
            new RecipeDefinition { ResultItemId = 222, ProfessionType = 5, RequiredLevel = 100, Mat1Id = 51, Mat1Count = 2, Mat2Id = 69, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_off_t10
            new RecipeDefinition { ResultItemId = 223, ProfessionType = 5, RequiredLevel = 100, Mat1Id = 51, Mat1Count = 2, Mat2Id = 69, Mat2Count = 1, CraftingTimeMs = 6000 }, // alc_def_t10
        };
        public static ReadOnlySpan<RecipeDefinition> Recipes => _recipes;

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
        public static string GetItemBaseId(int itemId) => _itemBaseIds[itemId - 1];

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
        public static int GetScaledMonsterMaxHp(int monsterId)
        {
            if (monsterId < 1 || monsterId > _monsters.Length)
            {
                return 0;
            }

            int baseMaxHp = _monsters[monsterId - 1].MaxHp;
            int regionTier = GetMonsterRegionTier(monsterId);
            return regionTier <= MaxAuthoredRegionTier ? baseMaxHp : (int)(baseMaxHp * GetEndgameScalingMultiplier(regionTier));
        }

        public static int GetScaledMonsterAttackPower(int monsterId)
        {
            if (monsterId < 1 || monsterId > _monsters.Length)
            {
                return 0;
            }

            int baseAttackPower = _monsters[monsterId - 1].AttackPower;
            int regionTier = GetMonsterRegionTier(monsterId);
            return regionTier <= MaxAuthoredRegionTier ? baseAttackPower : (int)(baseAttackPower * GetEndgameScalingMultiplier(regionTier));
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
            RequireContiguousIds(itemJson.Count, itemJson.Select(i => i.Id), "items.json", "Id");

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

            var newItems = new ItemDefinition[itemJson.Count];
            var newItemBaseIds = new string[itemJson.Count];
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
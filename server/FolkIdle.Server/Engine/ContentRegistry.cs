using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FolkIdle.Server.Engine
{
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
        public int ProfessionType; // 0 = Woodcutting, 1 = Mining
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

        private static string[] _monsterNames = Array.Empty<string>();
        private static string[] _monsterEnemyIds = Array.Empty<string>();
        private static string[] _itemBaseIds = Array.Empty<string>();

        private static MonsterDefinition[] _monsters = Array.Empty<MonsterDefinition>();

        private static readonly LootTableEntry[] _lootEntries = new LootTableEntry[]
        {
        };

        private static readonly (int Start, int Count)[] _lootSegments = new (int Start, int Count)[]
        {
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
            (0, 0),
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
            new RecipeDefinition { ResultItemId = 194, ProfessionType = 4, RequiredLevel = 10, Mat1Id = 11, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_pond_minnow_t1_food
            new RecipeDefinition { ResultItemId = 195, ProfessionType = 4, RequiredLevel = 20, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_river_trout_t2_food
            new RecipeDefinition { ResultItemId = 196, ProfessionType = 4, RequiredLevel = 30, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_mud_carp_t3_food
            new RecipeDefinition { ResultItemId = 197, ProfessionType = 4, RequiredLevel = 40, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_chasm_pike_t4_food
            new RecipeDefinition { ResultItemId = 198, ProfessionType = 4, RequiredLevel = 50, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_steppe_salmon_t5_food
            new RecipeDefinition { ResultItemId = 199, ProfessionType = 4, RequiredLevel = 60, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_coastline_cod_t6_food
            new RecipeDefinition { ResultItemId = 200, ProfessionType = 4, RequiredLevel = 70, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_deep_mire_eel_t7_food
            new RecipeDefinition { ResultItemId = 201, ProfessionType = 4, RequiredLevel = 80, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_canyon_catfish_t8_food
            new RecipeDefinition { ResultItemId = 202, ProfessionType = 4, RequiredLevel = 90, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_fjord_shark_t9_food
            new RecipeDefinition { ResultItemId = 203, ProfessionType = 4, RequiredLevel = 100, Mat1Id = 1, Mat1Count = 1, Mat2Id = 129, Mat2Count = 1, CraftingTimeMs = 4000 }, // cooked_astral_whale_t10_food
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

        public static ReadOnlySpan<LootTableEntry> GetLootTable(int lootTableId)
        {
            var segment = _lootSegments[lootTableId - 1];
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

                int index = m.Id - 1;
                newMonsters[index] = new MonsterDefinition
                {
                    Id = m.Id,
                    MaxHp = m.MaxHp,
                    AttackPower = m.AttackPower,
                    BaseGoldReward = m.BaseGoldReward,
                    BaseXpReward = m.BaseXpReward,
                    AttackIntervalMs = m.AttackIntervalMs,
                    LootTableId = m.LootTableId
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
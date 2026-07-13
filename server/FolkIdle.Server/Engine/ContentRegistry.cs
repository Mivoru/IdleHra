using System;
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

        private static readonly string[] _monsterNames = new string[]
        {
            "Highland Ram",
            "Moor Crow",
            "Mist Stalker",
            "Coastline Selkie Renegade",
            "Bog Stalker",
            "Kelpie Mare of the Depths",
            "Shadow Raven",
            "Bramble Boar",
            "Wood Sprite",
            "Bark Golem",
            "Schrat Huntsman",
            "Headless Dullahan Knight",
            "Karst Scorpion",
            "Canyon Viper",
            "Cave Harpy",
            "Stone Gargoyle",
            "Kallikantzaroi Corruptor",
            "Elder Balkan Dragon (Zmey)",
            "Fjord Gull",
            "Glacial Bear",
            "Runestone Guard",
            "Draugr Raider",
            "Draugr Berserker",
            "Frost Jtunn Exile",
            "Auroral Spark",
            "Citadel Sentinel",
            "Crystal Golem",
            "Transcendent Shade",
            "Arch-Revenant",
            "Perun's Celestial Avatar",
            "Bog Viper",
            "Swamp Firefly",
            "Drowned Corpse",
            "Willow Whisp",
            "Marsh Hag",
            "Ancient Vodnk Regent",
            "Cave Bat",
            "Iron Ore Beetle",
            "Subterranean Burrower",
            "Deep Earth Imp",
            "Mine Saboteur",
            "Great Kobold Overlord",
            "Mountain Eagle",
            "Crag Goat",
            "Frost Lizard",
            "Rock Elemental",
            "Alpine Wyrm",
            "Crested Tatzelwurm",
            "Field Locust",
            "Steppe Falcon",
            "Blighted Vulture",
            "Dry Grass Spirit",
            "Sunstroke Specter",
            "Crimson Noon Wraith (Poludnica)",
            "Forest Rat",
            "Marsh Frog",
            "Stray Feral Cat",
            "Hungry Timber Dog",
            "Rabid Alpha Wolf",
            "Moss-Grown Forest Troll",
        };

        private static readonly string[] _monsterEnemyIds = new string[]
        {
            "highland_ram",
            "moor_crow",
            "mist_stalker",
            "coastline_selkie_renegade",
            "bog_stalker",
            "kelpie_mare_of_the_depths",
            "shadow_raven",
            "bramble_boar",
            "wood_sprite",
            "bark_golem",
            "schrat_huntsman",
            "headless_dullahan_knight",
            "karst_scorpion",
            "canyon_viper",
            "cave_harpy",
            "stone_gargoyle",
            "kallikantzaroi_corruptor",
            "elder_balkan_dragon_zmey",
            "fjord_gull",
            "glacial_bear",
            "runestone_guard",
            "draugr_raider",
            "draugr_berserker",
            "frost_jtunn_exile",
            "auroral_spark",
            "citadel_sentinel",
            "crystal_golem",
            "transcendent_shade",
            "arch_revenant",
            "peruns_celestial_avatar",
            "bog_viper",
            "swamp_firefly",
            "drowned_corpse",
            "willow_whisp",
            "marsh_hag",
            "ancient_vodnk_regent",
            "cave_bat",
            "iron_ore_beetle",
            "subterranean_burrower",
            "deep_earth_imp",
            "mine_saboteur",
            "great_kobold_overlord",
            "mountain_eagle",
            "crag_goat",
            "frost_lizard",
            "rock_elemental",
            "alpine_wyrm",
            "crested_tatzelwurm",
            "field_locust",
            "steppe_falcon",
            "blighted_vulture",
            "dry_grass_spirit",
            "sunstroke_specter",
            "crimson_noon_wraith_poludnica",
            "forest_rat",
            "marsh_frog",
            "stray_feral_cat",
            "hungry_timber_dog",
            "rabid_alpha_wolf",
            "moss_grown_forest_troll",
        };

        private static readonly string[] _itemBaseIds = new string[]
        {
            "gold_ore_crafting_material",
            "highland_wool_crafting_material",
            "gilded_sabatons_boots_armor_slot_base",
            "premium_diamond",
            "salt_lotus_herbalism_material",
            "ominous_feather_crafting_material",
            "gilded_band_ring_1/2_slot_base",
            "maple_trees_woodcutting_material",
            "condensation_essence_alchemy_material",
            "gilded_hauberk_chest_armor_slot_base",
            "coastline_cod_raw_fishing_material",
            "selkie_skin_fragment_crafting_material",
            "gilded_chausses_leggings_armor_slot_base",
            "peat_clump_rare_alchemy_ingredient",
            "gilded_sallet_helmet_armor_slot_base",
            "gilded_round_shield_shield_slot_base",
            "kelpie_mane_unique_regional_boss_material",
            "premium_diamond_cluster",
            "loch_crossbow_range_weapon_slot_base",
            "abyssal_pearl_amulet_slot_base",
            "mithril_ore_crafting_material",
            "shadow_feather_crafting_material",
            "mithril_greaves_boots_armor_slot_base",
            "screaming_mandrake_herbalism_material",
            "tainted_tusk_crafting_material",
            "mithril_signet_ring_1/2_slot_base",
            "yew_trees_woodcutting_material",
            "spore_pod_alchemy_material",
            "mithril_cuirass_chest_armor_slot_base",
            "deep_mire_eel_raw_fishing_material",
            "heartwood_core_alchemy_material",
            "mithril_platelegs_leggings_armor_slot_base",
            "schrat_horn_rare_alchemy_ingredient",
            "mithril_armet_helmet_armor_slot_base",
            "mithril_scutum_shield_slot_base",
            "spine_whip_link_unique_regional_boss_material",
            "dullahan_greatsword_melee_weapon_slot_base",
            "spinal_collar_amulet_slot_base",
            "adamantite_ore_crafting_material",
            "scorpion_stinger_crafting_material",
            "adamant_sollerets_boots_armor_slot_base",
            "jagged_bloodgrass_herbalism_material",
            "crystallized_venom_crafting_material",
            "adamant_loop_ring_1/2_slot_base",
            "elder_trees_woodcutting_material",
            "harpy_talon_crafting_material",
            "adamant_plate_chest_armor_slot_base",
            "canyon_catfish_raw_fishing_material",
            "gargoyle_heart_shard_alchemy_material",
            "adamant_leggings_leggings_armor_slot_base",
            "subterranean_sawdust_rare_alchemy_ingredient",
            "adamant_helm_helmet_armor_slot_base",
            "adamant_tower_shield_shield_slot_base",
            "zmey_core_scale_unique_regional_boss_material",
            "volcanic_warhammer_blunt_weapon_slot_base",
            "draconic_eye_talisman_amulet_slot_base",
            "obsidian_ore_crafting_material",
            "frosted_down_crafting_material",
            "runed_boots_boots_armor_slot_base",
            "frost_moonflower_herbalism_material",
            "glacial_claw_crafting_material",
            "runed_engraving_ring_1/2_slot_base",
            "ancient_wood_woodcutting_material",
            "runestone_shard_crafting_material",
            "runed_hauberk_chest_armor_slot_base",
            "fjord_shark_raw_fishing_material",
            "ancient_burial_cloth_crafting_material",
            "runed_cuisses_leggings_armor_slot_base",
            "berserker_blood_essence_rare_alchemy_ingredient",
            "runed_greathelm_helmet_armor_slot_base",
            "runed_aegis_shield_slot_base",
            "glacial_hearthstone_unique_regional_boss_material",
            "northern_greataxe_melee_weapon_slot_base",
            "jotunn_ice_medallion_amulet_slot_base",
            "celestial_ore_crafting_material",
            "pure_aurora_filament_crafting_material",
            "transcendent_sollerets_boots_armor_slot_base",
            "golden_ambrosia_herbalism_material",
            "sentinel_alloy_plate_crafting_material",
            "transcendent_loop_ring_1/2_slot_base",
            "yggdrasil_burl_woodcutting_material",
            "prismatic_core_prism_crafting_material",
            "transcendent_cuirass_chest_armor_slot_base",
            "astral_whale_raw_fishing_material",
            "ethereal_shroud_fabric_crafting_material",
            "transcendent_platelegs_leggings_armor_slot_base",
            "revenant_phylactery_rare_alchemy_ingredient",
            "transcendent_armet_helmet_armor_slot_base",
            "transcendent_greatshield_shield_slot_base",
            "lightning_bolt_fragment_ultimate_mythic_upgrade_material",
            "peruns_stormcaller_structural_weapon_slot_base___adapts_to_matching_high_weapon_skill_archetype_upon_compilation",
            "divine_crest_pendant_amulet_slot_base",
            "tin_ore_crafting_material",
            "viper_venom_alchemy_material",
            "bronze_dagger_melee_weapon_slot_base",
            "bog_nightshade_herbalism_material",
            "luminous_dust_alchemy_material",
            "bronze_ring_ring_1/2_slot_base",
            "oak_logs_woodcutting_material",
            "waterlogged_cloth_crafting_material",
            "bronze_cuirass_chest_armor_slot_base",
            "river_trout_raw_fishing_material",
            "essence_of_mists_alchemy_material",
            "bronze_greaves_leggings_armor_slot_base",
            "hag_eye_rare_alchemy_ingredient",
            "bronze_helmet_helmet_armor_slot_base",
            "bronze_buckler_shield_slot_base",
            "regent_ribbon_unique_regional_boss_material",
            "vodnk_harpoon_range_weapon_slot_base",
            "coral_amulet_amulet_slot_base",
            "iron_ore_crafting_material",
            "bat_wing_crafting_material",
            "iron_sabatons_boots_armor_slot_base",
            "cave_moss_herbalism_material",
            "carapace_shard_crafting_material",
            "iron_band_ring_1/2_slot_base",
            "willow_logs_woodcutting_material",
            "chitin_segment_crafting_material",
            "iron_breastplate_chest_armor_slot_base",
            "mud_carp_raw_fishing_material",
            "sulfuric_ash_alchemy_material",
            "iron_platelegs_leggings_armor_slot_base",
            "blasting_powder_rare_crafting_ingredient",
            "iron_armet_helmet_armor_slot_base",
            "iron_kite_shield_shield_slot_base",
            "overlord_crest_unique_regional_boss_material",
            "kobold_sledge_blunt_weapon_slot_base",
            "spike_spatha_melee_weapon_slot_base",
            "coal_node_crafting_material",
            "eagle_feather_crafting_material",
            "steel_sollerets_boots_armor_slot_base",
            "mountain_ginger_herbalism_material",
            "thick_goat_horn_crafting_material",
            "steel_signet_ring_1/2_slot_base",
            "pine_trees_woodcutting_material",
            "frozen_scale_crafting_material",
            "steel_hauberk_chest_armor_slot_base",
            "chasm_pike_raw_fishing_material",
            "core_fragment_alchemy_material",
            "steel_chausses_leggings_armor_slot_base",
            "wyrm_blood_rare_alchemy_ingredient",
            "steel_sallet_helmet_armor_slot_base",
            "steel_heater_shield_shield_slot_base",
            "wurm_eye_unique_regional_boss_material",
            "tatzel_crossbow_range_weapon_slot_base",
            "alpine_talisman_amulet_slot_base",
            "silver_ore_crafting_material",
            "locust_wing_crafting_material",
            "silvered_greaves_boots_armor_slot_base",
            "wild_ginseng_herbalism_material",
            "falcon_talon_crafting_material",
            "silvered_loop_ring_1/2_slot_base",
            "birch_trees_woodcutting_material",
            "desiccated_bone_crafting_material",
            "silvered_cuirass_chest_armor_slot_base",
            "steppe_salmon_raw_fishing_material",
            "arid_core_alchemy_material",
            "silvered_platelegs_leggings_armor_slot_base",
            "specter_dust_rare_alchemy_ingredient",
            "silvered_helm_helmet_armor_slot_base",
            "silvered_pavise_shield_slot_base",
            "noon_shroud_unique_regional_boss_material",
            "poludnica_scythe_melee_weapon_slot_base",
            "solar_pendant_amulet_slot_base",
            "copper_ore_crafting_material",
            "rat_pelt_crafting_material",
            "leather_boots_footwear_armor_slot_base",
            "pond_minnow_raw_cooking_ingredient",
            "lily_root_alchemy_ingredient",
            "leather_hood_helmet_armor_slot_base",
            "field_marigold_herbalism/cooking_ingredient",
            "sharp_claw_crafting_material",
            "wooden_bow_ranged_weapon_slot_base",
            "beech_logs_raw_woodcutting_material",
            "wolf_tooth_crafting_material",
            "leather_tunic_chest_armor_slot_base",
            "thick_wolf_hide_rare_crafting_material",
            "leather_chaps_leggings_armor_slot_base",
            "crude_copper_shield_shield_slot_base",
            "mossy_troll_hide_unique_regional_boss_material",
            "premium_diamond_cluster_guaranteed_currency_payout",
            "troll_club_blunt_weapon_slot_base",
            "copper_greatsword_melee_weapon_slot_base",
            "copper_bar_crafting_material",
            "bronze_bar_crafting_material",
            "iron_bar_crafting_material",
            "steel_bar_crafting_material",
            "silver_bar_crafting_material",
            "gold_bar_crafting_material",
            "mithril_bar_crafting_material",
            "adamantite_bar_crafting_material",
            "obsidian_bar_crafting_material",
            "celestial_bar_crafting_material",
            "cooked_pond_minnow_t1_food",
            "cooked_river_trout_t2_food",
            "cooked_mud_carp_t3_food",
            "cooked_chasm_pike_t4_food",
            "cooked_steppe_salmon_t5_food",
            "cooked_coastline_cod_t6_food",
            "cooked_deep_mire_eel_t7_food",
            "cooked_canyon_catfish_t8_food",
            "cooked_fjord_shark_t9_food",
            "cooked_astral_whale_t10_food",
            "alc_off_t01",
            "alc_def_t01",
            "alc_off_t02",
            "alc_def_t02",
            "alc_off_t03",
            "alc_def_t03",
            "alc_off_t04",
            "alc_def_t04",
            "alc_off_t05",
            "alc_def_t05",
            "alc_off_t06",
            "alc_def_t06",
            "alc_off_t07",
            "alc_def_t07",
            "alc_off_t08",
            "alc_def_t08",
            "alc_off_t09",
            "alc_def_t09",
            "alc_off_t10",
            "alc_def_t10",
        };

        private static readonly MonsterDefinition[] _monsters = new MonsterDefinition[]
        {
            new MonsterDefinition { Id = 1, MaxHp = 32450, AttackPower = 960, BaseGoldReward = 1410, BaseXpReward = 6850, AttackIntervalMs = 2000, LootTableId = 1 },
            new MonsterDefinition { Id = 2, MaxHp = 40320, AttackPower = 1120, BaseGoldReward = 1680, BaseXpReward = 8340, AttackIntervalMs = 1100, LootTableId = 2 },
            new MonsterDefinition { Id = 3, MaxHp = 50480, AttackPower = 1310, BaseGoldReward = 2010, BaseXpReward = 10210, AttackIntervalMs = 1600, LootTableId = 3 },
            new MonsterDefinition { Id = 4, MaxHp = 63650, AttackPower = 1540, BaseGoldReward = 2420, BaseXpReward = 12560, AttackIntervalMs = 1500, LootTableId = 4 },
            new MonsterDefinition { Id = 5, MaxHp = 80720, AttackPower = 1820, BaseGoldReward = 2940, BaseXpReward = 15520, AttackIntervalMs = 1800, LootTableId = 5 },
            new MonsterDefinition { Id = 6, MaxHp = 772140, AttackPower = 4378, BaseGoldReward = 48750, BaseXpReward = 193500, AttackIntervalMs = 2200, LootTableId = 6 },
            new MonsterDefinition { Id = 7, MaxHp = 102450, AttackPower = 2140, BaseGoldReward = 3580, BaseXpReward = 19850, AttackIntervalMs = 1200, LootTableId = 7 },
            new MonsterDefinition { Id = 8, MaxHp = 128640, AttackPower = 2480, BaseGoldReward = 4260, BaseXpReward = 24140, AttackIntervalMs = 2400, LootTableId = 8 },
            new MonsterDefinition { Id = 9, MaxHp = 161850, AttackPower = 2890, BaseGoldReward = 5110, BaseXpReward = 29560, AttackIntervalMs = 1400, LootTableId = 9 },
            new MonsterDefinition { Id = 10, MaxHp = 204320, AttackPower = 3380, BaseGoldReward = 6180, BaseXpReward = 36480, AttackIntervalMs = 3200, LootTableId = 10 },
            new MonsterDefinition { Id = 11, MaxHp = 259450, AttackPower = 3960, BaseGoldReward = 7520, BaseXpReward = 45210, AttackIntervalMs = 1500, LootTableId = 11 },
            new MonsterDefinition { Id = 12, MaxHp = 2812450, AttackPower = 10208, BaseGoldReward = 137250, BaseXpReward = 568500, AttackIntervalMs = 1800, LootTableId = 12 },
            new MonsterDefinition { Id = 13, MaxHp = 328450, AttackPower = 4680, BaseGoldReward = 9450, BaseXpReward = 58450, AttackIntervalMs = 1100, LootTableId = 13 },
            new MonsterDefinition { Id = 14, MaxHp = 412320, AttackPower = 5340, BaseGoldReward = 11260, BaseXpReward = 71200, AttackIntervalMs = 1400, LootTableId = 14 },
            new MonsterDefinition { Id = 15, MaxHp = 518450, AttackPower = 6120, BaseGoldReward = 13480, BaseXpReward = 86950, AttackIntervalMs = 1300, LootTableId = 15 },
            new MonsterDefinition { Id = 16, MaxHp = 654320, AttackPower = 7050, BaseGoldReward = 16120, BaseXpReward = 106450, AttackIntervalMs = 2800, LootTableId = 16 },
            new MonsterDefinition { Id = 17, MaxHp = 829450, AttackPower = 8140, BaseGoldReward = 19450, BaseXpReward = 130420, AttackIntervalMs = 1600, LootTableId = 17 },
            new MonsterDefinition { Id = 18, MaxHp = 8984250, AttackPower = 20680, BaseGoldReward = 354250, BaseXpReward = 1604500, AttackIntervalMs = 2500, LootTableId = 18 },
            new MonsterDefinition { Id = 19, MaxHp = 1045600, AttackPower = 9840, BaseGoldReward = 24500, BaseXpReward = 215000, AttackIntervalMs = 1000, LootTableId = 19 },
            new MonsterDefinition { Id = 20, MaxHp = 1312400, AttackPower = 11450, BaseGoldReward = 29150, BaseXpReward = 262400, AttackIntervalMs = 2300, LootTableId = 20 },
            new MonsterDefinition { Id = 21, MaxHp = 1648500, AttackPower = 13240, BaseGoldReward = 35040, BaseXpReward = 321500, AttackIntervalMs = 3000, LootTableId = 21 },
            new MonsterDefinition { Id = 22, MaxHp = 2084200, AttackPower = 15310, BaseGoldReward = 42160, BaseXpReward = 395400, AttackIntervalMs = 1700, LootTableId = 22 },
            new MonsterDefinition { Id = 23, MaxHp = 2642500, AttackPower = 17840, BaseGoldReward = 51240, BaseXpReward = 488500, AttackIntervalMs = 1400, LootTableId = 23 },
            new MonsterDefinition { Id = 24, MaxHp = 28124500, AttackPower = 45408, BaseGoldReward = 937500, BaseXpReward = 6045000, AttackIntervalMs = 2600, LootTableId = 24 },
            new MonsterDefinition { Id = 25, MaxHp = 3450600, AttackPower = 21450, BaseGoldReward = 64500, BaseXpReward = 815000, AttackIntervalMs = 900, LootTableId = 25 },
            new MonsterDefinition { Id = 26, MaxHp = 4324800, AttackPower = 24960, BaseGoldReward = 78200, BaseXpReward = 1024500, AttackIntervalMs = 1800, LootTableId = 26 },
            new MonsterDefinition { Id = 27, MaxHp = 5485000, AttackPower = 29120, BaseGoldReward = 95400, BaseXpReward = 1312000, AttackIntervalMs = 2800, LootTableId = 27 },
            new MonsterDefinition { Id = 28, MaxHp = 6984200, AttackPower = 34140, BaseGoldReward = 118500, BaseXpReward = 1694500, AttackIntervalMs = 1200, LootTableId = 28 },
            new MonsterDefinition { Id = 29, MaxHp = 8945600, AttackPower = 40240, BaseGoldReward = 148200, BaseXpReward = 2192000, AttackIntervalMs = 1500, LootTableId = 29 },
            new MonsterDefinition { Id = 30, MaxHp = 95842500, AttackPower = 102960, BaseGoldReward = 2850000, BaseXpReward = 28540000, AttackIntervalMs = 2000, LootTableId = 30 },
            new MonsterDefinition { Id = 31, MaxHp = 403, AttackPower = 47, BaseGoldReward = 34, BaseXpReward = 98, AttackIntervalMs = 1600, LootTableId = 31 },
            new MonsterDefinition { Id = 32, MaxHp = 494, AttackPower = 55, BaseGoldReward = 40, BaseXpReward = 119, AttackIntervalMs = 1000, LootTableId = 32 },
            new MonsterDefinition { Id = 33, MaxHp = 611, AttackPower = 65, BaseGoldReward = 48, BaseXpReward = 145, AttackIntervalMs = 2500, LootTableId = 33 },
            new MonsterDefinition { Id = 34, MaxHp = 764, AttackPower = 76, BaseGoldReward = 58, BaseXpReward = 179, AttackIntervalMs = 1200, LootTableId = 34 },
            new MonsterDefinition { Id = 35, MaxHp = 963, AttackPower = 89, BaseGoldReward = 69, BaseXpReward = 222, AttackIntervalMs = 1800, LootTableId = 35 },
            new MonsterDefinition { Id = 36, MaxHp = 9214, AttackPower = 211, BaseGoldReward = 1140, BaseXpReward = 2420, AttackIntervalMs = 2000, LootTableId = 36 },
            new MonsterDefinition { Id = 37, MaxHp = 1234, AttackPower = 104, BaseGoldReward = 83, BaseXpReward = 277, AttackIntervalMs = 1400, LootTableId = 37 },
            new MonsterDefinition { Id = 38, MaxHp = 1514, AttackPower = 120, BaseGoldReward = 98, BaseXpReward = 338, AttackIntervalMs = 2800, LootTableId = 38 },
            new MonsterDefinition { Id = 39, MaxHp = 1867, AttackPower = 140, BaseGoldReward = 118, BaseXpReward = 416, AttackIntervalMs = 2200, LootTableId = 39 },
            new MonsterDefinition { Id = 40, MaxHp = 2315, AttackPower = 162, BaseGoldReward = 142, BaseXpReward = 515, AttackIntervalMs = 1100, LootTableId = 40 },
            new MonsterDefinition { Id = 41, MaxHp = 2883, AttackPower = 189, BaseGoldReward = 171, BaseXpReward = 642, AttackIntervalMs = 1600, LootTableId = 41 },
            new MonsterDefinition { Id = 42, MaxHp = 27242, AttackPower = 448, BaseGoldReward = 2820, BaseXpReward = 7120, AttackIntervalMs = 2400, LootTableId = 42 },
            new MonsterDefinition { Id = 43, MaxHp = 3678, AttackPower = 221, BaseGoldReward = 212, BaseXpReward = 823, AttackIntervalMs = 1300, LootTableId = 43 },
            new MonsterDefinition { Id = 44, MaxHp = 4528, AttackPower = 255, BaseGoldReward = 254, BaseXpReward = 1012, AttackIntervalMs = 2000, LootTableId = 44 },
            new MonsterDefinition { Id = 45, MaxHp = 5595, AttackPower = 296, BaseGoldReward = 305, BaseXpReward = 1250, AttackIntervalMs = 1500, LootTableId = 45 },
            new MonsterDefinition { Id = 46, MaxHp = 6937, AttackPower = 344, BaseGoldReward = 368, BaseXpReward = 1552, AttackIntervalMs = 3000, LootTableId = 46 },
            new MonsterDefinition { Id = 47, MaxHp = 8629, AttackPower = 400, BaseGoldReward = 445, BaseXpReward = 1935, AttackIntervalMs = 1700, LootTableId = 47 },
            new MonsterDefinition { Id = 48, MaxHp = 82144, AttackPower = 954, BaseGoldReward = 7425, BaseXpReward = 21780, AttackIntervalMs = 2100, LootTableId = 48 },
            new MonsterDefinition { Id = 49, MaxHp = 10879, AttackPower = 468, BaseGoldReward = 550, BaseXpReward = 2504, AttackIntervalMs = 800, LootTableId = 49 },
            new MonsterDefinition { Id = 50, MaxHp = 13421, AttackPower = 541, BaseGoldReward = 661, BaseXpReward = 3105, AttackIntervalMs = 1200, LootTableId = 50 },
            new MonsterDefinition { Id = 51, MaxHp = 16608, AttackPower = 627, BaseGoldReward = 797, BaseXpReward = 3865, AttackIntervalMs = 1900, LootTableId = 51 },
            new MonsterDefinition { Id = 52, MaxHp = 20612, AttackPower = 728, BaseGoldReward = 964, BaseXpReward = 4825, AttackIntervalMs = 1500, LootTableId = 52 },
            new MonsterDefinition { Id = 53, MaxHp = 25654, AttackPower = 847, BaseGoldReward = 1170, BaseXpReward = 6045, AttackIntervalMs = 1400, LootTableId = 53 },
            new MonsterDefinition { Id = 54, MaxHp = 245326, AttackPower = 2028, BaseGoldReward = 19650, BaseXpReward = 68540, AttackIntervalMs = 1600, LootTableId = 54 },
            new MonsterDefinition { Id = 55, MaxHp = 69, AttackPower = 9, BaseGoldReward = 7, BaseXpReward = 21, AttackIntervalMs = 2000, LootTableId = 55 },
            new MonsterDefinition { Id = 56, MaxHp = 129, AttackPower = 16, BaseGoldReward = 12, BaseXpReward = 34, AttackIntervalMs = 1800, LootTableId = 56 },
            new MonsterDefinition { Id = 57, MaxHp = 192, AttackPower = 23, BaseGoldReward = 17, BaseXpReward = 49, AttackIntervalMs = 1200, LootTableId = 57 },
            new MonsterDefinition { Id = 58, MaxHp = 258, AttackPower = 31, BaseGoldReward = 23, BaseXpReward = 64, AttackIntervalMs = 2200, LootTableId = 58 },
            new MonsterDefinition { Id = 59, MaxHp = 327, AttackPower = 38, BaseGoldReward = 28, BaseXpReward = 80, AttackIntervalMs = 1500, LootTableId = 59 },
            new MonsterDefinition { Id = 60, MaxHp = 3085, AttackPower = 92, BaseGoldReward = 465, BaseXpReward = 880, AttackIntervalMs = 3500, LootTableId = 60 },
        };

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

        private static readonly ItemDefinition[] _itemDefinitions = new ItemDefinition[]
        {
            new ItemDefinition { Id = 1, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 0 }, // gold_ore_crafting_material
            new ItemDefinition { Id = 2, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // highland_wool_crafting_material
            new ItemDefinition { Id = 3, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 68 }, // gilded_sabatons_boots_armor_slot_base
            new ItemDefinition { Id = 4, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // premium_diamond
            new ItemDefinition { Id = 5, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // salt_lotus_herbalism_material
            new ItemDefinition { Id = 6, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // ominous_feather_crafting_material
            new ItemDefinition { Id = 7, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 0 }, // gilded_band_ring_1/2_slot_base
            new ItemDefinition { Id = 8, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // maple_trees_woodcutting_material
            new ItemDefinition { Id = 9, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // condensation_essence_alchemy_material
            new ItemDefinition { Id = 10, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 68 }, // gilded_hauberk_chest_armor_slot_base
            new ItemDefinition { Id = 11, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // coastline_cod_raw_fishing_material
            new ItemDefinition { Id = 12, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // selkie_skin_fragment_crafting_material
            new ItemDefinition { Id = 13, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 68 }, // gilded_chausses_leggings_armor_slot_base
            new ItemDefinition { Id = 14, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // peat_clump_rare_alchemy_ingredient
            new ItemDefinition { Id = 15, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 68 }, // gilded_sallet_helmet_armor_slot_base
            new ItemDefinition { Id = 16, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 68 }, // gilded_round_shield_shield_slot_base
            new ItemDefinition { Id = 17, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // kelpie_mane_unique_regional_boss_material
            new ItemDefinition { Id = 18, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // premium_diamond_cluster
            new ItemDefinition { Id = 19, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 137, FlatDefenseRating = 0 }, // loch_crossbow_range_weapon_slot_base
            new ItemDefinition { Id = 20, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // abyssal_pearl_amulet_slot_base
            new ItemDefinition { Id = 21, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 0 }, // mithril_ore_crafting_material
            new ItemDefinition { Id = 22, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // shadow_feather_crafting_material
            new ItemDefinition { Id = 23, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 94 }, // mithril_greaves_boots_armor_slot_base
            new ItemDefinition { Id = 24, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // screaming_mandrake_herbalism_material
            new ItemDefinition { Id = 25, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // tainted_tusk_crafting_material
            new ItemDefinition { Id = 26, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 0 }, // mithril_signet_ring_1/2_slot_base
            new ItemDefinition { Id = 27, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // yew_trees_woodcutting_material
            new ItemDefinition { Id = 28, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // spore_pod_alchemy_material
            new ItemDefinition { Id = 29, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 94 }, // mithril_cuirass_chest_armor_slot_base
            new ItemDefinition { Id = 30, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // deep_mire_eel_raw_fishing_material
            new ItemDefinition { Id = 31, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // heartwood_core_alchemy_material
            new ItemDefinition { Id = 32, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 94 }, // mithril_platelegs_leggings_armor_slot_base
            new ItemDefinition { Id = 33, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // schrat_horn_rare_alchemy_ingredient
            new ItemDefinition { Id = 34, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 94 }, // mithril_armet_helmet_armor_slot_base
            new ItemDefinition { Id = 35, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 94 }, // mithril_scutum_shield_slot_base
            new ItemDefinition { Id = 36, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // spine_whip_link_unique_regional_boss_material
            new ItemDefinition { Id = 37, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 188, FlatDefenseRating = 0 }, // dullahan_greatsword_melee_weapon_slot_base
            new ItemDefinition { Id = 38, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // spinal_collar_amulet_slot_base
            new ItemDefinition { Id = 39, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 0 }, // adamantite_ore_crafting_material
            new ItemDefinition { Id = 40, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // scorpion_stinger_crafting_material
            new ItemDefinition { Id = 41, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 127 }, // adamant_sollerets_boots_armor_slot_base
            new ItemDefinition { Id = 42, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // jagged_bloodgrass_herbalism_material
            new ItemDefinition { Id = 43, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // crystallized_venom_crafting_material
            new ItemDefinition { Id = 44, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 0 }, // adamant_loop_ring_1/2_slot_base
            new ItemDefinition { Id = 45, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // elder_trees_woodcutting_material
            new ItemDefinition { Id = 46, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // harpy_talon_crafting_material
            new ItemDefinition { Id = 47, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 127 }, // adamant_plate_chest_armor_slot_base
            new ItemDefinition { Id = 48, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // canyon_catfish_raw_fishing_material
            new ItemDefinition { Id = 49, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // gargoyle_heart_shard_alchemy_material
            new ItemDefinition { Id = 50, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 127 }, // adamant_leggings_leggings_armor_slot_base
            new ItemDefinition { Id = 51, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // subterranean_sawdust_rare_alchemy_ingredient
            new ItemDefinition { Id = 52, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 127 }, // adamant_helm_helmet_armor_slot_base
            new ItemDefinition { Id = 53, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 127 }, // adamant_tower_shield_shield_slot_base
            new ItemDefinition { Id = 54, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // zmey_core_scale_unique_regional_boss_material
            new ItemDefinition { Id = 55, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 254, FlatDefenseRating = 0 }, // volcanic_warhammer_blunt_weapon_slot_base
            new ItemDefinition { Id = 56, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // draconic_eye_talisman_amulet_slot_base
            new ItemDefinition { Id = 57, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 0 }, // obsidian_ore_crafting_material
            new ItemDefinition { Id = 58, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // frosted_down_crafting_material
            new ItemDefinition { Id = 59, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 169 }, // runed_boots_boots_armor_slot_base
            new ItemDefinition { Id = 60, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // frost_moonflower_herbalism_material
            new ItemDefinition { Id = 61, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // glacial_claw_crafting_material
            new ItemDefinition { Id = 62, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 0 }, // runed_engraving_ring_1/2_slot_base
            new ItemDefinition { Id = 63, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // ancient_wood_woodcutting_material
            new ItemDefinition { Id = 64, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // runestone_shard_crafting_material
            new ItemDefinition { Id = 65, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 169 }, // runed_hauberk_chest_armor_slot_base
            new ItemDefinition { Id = 66, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // fjord_shark_raw_fishing_material
            new ItemDefinition { Id = 67, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // ancient_burial_cloth_crafting_material
            new ItemDefinition { Id = 68, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 169 }, // runed_cuisses_leggings_armor_slot_base
            new ItemDefinition { Id = 69, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // berserker_blood_essence_rare_alchemy_ingredient
            new ItemDefinition { Id = 70, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 169 }, // runed_greathelm_helmet_armor_slot_base
            new ItemDefinition { Id = 71, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 169 }, // runed_aegis_shield_slot_base
            new ItemDefinition { Id = 72, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // glacial_hearthstone_unique_regional_boss_material
            new ItemDefinition { Id = 73, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 338, FlatDefenseRating = 0 }, // northern_greataxe_melee_weapon_slot_base
            new ItemDefinition { Id = 74, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // jotunn_ice_medallion_amulet_slot_base
            new ItemDefinition { Id = 75, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 0 }, // celestial_ore_crafting_material
            new ItemDefinition { Id = 76, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // pure_aurora_filament_crafting_material
            new ItemDefinition { Id = 77, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 221 }, // transcendent_sollerets_boots_armor_slot_base
            new ItemDefinition { Id = 78, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 0 }, // golden_ambrosia_herbalism_material
            new ItemDefinition { Id = 79, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // sentinel_alloy_plate_crafting_material
            new ItemDefinition { Id = 80, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 0 }, // transcendent_loop_ring_1/2_slot_base
            new ItemDefinition { Id = 81, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // yggdrasil_burl_woodcutting_material
            new ItemDefinition { Id = 82, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // prismatic_core_prism_crafting_material
            new ItemDefinition { Id = 83, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 221 }, // transcendent_cuirass_chest_armor_slot_base
            new ItemDefinition { Id = 84, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // astral_whale_raw_fishing_material
            new ItemDefinition { Id = 85, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // ethereal_shroud_fabric_crafting_material
            new ItemDefinition { Id = 86, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 221 }, // transcendent_platelegs_leggings_armor_slot_base
            new ItemDefinition { Id = 87, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // revenant_phylactery_rare_alchemy_ingredient
            new ItemDefinition { Id = 88, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 221 }, // transcendent_armet_helmet_armor_slot_base
            new ItemDefinition { Id = 89, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 221 }, // transcendent_greatshield_shield_slot_base
            new ItemDefinition { Id = 90, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // lightning_bolt_fragment_ultimate_mythic_upgrade_material
            new ItemDefinition { Id = 91, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 443, FlatDefenseRating = 0 }, // peruns_stormcaller_structural_weapon_slot_base___adapts_to_matching_high_weapon_skill_archetype_upon_compilation
            new ItemDefinition { Id = 92, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // divine_crest_pendant_amulet_slot_base
            new ItemDefinition { Id = 93, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // tin_ore_crafting_material
            new ItemDefinition { Id = 94, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // viper_venom_alchemy_material
            new ItemDefinition { Id = 95, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 23, FlatDefenseRating = 0 }, // bronze_dagger_melee_weapon_slot_base
            new ItemDefinition { Id = 96, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // bog_nightshade_herbalism_material
            new ItemDefinition { Id = 97, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // luminous_dust_alchemy_material
            new ItemDefinition { Id = 98, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 0 }, // bronze_ring_ring_1/2_slot_base
            new ItemDefinition { Id = 99, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // oak_logs_woodcutting_material
            new ItemDefinition { Id = 100, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // waterlogged_cloth_crafting_material
            new ItemDefinition { Id = 101, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 11 }, // bronze_cuirass_chest_armor_slot_base
            new ItemDefinition { Id = 102, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // river_trout_raw_fishing_material
            new ItemDefinition { Id = 103, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // essence_of_mists_alchemy_material
            new ItemDefinition { Id = 104, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 11 }, // bronze_greaves_leggings_armor_slot_base
            new ItemDefinition { Id = 105, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // hag_eye_rare_alchemy_ingredient
            new ItemDefinition { Id = 106, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 11 }, // bronze_helmet_helmet_armor_slot_base
            new ItemDefinition { Id = 107, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 11 }, // bronze_buckler_shield_slot_base
            new ItemDefinition { Id = 108, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // regent_ribbon_unique_regional_boss_material
            new ItemDefinition { Id = 109, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 10, FlatDefenseRating = 0 }, // vodnk_harpoon_range_weapon_slot_base
            new ItemDefinition { Id = 110, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // coral_amulet_amulet_slot_base
            new ItemDefinition { Id = 111, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 0 }, // iron_ore_crafting_material
            new ItemDefinition { Id = 112, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // bat_wing_crafting_material
            new ItemDefinition { Id = 113, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 20 }, // iron_sabatons_boots_armor_slot_base
            new ItemDefinition { Id = 114, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cave_moss_herbalism_material
            new ItemDefinition { Id = 115, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // carapace_shard_crafting_material
            new ItemDefinition { Id = 116, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 0 }, // iron_band_ring_1/2_slot_base
            new ItemDefinition { Id = 117, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // willow_logs_woodcutting_material
            new ItemDefinition { Id = 118, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // chitin_segment_crafting_material
            new ItemDefinition { Id = 119, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 20 }, // iron_breastplate_chest_armor_slot_base
            new ItemDefinition { Id = 120, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // mud_carp_raw_fishing_material
            new ItemDefinition { Id = 121, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // sulfuric_ash_alchemy_material
            new ItemDefinition { Id = 122, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 20 }, // iron_platelegs_leggings_armor_slot_base
            new ItemDefinition { Id = 123, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // blasting_powder_rare_crafting_ingredient
            new ItemDefinition { Id = 124, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 20 }, // iron_armet_helmet_armor_slot_base
            new ItemDefinition { Id = 125, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 20 }, // iron_kite_shield_shield_slot_base
            new ItemDefinition { Id = 126, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // overlord_crest_unique_regional_boss_material
            new ItemDefinition { Id = 127, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 41, FlatDefenseRating = 0 }, // kobold_sledge_blunt_weapon_slot_base
            new ItemDefinition { Id = 128, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 41, FlatDefenseRating = 0 }, // spike_spatha_melee_weapon_slot_base
            new ItemDefinition { Id = 129, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // coal_node_crafting_material
            new ItemDefinition { Id = 130, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // eagle_feather_crafting_material
            new ItemDefinition { Id = 131, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 32 }, // steel_sollerets_boots_armor_slot_base
            new ItemDefinition { Id = 132, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // mountain_ginger_herbalism_material
            new ItemDefinition { Id = 133, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // thick_goat_horn_crafting_material
            new ItemDefinition { Id = 134, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 0 }, // steel_signet_ring_1/2_slot_base
            new ItemDefinition { Id = 135, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // pine_trees_woodcutting_material
            new ItemDefinition { Id = 136, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // frozen_scale_crafting_material
            new ItemDefinition { Id = 137, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 32 }, // steel_hauberk_chest_armor_slot_base
            new ItemDefinition { Id = 138, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // chasm_pike_raw_fishing_material
            new ItemDefinition { Id = 139, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // core_fragment_alchemy_material
            new ItemDefinition { Id = 140, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 32 }, // steel_chausses_leggings_armor_slot_base
            new ItemDefinition { Id = 141, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // wyrm_blood_rare_alchemy_ingredient
            new ItemDefinition { Id = 142, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 32 }, // steel_sallet_helmet_armor_slot_base
            new ItemDefinition { Id = 143, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 32 }, // steel_heater_shield_shield_slot_base
            new ItemDefinition { Id = 144, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // wurm_eye_unique_regional_boss_material
            new ItemDefinition { Id = 145, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 65, FlatDefenseRating = 0 }, // tatzel_crossbow_range_weapon_slot_base
            new ItemDefinition { Id = 146, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alpine_talisman_amulet_slot_base
            new ItemDefinition { Id = 147, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 0 }, // silver_ore_crafting_material
            new ItemDefinition { Id = 148, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // locust_wing_crafting_material
            new ItemDefinition { Id = 149, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 48 }, // silvered_greaves_boots_armor_slot_base
            new ItemDefinition { Id = 150, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // wild_ginseng_herbalism_material
            new ItemDefinition { Id = 151, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // falcon_talon_crafting_material
            new ItemDefinition { Id = 152, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 0 }, // silvered_loop_ring_1/2_slot_base
            new ItemDefinition { Id = 153, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // birch_trees_woodcutting_material
            new ItemDefinition { Id = 154, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // desiccated_bone_crafting_material
            new ItemDefinition { Id = 155, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 48 }, // silvered_cuirass_chest_armor_slot_base
            new ItemDefinition { Id = 156, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // steppe_salmon_raw_fishing_material
            new ItemDefinition { Id = 157, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // arid_core_alchemy_material
            new ItemDefinition { Id = 158, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 48 }, // silvered_platelegs_leggings_armor_slot_base
            new ItemDefinition { Id = 159, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // specter_dust_rare_alchemy_ingredient
            new ItemDefinition { Id = 160, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 48 }, // silvered_helm_helmet_armor_slot_base
            new ItemDefinition { Id = 161, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 48 }, // silvered_pavise_shield_slot_base
            new ItemDefinition { Id = 162, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // noon_shroud_unique_regional_boss_material
            new ItemDefinition { Id = 163, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 96, FlatDefenseRating = 0 }, // poludnica_scythe_melee_weapon_slot_base
            new ItemDefinition { Id = 164, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // solar_pendant_amulet_slot_base
            new ItemDefinition { Id = 165, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // copper_ore_crafting_material
            new ItemDefinition { Id = 166, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // rat_pelt_crafting_material
            new ItemDefinition { Id = 167, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 5 }, // leather_boots_footwear_armor_slot_base
            new ItemDefinition { Id = 168, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // pond_minnow_raw_cooking_ingredient
            new ItemDefinition { Id = 169, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // lily_root_alchemy_ingredient
            new ItemDefinition { Id = 170, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 5 }, // leather_hood_helmet_armor_slot_base
            new ItemDefinition { Id = 171, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // field_marigold_herbalism/cooking_ingredient
            new ItemDefinition { Id = 172, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // sharp_claw_crafting_material
            new ItemDefinition { Id = 173, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 10, FlatDefenseRating = 0 }, // wooden_bow_ranged_weapon_slot_base
            new ItemDefinition { Id = 174, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // beech_logs_raw_woodcutting_material
            new ItemDefinition { Id = 175, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // wolf_tooth_crafting_material
            new ItemDefinition { Id = 176, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 5 }, // leather_tunic_chest_armor_slot_base
            new ItemDefinition { Id = 177, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // thick_wolf_hide_rare_crafting_material
            new ItemDefinition { Id = 178, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 5 }, // leather_chaps_leggings_armor_slot_base
            new ItemDefinition { Id = 179, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 5 }, // crude_copper_shield_shield_slot_base
            new ItemDefinition { Id = 180, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // mossy_troll_hide_unique_regional_boss_material
            new ItemDefinition { Id = 181, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // premium_diamond_cluster_guaranteed_currency_payout
            new ItemDefinition { Id = 182, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 10, FlatDefenseRating = 0 }, // troll_club_blunt_weapon_slot_base
            new ItemDefinition { Id = 183, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 10, FlatDefenseRating = 0 }, // copper_greatsword_melee_weapon_slot_base
            new ItemDefinition { Id = 184, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // copper_bar_crafting_material
            new ItemDefinition { Id = 185, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 0 }, // bronze_bar_crafting_material
            new ItemDefinition { Id = 186, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 0 }, // iron_bar_crafting_material
            new ItemDefinition { Id = 187, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 0 }, // steel_bar_crafting_material
            new ItemDefinition { Id = 188, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 0 }, // silver_bar_crafting_material
            new ItemDefinition { Id = 189, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 0 }, // gold_bar_crafting_material
            new ItemDefinition { Id = 190, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 0 }, // mithril_bar_crafting_material
            new ItemDefinition { Id = 191, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 0 }, // adamantite_bar_crafting_material
            new ItemDefinition { Id = 192, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 0 }, // obsidian_bar_crafting_material
            new ItemDefinition { Id = 193, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 0 }, // celestial_bar_crafting_material
            new ItemDefinition { Id = 194, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_pond_minnow_t1_food
            new ItemDefinition { Id = 195, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_river_trout_t2_food
            new ItemDefinition { Id = 196, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_mud_carp_t3_food
            new ItemDefinition { Id = 197, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_chasm_pike_t4_food
            new ItemDefinition { Id = 198, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_steppe_salmon_t5_food
            new ItemDefinition { Id = 199, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_coastline_cod_t6_food
            new ItemDefinition { Id = 200, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_deep_mire_eel_t7_food
            new ItemDefinition { Id = 201, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_canyon_catfish_t8_food
            new ItemDefinition { Id = 202, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_fjord_shark_t9_food
            new ItemDefinition { Id = 203, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 0 }, // cooked_astral_whale_t10_food
            new ItemDefinition { Id = 204, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t01
            new ItemDefinition { Id = 205, RegionTier = 1, BaseValueGold = 10, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t01
            new ItemDefinition { Id = 206, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t02
            new ItemDefinition { Id = 207, RegionTier = 2, BaseValueGold = 40, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t02
            new ItemDefinition { Id = 208, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t03
            new ItemDefinition { Id = 209, RegionTier = 3, BaseValueGold = 90, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t03
            new ItemDefinition { Id = 210, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t04
            new ItemDefinition { Id = 211, RegionTier = 4, BaseValueGold = 160, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t04
            new ItemDefinition { Id = 212, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t05
            new ItemDefinition { Id = 213, RegionTier = 5, BaseValueGold = 250, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t05
            new ItemDefinition { Id = 214, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t06
            new ItemDefinition { Id = 215, RegionTier = 6, BaseValueGold = 360, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t06
            new ItemDefinition { Id = 216, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t07
            new ItemDefinition { Id = 217, RegionTier = 7, BaseValueGold = 490, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t07
            new ItemDefinition { Id = 218, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t08
            new ItemDefinition { Id = 219, RegionTier = 8, BaseValueGold = 640, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t08
            new ItemDefinition { Id = 220, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t09
            new ItemDefinition { Id = 221, RegionTier = 9, BaseValueGold = 810, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t09
            new ItemDefinition { Id = 222, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_off_t10
            new ItemDefinition { Id = 223, RegionTier = 10, BaseValueGold = 1000, FlatAttackPower = 0, FlatDefenseRating = 0 }, // alc_def_t10
        };

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

        private static readonly GatheringNodeDefinition[] _gatheringNodes = new GatheringNodeDefinition[]
        {
            new GatheringNodeDefinition { ActivityId = 101, ProfessionType = 0, BaseTickThreshold = 30, BaseMasteryXpReward = 15 },
            new GatheringNodeDefinition { ActivityId = 102, ProfessionType = 0, BaseTickThreshold = 45, BaseMasteryXpReward = 30 },
            new GatheringNodeDefinition { ActivityId = 103, ProfessionType = 0, BaseTickThreshold = 60, BaseMasteryXpReward = 55 },
            new GatheringNodeDefinition { ActivityId = 104, ProfessionType = 0, BaseTickThreshold = 80, BaseMasteryXpReward = 90 },
            new GatheringNodeDefinition { ActivityId = 105, ProfessionType = 0, BaseTickThreshold = 100, BaseMasteryXpReward = 140 },
            new GatheringNodeDefinition { ActivityId = 201, ProfessionType = 1, BaseTickThreshold = 30, BaseMasteryXpReward = 15 },
            new GatheringNodeDefinition { ActivityId = 202, ProfessionType = 1, BaseTickThreshold = 45, BaseMasteryXpReward = 30 },
            new GatheringNodeDefinition { ActivityId = 203, ProfessionType = 1, BaseTickThreshold = 60, BaseMasteryXpReward = 55 },
            new GatheringNodeDefinition { ActivityId = 204, ProfessionType = 1, BaseTickThreshold = 80, BaseMasteryXpReward = 90 },
            new GatheringNodeDefinition { ActivityId = 205, ProfessionType = 1, BaseTickThreshold = 100, BaseMasteryXpReward = 140 },
        };

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
    }



}
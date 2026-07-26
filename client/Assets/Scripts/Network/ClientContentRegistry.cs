using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace FolkIdle.Client.Network
{
    public sealed class MonsterEntry
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

        // Modul: UI rework. Which of the game's regions this monster
        // belongs to. Already authored in GameData/monsters.json and read by
        // the server (ContentRegistry.GetMonsterRegionTier, which drives
        // region completion and the Codex region rows) but simply absent
        // from this client-side mirror, so the client had no idea which
        // monsters lived where - which is why the old Combat screen offered
        // one flat dropdown of every discovered monster under all five
        // region rows alike.
        public int RegionTier { get; set; }

        public int Armor { get; set; }
        public int DodgeRating { get; set; }
    }

    public sealed class ItemEntry
    {
        public int Id { get; set; }
        public int RegionTier { get; set; }
        public int BaseValueGold { get; set; }
        public int FlatAttackPower { get; set; }
        public int FlatDefenseRating { get; set; }
        public string BaseId { get; set; } = string.Empty;
    }

    public sealed class GatheringNodeEntry
    {
        public long ActivityId { get; set; }
        public int ProfessionType { get; set; }
        public int BaseTickThreshold { get; set; }
        public int BaseMasteryXpReward { get; set; }
    }

    public sealed class SkillEntry
    {
        public int SkillId { get; set; }
        public int ManaCost { get; set; }
        public int CooldownMs { get; set; }
        public int DamageMultiplierPct { get; set; }
        public int RequiredSkillPointCost { get; set; }
    }

    // Client mirror of the server's ContentRegistry/ActiveSkillEngine JSON
    // content pipeline (see server/GameData/*.json, mirrored verbatim into
    // StreamingAssets/GameData). Parses those same files once at boot so the
    // UI never hand-duplicates a balance number that could silently drift
    // from the server's real values - client and server read the exact same
    // JSON, just from different filesystem locations.
    //
    // Not hot-path: nothing in Unity's Update loop reads this every frame
    // the way SimulationEngine's 10Hz tick reads ContentRegistry
    // server-side, so plain dictionaries built once at load are sufficient.
    // Unlike the server, this does not require dense Id-1 array indexing or
    // strict ID-contiguity validation, since nothing here does unsafe direct
    // array access - Dictionary lookups are simpler and equally correct for
    // UI-frequency reads.
    public static class ClientContentRegistry
    {
        private static bool _isInitialized;

        private static readonly Dictionary<int, MonsterEntry> _monsters = new();
        private static readonly Dictionary<int, ItemEntry> _items = new();
        private static readonly Dictionary<string, ItemEntry> _itemsByBaseId = new();
        private static readonly Dictionary<long, GatheringNodeEntry> _gatheringNodes = new();
        private static readonly Dictionary<int, SkillEntry> _skills = new();

        public static void Initialize()
        {
            if (_isInitialized) return;

            string gameDataDir = Path.Combine(Application.streamingAssetsPath, "GameData");

            List<MonsterEntry> monsters = LoadList<MonsterEntry>(Path.Combine(gameDataDir, "monsters.json"));
            foreach (MonsterEntry monster in monsters)
            {
                _monsters[monster.Id] = monster;
            }

            List<ItemEntry> items = LoadList<ItemEntry>(Path.Combine(gameDataDir, "items.json"));
            foreach (ItemEntry item in items)
            {
                _items[item.Id] = item;
                _itemsByBaseId[item.BaseId] = item;
            }

            List<GatheringNodeEntry> gatheringNodes = LoadList<GatheringNodeEntry>(Path.Combine(gameDataDir, "gathering_nodes.json"));
            foreach (GatheringNodeEntry node in gatheringNodes)
            {
                _gatheringNodes[node.ActivityId] = node;
            }

            List<SkillEntry> skills = LoadList<SkillEntry>(Path.Combine(gameDataDir, "skills.json"));
            foreach (SkillEntry skill in skills)
            {
                _skills[skill.SkillId] = skill;
            }

            BuildDerivedIndexes();

            _isInitialized = true;
        }

        public static MonsterEntry GetMonster(int id)
        {
            if (_monsters.TryGetValue(id, out MonsterEntry monster)) return monster;
            throw new KeyNotFoundException($"ClientContentRegistry: no monster with Id {id}.");
        }

        public static string GetMonsterName(int id) => GetMonster(id).Name;

        public static ItemEntry GetItem(int id)
        {
            if (_items.TryGetValue(id, out ItemEntry item)) return item;
            throw new KeyNotFoundException($"ClientContentRegistry: no item with Id {id}.");
        }

        public static bool TryGetItemByBaseId(string baseId, out ItemEntry item) => _itemsByBaseId.TryGetValue(baseId, out item);

        public static bool TryGetMonster(int id, out MonsterEntry monster) => _monsters.TryGetValue(id, out monster);

        public static bool TryGetItemById(int id, out ItemEntry item) => _items.TryGetValue(id, out item);

        // ------------------------------------------------------------
        // Modul: UI rework - region/location model for the Combat screen.
        //
        // There is no "region" table anywhere: a region is defined purely by
        // which monsters carry that RegionTier, exactly as the server's own
        // region-progress endpoint derives it (see
        // NetworkBroadcastSystem.HandleCodexRegionsSnapshot, which walks
        // every monster and groups by GetMonsterRegionTier). This mirrors
        // that derivation rather than inventing a parallel content file that
        // could drift from it.
        // ------------------------------------------------------------
        private static readonly Dictionary<int, List<MonsterEntry>> _monstersByRegion = new();
        private static readonly List<int> _regionIds = new();

        public static IReadOnlyList<int> RegionIds => _regionIds;

        // Monsters of one region, weakest first. The last entry is the
        // region's boss - see IsRegionBoss.
        public static IReadOnlyList<MonsterEntry> GetMonstersInRegion(int regionId)
        {
            return _monstersByRegion.TryGetValue(regionId, out List<MonsterEntry> list)
                ? list
                : System.Array.Empty<MonsterEntry>();
        }

        // The single toughest monster in a region. Every authored region
        // ends in one dramatically higher-HP entry (Kelpie Mare at 772k
        // against ~80k for the rest of region 1; Malakor at 15M against
        // ~520k), so "highest MaxHp in the region" identifies the boss from
        // the real balance data instead of a hand-maintained id list that
        // would need editing every time content is added.
        public static bool IsRegionBoss(MonsterEntry monster)
        {
            if (monster == null || !TryResolveCanonicalRegion(monster, out int regionId)) return false;

            IReadOnlyList<MonsterEntry> region = GetMonstersInRegion(regionId);
            return region.Count > 0 && region[region.Count - 1].Id == monster.Id;
        }

        // Modul: canonical progression content. There are exactly FIVE
        // regions, each with four regular monsters plus one boss - the
        // m_01_* through m_05_* entity family (monsters.json ids 91-115).
        //
        // monsters.json also still carries 90 older entries whose RegionTier
        // spans 1-10. Deriving locations from RegionTier alone therefore
        // produced ten locations and dumped dozens of unrelated legacy
        // monsters into them (region 1 listed 41 creatures). Locations are now
        // built from the canonical family only, by EnemyId prefix, so the
        // Combat screen shows precisely the intended 5 x (4 + 1).
        public const int CanonicalRegionCount = 5;

        private static readonly string[] _regionNames =
        {
            "Sunlit Plains",
            "Whispering Woods",
            "Scorched Wasteland",
            "Frozen Peaks",
            "Shadow Citadel"
        };

        // Inclusive character-level band each region is designed for.
        private static readonly (int Min, int Max)[] _regionLevelBands =
        {
            (1, 20),
            (21, 40),
            (41, 60),
            (61, 80),
            (81, 100)
        };

        public static string GetRegionName(int regionId)
        {
            return regionId >= 1 && regionId <= _regionNames.Length
                ? _regionNames[regionId - 1]
                : "Region " + regionId;
        }

        // Renders as "Levels 21-40", or "Levels 81-100+" for the last region,
        // which is the open-ended endgame band.
        public static string GetRegionLevelBand(int regionId)
        {
            if (regionId < 1 || regionId > _regionLevelBands.Length)
            {
                return string.Empty;
            }

            (int min, int max) = _regionLevelBands[regionId - 1];
            bool isFinalRegion = regionId == _regionLevelBands.Length;
            return "Levels " + min + "-" + max + (isFinalRegion ? "+" : string.Empty);
        }

        // ------------------------------------------------------------
        // Modul: UI rework - consumables, for the Combat screen's food and
        // potion slots. Classified by the same three BaseId markers the
        // server's ConsumableEngine.TryApplyConsumable uses, so the client
        // can never offer something the server would refuse to apply.
        // ------------------------------------------------------------
        // Modul: larder. This marker is the narrow legacy one, carried by
        // exactly four items (roasted_perch, viper_stew, bear_stew,
        // yeti_platter) that no recipe produces and no monster drops. The ten
        // foods the game actually has - the cooking recipes' outputs, items
        // 194-203 - end in "_food" with no "_consumable", so the food dropdown
        // offered only unobtainable items and hid every real one. Both markers
        // are honoured now, matching the server's FoodRegistry.
        public const string FoodMarker = "_food_consumable";
        public const string CookedFoodMarker = "_food";

        // Mirrors FoodRegistry's heal table (GDD Module "Cooking (Sustain &
        // Auto-Eat Economy)" 3.2) for display only - the server is the
        // authority on what a food actually restores.
        private static readonly int[] _foodHealPayoutFlatHp =
        {
            40, 120, 310, 750, 1720, 3840, 8450, 18200, 38900, 82000
        };

        public const int FirstCookedFoodItemId = 194;
        public const int LastCookedFoodItemId = 203;

        // Flat HP one unit of this food restores, or 0 if it is not food.
        public static int GetFoodHealFlatHp(int itemId)
        {
            if (itemId >= FirstCookedFoodItemId && itemId <= LastCookedFoodItemId)
            {
                return _foodHealPayoutFlatHp[itemId - FirstCookedFoodItemId];
            }

            if (!TryGetItemById(itemId, out ItemEntry item) || !IsFood(item.BaseId))
            {
                return 0;
            }

            int tierIndex = Mathf.Clamp(item.RegionTier - 1, 0, _foodHealPayoutFlatHp.Length - 1);
            return _foodHealPayoutFlatHp[tierIndex];
        }

        public static bool IsFood(string baseId)
        {
            return !string.IsNullOrEmpty(baseId) && baseId.Contains(CookedFoodMarker);
        }
        public const string OffensivePotionMarker = "_offensive_potion_consumable";
        public const string DefensivePotionMarker = "_defensive_potion_consumable";

        private static readonly List<ItemEntry> _foods = new();
        private static readonly List<ItemEntry> _potions = new();

        public static IReadOnlyList<ItemEntry> Foods => _foods;
        public static IReadOnlyList<ItemEntry> Potions => _potions;

        // Turns "roasted_perch_food_consumable" into "Roasted Perch" and
        // "eq_obsidian_cleaver_melee_weapon_slot_base" into "Obsidian
        // Cleaver" - items.json carries no display names at all, only
        // BaseIds, so every item label in the game is derived here.
        //
        // The suffix list is ordered longest-first: "_melee_weapon_slot_base"
        // must be tried before "_base" or the result keeps a dangling
        // "Melee Weapon Slot".
        private static readonly string[] _strippedSuffixes =
        {
            "_melee_weapon_slot_base",
            "_ranged_weapon_slot_base",
            "_range_weapon_slot_base",
            "_magic_weapon_slot_base",
            "_chest_armor_slot_base",
            "_boots_armor_slot_base",
            "_leggings_armor_slot_base",
            "_helmet_armor_slot_base",
            "_gloves_armor_slot_base",
            "_helper_offhand_base",
            "_crafting_material",
            FoodMarker,
            OffensivePotionMarker,
            DefensivePotionMarker,
            // Modul: larder. Must come after FoodMarker above, which is longer
            // and more specific - stripping "_food" first would leave a
            // dangling "Consumable" on the four legacy items.
            CookedFoodMarker
        };

        private static readonly string[] _strippedPrefixes = { "mat_", "eq_" };

        public static string GetItemDisplayName(ItemEntry item)
        {
            if (item == null) return string.Empty;
            return GetItemDisplayName(item.BaseId);
        }

        public static string GetItemDisplayName(string baseId)
        {
            if (string.IsNullOrEmpty(baseId)) return string.Empty;

            for (int i = 0; i < _strippedSuffixes.Length; i++)
            {
                string stripped = StripSuffix(baseId, _strippedSuffixes[i]);
                if (!ReferenceEquals(stripped, baseId))
                {
                    baseId = stripped;
                    break;
                }
            }

            for (int i = 0; i < _strippedPrefixes.Length; i++)
            {
                if (baseId.StartsWith(_strippedPrefixes[i], StringComparison.Ordinal))
                {
                    baseId = baseId.Substring(_strippedPrefixes[i].Length);
                    break;
                }
            }

            string[] words = baseId.Split('_');
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length > 0)
                {
                    words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
                }
            }
            return string.Join(" ", words);
        }

        private static string StripSuffix(string value, string suffix)
        {
            return value.EndsWith(suffix, StringComparison.Ordinal)
                ? value.Substring(0, value.Length - suffix.Length)
                : value;
        }

        // Canonical entity ids are "m_<region>_<name>", e.g. m_03_basilisk.
        // The region number is carried in the prefix, which is what makes this
        // family identifiable without a separate content file.
        private const string CanonicalEnemyIdPrefix = "m_0";

        private static bool TryResolveCanonicalRegion(MonsterEntry monster, out int regionId)
        {
            regionId = 0;

            string enemyId = monster.EnemyId;
            if (string.IsNullOrEmpty(enemyId) || !enemyId.StartsWith(CanonicalEnemyIdPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            // "m_0" + one digit + "_"
            if (enemyId.Length < 5 || enemyId[4] != '_') return false;

            int parsed = enemyId[3] - '0';
            if (parsed < 1 || parsed > CanonicalRegionCount) return false;

            regionId = parsed;
            return true;
        }

        private static void BuildDerivedIndexes()
        {
            _monstersByRegion.Clear();
            _regionIds.Clear();

            foreach (MonsterEntry monster in _monsters.Values)
            {
                if (!TryResolveCanonicalRegion(monster, out int regionId))
                {
                    continue;
                }

                if (!_monstersByRegion.TryGetValue(regionId, out List<MonsterEntry> list))
                {
                    list = new List<MonsterEntry>(8);
                    _monstersByRegion[regionId] = list;
                    _regionIds.Add(regionId);
                }
                list.Add(monster);
            }

            _regionIds.Sort();
            foreach (List<MonsterEntry> list in _monstersByRegion.Values)
            {
                list.Sort((a, b) => a.MaxHp.CompareTo(b.MaxHp));
            }

            _foods.Clear();
            _potions.Clear();
            foreach (ItemEntry item in _items.Values)
            {
                if (IsFood(item.BaseId))
                {
                    _foods.Add(item);
                }
                else if (item.BaseId.Contains(OffensivePotionMarker) || item.BaseId.Contains(DefensivePotionMarker))
                {
                    _potions.Add(item);
                }
            }

            _foods.Sort((a, b) => a.RegionTier.CompareTo(b.RegionTier));
            _potions.Sort((a, b) => a.RegionTier.CompareTo(b.RegionTier));
        }

        public static bool TryGetGatheringNode(long activityId, out GatheringNodeEntry node) => _gatheringNodes.TryGetValue(activityId, out node);

        public static SkillEntry GetSkill(int skillId)
        {
            if (_skills.TryGetValue(skillId, out SkillEntry skill)) return skill;
            throw new KeyNotFoundException($"ClientContentRegistry: no skill with SkillId {skillId}.");
        }

        // Windows/Editor/standalone StreamingAssets is a plain filesystem
        // path (unlike Android/WebGL, where it is packed into a compressed
        // archive and requires UnityWebRequest) - this codebase's other
        // network code already assumes a desktop target throughout (raw
        // System.IO.File/System.Net.WebSockets usage elsewhere), so this
        // matches that same assumption rather than adding platform
        // abstraction nothing else here attempts either.
        private static List<T> LoadList<T>(string path)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"ClientContentRegistry: required content file missing: {path}");
            }

            string json = File.ReadAllText(path);
            List<T> parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<List<T>>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"ClientContentRegistry: failed to parse {path}: {ex.Message}", ex);
            }

            if (parsed == null || parsed.Count == 0)
            {
                throw new InvalidOperationException($"ClientContentRegistry: {path} parsed to empty or null content.");
            }

            return parsed;
        }
    }
}

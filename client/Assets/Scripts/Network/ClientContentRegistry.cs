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

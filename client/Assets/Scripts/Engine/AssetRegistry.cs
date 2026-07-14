using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace FolkIdle.Client.Engine
{
    [Serializable]
    public class MonsterMapping
    {
        public int MonsterId;
        public AssetReference MonsterPrefab;
    }

    [Serializable]
    public class ItemMapping
    {
        public string ItemId;
        public AssetReference ItemPrefab;
    }

    // Designer-facing, type-safe MonsterId/ItemId -> AssetReference lookup.
    // Serialized as flat Lists for Inspector drag-and-drop editing (Unity cannot
    // serialize Dictionaries directly); compiled into Dictionaries once so
    // lookups afterward are O(1) and allocation-free.
    [CreateAssetMenu(fileName = "AssetRegistry", menuName = "FolkIdle/Asset Registry")]
    public class AssetRegistry : ScriptableObject
    {
        public List<MonsterMapping> monsterMappings = new List<MonsterMapping>();
        public List<ItemMapping> itemMappings = new List<ItemMapping>();

        private Dictionary<int, AssetReference> _monsterCache;
        private Dictionary<string, AssetReference> _itemCache;

        private void OnEnable()
        {
            BuildCaches();
        }

        private void BuildCaches()
        {
            _monsterCache = new Dictionary<int, AssetReference>(monsterMappings.Count);
            for (int i = 0; i < monsterMappings.Count; i++)
            {
                MonsterMapping mapping = monsterMappings[i];
                if (mapping == null) continue;
                _monsterCache[mapping.MonsterId] = mapping.MonsterPrefab;
            }

            _itemCache = new Dictionary<string, AssetReference>(itemMappings.Count);
            for (int i = 0; i < itemMappings.Count; i++)
            {
                ItemMapping mapping = itemMappings[i];
                if (mapping == null || string.IsNullOrEmpty(mapping.ItemId)) continue;
                _itemCache[mapping.ItemId] = mapping.ItemPrefab;
            }
        }

        public bool TryGetMonsterAsset(int monsterId, out AssetReference assetRef)
        {
            if (_monsterCache == null)
            {
                BuildCaches();
            }

            if (_monsterCache.TryGetValue(monsterId, out assetRef) && assetRef != null && assetRef.RuntimeKeyIsValid())
            {
                return true;
            }

            Debug.LogWarning("AssetRegistry: no valid MonsterPrefab mapping for MonsterId " + monsterId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            assetRef = null;
            return false;
        }

        public bool TryGetItemAsset(string itemId, out AssetReference assetRef)
        {
            if (_itemCache == null)
            {
                BuildCaches();
            }

            if (!string.IsNullOrEmpty(itemId) && _itemCache.TryGetValue(itemId, out assetRef) && assetRef != null && assetRef.RuntimeKeyIsValid())
            {
                return true;
            }

            Debug.LogWarning("AssetRegistry: no valid ItemPrefab mapping for ItemId '" + itemId + "'");
            assetRef = null;
            return false;
        }
    }
}

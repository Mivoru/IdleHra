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

    // Flat 2D icon lookups (Sprite, not Addressables) - separate from the
    // MonsterMapping/ItemMapping pair above, which resolve to prefabs for the
    // 3D preview viewers (UiCodex3DViewer/UiForgeItemViewer). Generated art
    // dropped into Assets/Images/Sprites/ is plain 2D character/monster/
    // material artwork with no corresponding prefab, so it is wired here
    // instead and consumed directly by Image.sprite on UI rows.
    [Serializable]
    public class MonsterSpriteMapping
    {
        public int MonsterId;
        public Sprite Icon;
    }

    [Serializable]
    public class ItemSpriteMapping
    {
        public string ItemBaseId;
        public Sprite Icon;
    }

    [Serializable]
    public class RaceSpriteMapping
    {
        public int RaceId;
        public Sprite MaleIcon;
        public Sprite FemaleIcon;
    }

    // Modul: UI rework. Backdrop art for a combat location, keyed by the
    // RegionTier values authored in GameData/monsters.json. Populated by
    // AssetRegistryBuilder from Assets/Images/Sprites/Locations/NN/Location.png
    // where such a file exists - none do yet (the generated art so far is
    // monsters and materials only), so the Combat screen falls back to a
    // plain tinted frame and picks this up automatically once the art lands.
    [Serializable]
    public class RegionSpriteMapping
    {
        public int RegionId;
        public Sprite Backdrop;
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

        public List<MonsterSpriteMapping> monsterSpriteMappings = new List<MonsterSpriteMapping>();
        public List<ItemSpriteMapping> itemSpriteMappings = new List<ItemSpriteMapping>();
        public List<RaceSpriteMapping> raceSpriteMappings = new List<RaceSpriteMapping>();
        public List<RegionSpriteMapping> regionSpriteMappings = new List<RegionSpriteMapping>();
        public Sprite GoldIcon;
        public Sprite GemsIcon;

        private Dictionary<int, AssetReference> _monsterCache;
        private Dictionary<string, AssetReference> _itemCache;

        private Dictionary<int, Sprite> _monsterIconCache;
        private Dictionary<string, Sprite> _itemIconCache;
        private Dictionary<int, RaceSpriteMapping> _raceIconCache;
        private Dictionary<int, Sprite> _regionBackdropCache;

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

            _monsterIconCache = new Dictionary<int, Sprite>(monsterSpriteMappings.Count);
            for (int i = 0; i < monsterSpriteMappings.Count; i++)
            {
                MonsterSpriteMapping mapping = monsterSpriteMappings[i];
                if (mapping == null || mapping.Icon == null) continue;
                _monsterIconCache[mapping.MonsterId] = mapping.Icon;
            }

            _itemIconCache = new Dictionary<string, Sprite>(itemSpriteMappings.Count);
            for (int i = 0; i < itemSpriteMappings.Count; i++)
            {
                ItemSpriteMapping mapping = itemSpriteMappings[i];
                if (mapping == null || string.IsNullOrEmpty(mapping.ItemBaseId) || mapping.Icon == null) continue;
                _itemIconCache[mapping.ItemBaseId] = mapping.Icon;
            }

            _raceIconCache = new Dictionary<int, RaceSpriteMapping>(raceSpriteMappings.Count);
            for (int i = 0; i < raceSpriteMappings.Count; i++)
            {
                RaceSpriteMapping mapping = raceSpriteMappings[i];
                if (mapping == null) continue;
                _raceIconCache[mapping.RaceId] = mapping;
            }

            _regionBackdropCache = new Dictionary<int, Sprite>(regionSpriteMappings.Count);
            for (int i = 0; i < regionSpriteMappings.Count; i++)
            {
                RegionSpriteMapping mapping = regionSpriteMappings[i];
                if (mapping == null || mapping.Backdrop == null) continue;
                _regionBackdropCache[mapping.RegionId] = mapping.Backdrop;
            }
        }

        // Silent on a miss, unlike TryGetMonsterAsset - no region backdrop
        // art exists yet at all, so warning per lookup would spam the console
        // every time the Combat screen pages between locations.
        public bool TryGetRegionBackdrop(int regionId, out Sprite backdrop)
        {
            if (_regionBackdropCache == null)
            {
                BuildCaches();
            }

            return _regionBackdropCache.TryGetValue(regionId, out backdrop);
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

        public bool TryGetMonsterSprite(int monsterId, out Sprite icon)
        {
            if (_monsterIconCache == null)
            {
                BuildCaches();
            }

            return _monsterIconCache.TryGetValue(monsterId, out icon);
        }

        public bool TryGetItemSprite(string itemBaseId, out Sprite icon)
        {
            if (_itemIconCache == null)
            {
                BuildCaches();
            }

            if (!string.IsNullOrEmpty(itemBaseId) && _itemIconCache.TryGetValue(itemBaseId, out icon))
            {
                return true;
            }

            icon = null;
            return false;
        }

        public bool TryGetRaceSprite(int raceId, bool isFemale, out Sprite icon)
        {
            if (_raceIconCache == null)
            {
                BuildCaches();
            }

            if (_raceIconCache.TryGetValue(raceId, out RaceSpriteMapping mapping))
            {
                icon = isFemale ? mapping.FemaleIcon : mapping.MaleIcon;
                return icon != null;
            }

            icon = null;
            return false;
        }
    }
}

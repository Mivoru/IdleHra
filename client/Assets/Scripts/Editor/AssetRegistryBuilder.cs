using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEditor;
using UnityEngine;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Editor
{
    // Editor-only pipeline: converts the flat 2D reference art the user drops
    // into Assets/Images/Sprites/ (see ops/tools/generate_sprites.py for the
    // Python pass that strips backgrounds and writes those PNGs) into real
    // Sprite import settings plus AssetRegistry entries UI code can look up
    // by MonsterId/ItemBaseId/RaceId. Idempotent and safe to re-run any time
    // new art is dropped in - the sprite mapping lists are rebuilt from
    // scratch each run (keyed off filenames on disk, not indices), and
    // texture import settings are only rewritten when they actually differ.
    public static class AssetRegistryBuilder
    {
        private const string SpritesRootPath = "Assets/Images/Sprites";
        private const string AssetRegistryAssetPath = "Assets/Prefabs/UI/AssetRegistry.asset";
        private const string MonstersJsonRelativePath = "StreamingAssets/GameData/monsters.json";
        private const string ItemsJsonRelativePath = "StreamingAssets/GameData/items.json";

        // Character sprite race name -> RaceIds (see server/.../ContentRegistry.cs's
        // RaceIds: Human=1, Vila=2, Draugr=3, Kobold=4, Vodnik=5, Moosleute=6).
        // Four of the six generated race names match those RaceIds names
        // exactly; the two Slavic-folklore renames (Bes/Leshy) are the art's
        // names for Kobold/Moosleute - Bes is a Slavic house-spirit
        // analogous to a kobold, Leshy a Slavic forest spirit analogous to
        // "Moosleute" (German for "moss folk"). No other pairing fits the
        // remaining two races, so this is a confident match, not a guess -
        // but it is called out here in case future art contradicts it.
        private static readonly Dictionary<string, int> RaceNameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Human", 1 },
            { "Vila", 2 },
            { "Draugr", 3 },
            { "Bes", 4 },
            { "Vodnik", 5 },
            { "Leshy", 6 },
        };

        // Material/consumable sprite filename (without extension, exactly as
        // written to disk by generate_sprites.py) -> item BaseId. Hand-
        // verified against items.json rather than fuzzy-normalized, because
        // BaseId naming is inconsistent across the content file (older
        // entries use long descriptive slugs like
        // "birch_trees_woodcutting_material", newer ones use short "mat_x"/
        // clean-noun slugs like "birch_log") - a generic normalizer would
        // either miss real matches or confidently pick the wrong one.
        // Deliberately excluded (no single item unambiguously matches, see
        // PopulateMaterialSprites's skip warning): "CopperMalachiteOre" and
        // "IronHematitOre" (combined-ore gathering-node art, not a single
        // item), "Defensive Shield Potion" (no RegionTier-1/2 "shield"
        // potion exists in items.json - nearest candidates are RegionTier
        // 3/5 potions with unrelated names).
        private static readonly Dictionary<string, string> MaterialNameToBaseId = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Birch Log", "birch_log" },
            { "Birch Twig", "birch_twig" },
            { "Birch Tree", "birch_trees_woodcutting_material" },
            { "Golden Birch Log", "golden_birch_log" },
            { "Golden Birch Twig", "golden_birch_twig" },
            { "Copper", "copper_ore_crafting_material" },
            { "Malachite", "malachite_ore" },
            { "Roasted Perch", "roasted_perch_food_consumable" },
            { "Shimmering Trout", "shimmering_trout" },
            { "Sunlit Perch", "sunlit_perch" },
            { "Viper Venom Elixir", "mat_viper_venom" },
            { "Ancient Eel", "ancient_eel" },
            { "Golden Willow log", "whispering_willow_log" },
            { "Golden Willow twig", "whispering_willow_twig" },
            { "Hematite", "hematite_ore" },
            { "Iron bar", "iron_bar_crafting_material" },
            { "Moss Bass", "moss_bass" },
            { "Viper Stew", "viper_stew_food_consumable" },
            { "Willow log", "willow_log" },
            { "Willow tree", "willow_logs_woodcutting_material" },
            { "Willow twig", "willow_twig" },
        };

        [MenuItem("FolkIdle/Rebuild Asset Registry From Sprites")]
        public static void RebuildFromSprites()
        {
            FixSpriteImportSettings();
            PopulateRegistry();
        }

        public static void FixSpriteImportSettings()
        {
            if (!AssetDatabase.IsValidFolder(SpritesRootPath))
            {
                Debug.LogWarning("AssetRegistryBuilder: " + SpritesRootPath + " does not exist, nothing to import.");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { SpritesRootPath });
            int fixedCount = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                bool changed = false;
                if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; changed = true; }
                if (importer.spriteImportMode != SpriteImportMode.Single) { importer.spriteImportMode = SpriteImportMode.Single; changed = true; }
                if (importer.mipmapEnabled) { importer.mipmapEnabled = false; changed = true; }
                if (importer.alphaSource != TextureImporterAlphaSource.FromInput) { importer.alphaSource = TextureImporterAlphaSource.FromInput; changed = true; }
                if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; changed = true; }

                if (changed)
                {
                    importer.SaveAndReimport();
                    fixedCount++;
                }
            }

            Debug.Log("AssetRegistryBuilder: fixed import settings on " + fixedCount + " of " + guids.Length + " sprite texture(s) under " + SpritesRootPath + ".");
        }

        public static void PopulateRegistry()
        {
            AssetRegistry registry = AssetDatabase.LoadAssetAtPath<AssetRegistry>(AssetRegistryAssetPath);
            if (registry == null)
            {
                Debug.LogError("AssetRegistryBuilder: " + AssetRegistryAssetPath + " does not exist - run FolkIdle > Build Main Scene first so MainSceneBuilder can create it.");
                return;
            }

            List<MonsterEntry> monsters = LoadJsonList<MonsterEntry>(MonstersJsonRelativePath);
            Dictionary<string, int> monsterNameToId = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < monsters.Count; i++)
            {
                monsterNameToId[monsters[i].Name] = monsters[i].Id;
            }

            List<ItemEntry> items = LoadJsonList<ItemEntry>(ItemsJsonRelativePath);
            HashSet<string> knownBaseIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                knownBaseIds.Add(items[i].BaseId);
            }

            registry.monsterSpriteMappings.Clear();
            registry.itemSpriteMappings.Clear();
            registry.raceSpriteMappings.Clear();
            registry.regionSpriteMappings.Clear();

            int unmatchedCount = 0;
            unmatchedCount += PopulateMonsterSprites(registry, monsterNameToId);
            unmatchedCount += PopulateCharacterSprites(registry);
            unmatchedCount += PopulateMaterialSprites(registry, knownBaseIds);
            unmatchedCount += PopulateCurrencySprites(registry);
            PopulateRegionBackdrops(registry);

            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();

            Debug.Log("AssetRegistryBuilder: populated " + registry.monsterSpriteMappings.Count + " monster, " +
                registry.itemSpriteMappings.Count + " item, " + registry.raceSpriteMappings.Count +
                " race, " + registry.regionSpriteMappings.Count + " region sprite mapping(s); " + unmatchedCount + " sprite(s) skipped (no confident match, see warnings above).");
        }

        // Modul: UI rework. Backdrop art for the Combat screen's location
        // header, picked up from Assets/Images/Sprites/Locations/NN/Location.png.
        // No such file exists yet - the generated art so far is monsters and
        // materials only - so this currently maps nothing and the Combat
        // screen falls back to a tinted frame with the location name. Drop a
        // Location.png into any numbered location folder and re-run this
        // menu item and it appears with no code change.
        private static void PopulateRegionBackdrops(AssetRegistry registry)
        {
            string locationsRoot = SpritesRootPath + "/Locations";
            if (!AssetDatabase.IsValidFolder(locationsRoot))
            {
                return;
            }

            string[] folders = AssetDatabase.GetSubFolders(locationsRoot);
            for (int i = 0; i < folders.Length; i++)
            {
                string folderName = System.IO.Path.GetFileName(folders[i]);
                if (!int.TryParse(folderName, out int regionId) || regionId <= 0)
                {
                    continue;
                }

                string backdropPath = folders[i] + "/Location.png";
                Sprite backdrop = AssetDatabase.LoadAssetAtPath<Sprite>(backdropPath);
                if (backdrop == null)
                {
                    continue;
                }

                registry.regionSpriteMappings.Add(new RegionSpriteMapping { RegionId = regionId, Backdrop = backdrop });
            }
        }

        private static int PopulateMonsterSprites(AssetRegistry registry, Dictionary<string, int> monsterNameToId)
        {
            int unmatched = 0;
            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { SpritesRootPath + "/Locations" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!path.Contains("/Monsters/")) continue;

                string name = Path.GetFileNameWithoutExtension(path);
                if (!monsterNameToId.TryGetValue(name, out int monsterId))
                {
                    Debug.LogWarning("AssetRegistryBuilder: no monster named '" + name + "' in monsters.json for sprite " + path + ".");
                    unmatched++;
                    continue;
                }

                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) { unmatched++; continue; }

                registry.monsterSpriteMappings.Add(new MonsterSpriteMapping { MonsterId = monsterId, Icon = sprite });
            }

            return unmatched;
        }

        private static int PopulateCharacterSprites(AssetRegistry registry)
        {
            int unmatched = 0;
            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { SpritesRootPath + "/Characters" });
            Dictionary<int, RaceSpriteMapping> byRace = new Dictionary<int, RaceSpriteMapping>();

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                string name = Path.GetFileNameWithoutExtension(path);

                bool isFemale = name.EndsWith("_Female", StringComparison.Ordinal);
                bool isMale = name.EndsWith("_Male", StringComparison.Ordinal);
                if (!isFemale && !isMale)
                {
                    Debug.LogWarning("AssetRegistryBuilder: character sprite '" + name + "' has no _Male/_Female suffix, skipping.");
                    unmatched++;
                    continue;
                }

                string raceName = isFemale ? name.Substring(0, name.Length - "_Female".Length) : name.Substring(0, name.Length - "_Male".Length);
                if (!RaceNameToId.TryGetValue(raceName, out int raceId))
                {
                    Debug.LogWarning("AssetRegistryBuilder: no RaceId alias for character sprite race '" + raceName + "' (" + path + ").");
                    unmatched++;
                    continue;
                }

                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) { unmatched++; continue; }

                if (!byRace.TryGetValue(raceId, out RaceSpriteMapping mapping))
                {
                    mapping = new RaceSpriteMapping { RaceId = raceId };
                    byRace[raceId] = mapping;
                    registry.raceSpriteMappings.Add(mapping);
                }

                if (isFemale) mapping.FemaleIcon = sprite; else mapping.MaleIcon = sprite;
            }

            return unmatched;
        }

        private static int PopulateMaterialSprites(AssetRegistry registry, HashSet<string> knownBaseIds)
        {
            int unmatched = 0;
            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { SpritesRootPath + "/Locations" });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!path.Contains("/Materials&rest/")) continue;

                string name = Path.GetFileNameWithoutExtension(path);
                if (!MaterialNameToBaseId.TryGetValue(name, out string baseId))
                {
                    Debug.LogWarning("AssetRegistryBuilder: material sprite '" + name + "' has no known item BaseId alias, skipping (" + path + ").");
                    unmatched++;
                    continue;
                }

                if (!knownBaseIds.Contains(baseId))
                {
                    Debug.LogWarning("AssetRegistryBuilder: alias BaseId '" + baseId + "' for '" + name + "' no longer exists in items.json, skipping.");
                    unmatched++;
                    continue;
                }

                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) { unmatched++; continue; }

                registry.itemSpriteMappings.Add(new ItemSpriteMapping { ItemBaseId = baseId, Icon = sprite });
            }

            return unmatched;
        }

        private static int PopulateCurrencySprites(AssetRegistry registry)
        {
            string goldPath = SpritesRootPath + "/Others/Gold.png";
            string gemPath = SpritesRootPath + "/Others/Gem.png";

            Sprite goldSprite = AssetDatabase.LoadAssetAtPath<Sprite>(goldPath);
            Sprite gemSprite = AssetDatabase.LoadAssetAtPath<Sprite>(gemPath);

            int unmatched = 0;
            if (goldSprite != null) registry.GoldIcon = goldSprite; else { Debug.LogWarning("AssetRegistryBuilder: " + goldPath + " not found."); unmatched++; }
            if (gemSprite != null) registry.GemsIcon = gemSprite; else { Debug.LogWarning("AssetRegistryBuilder: " + gemPath + " not found."); unmatched++; }

            return unmatched;
        }

        private static List<T> LoadJsonList<T>(string relativePath)
        {
            string fullPath = Path.Combine(Application.dataPath, relativePath);
            string json = File.ReadAllText(fullPath);
            return JsonSerializer.Deserialize<List<T>>(json);
        }
    }
}

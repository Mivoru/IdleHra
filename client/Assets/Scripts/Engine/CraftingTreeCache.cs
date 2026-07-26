using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class CraftingRecipeData
    {
        public int ResultItemId { get; set; }
        public string ResultBaseItemId { get; set; } = string.Empty;
        public int ProfessionType { get; set; }
        public int RequiredLevel { get; set; }
        public int CraftingTimeMs { get; set; }
        public int Mat1Id { get; set; }
        public string Mat1BaseItemId { get; set; } = string.Empty;
        public int Mat1Count { get; set; }
        public long Mat1CurrentStock { get; set; }
        public int Mat2Id { get; set; }
        public string Mat2BaseItemId { get; set; } = string.Empty;
        public int Mat2Count { get; set; }
        public long Mat2CurrentStock { get; set; }

        // Both material requirements met. Level is checked separately so the
        // UI can distinguish "you cannot afford this" from "you are not high
        // enough level yet" - two very different messages to a player.
        public bool HasMaterials =>
            (Mat1Id <= 0 || Mat1CurrentStock >= Mat1Count) &&
            (Mat2Id <= 0 || Mat2CurrentStock >= Mat2Count);
    }

    public class CraftingRecipeSnapshotData
    {
        public int PlayerLevel { get; set; }
        public List<CraftingRecipeData> Recipes { get; set; } = new List<CraftingRecipeData>();
    }

    // Modul: Crafting Tree screen. First client-side access of any kind to
    // ContentRegistry's 103-recipe crafting tree.
    //
    // That tree has been fully functional server-side for a long time
    // (CommandType.InitializeCrafting -> CraftingEngine.ExecuteCraftingAsync,
    // real material consumption through the unified backpack+stash path) but
    // had no endpoint, no cache and no UI, so it was completely invisible
    // and unreachable - the single biggest content gap in the game. Note it
    // is a different, much larger system from CraftingReceptuary, the narrow
    // equipment-affix crafting the Forge screen already exposes.
    //
    // Professions are ContentRegistry's own ProfessionType values.
    public static class CraftingTreeCache
    {
        public const int ProfessionSmelting = 2;
        public const int ProfessionEquipment = 3;
        public const int ProfessionCooking = 4;
        public const int ProfessionAlchemy = 5;

        public static string ServerBaseUrl = "http://localhost:8080";

        public static event Action OnRecipesUpdated;

        public static IReadOnlyList<CraftingRecipeData> Recipes => _recipes;
        public static int PlayerLevel { get; private set; }

        private static List<CraftingRecipeData> _recipes = new List<CraftingRecipeData>();
        private static bool _requestInFlight;

        public static string GetProfessionName(int professionType)
        {
            switch (professionType)
            {
                case ProfessionSmelting: return "Smelting";
                case ProfessionEquipment: return "Equipment";
                case ProfessionCooking: return "Cooking";
                case ProfessionAlchemy: return "Alchemy";
                default: return "Profession " + professionType;
            }
        }

        public static void RequestSnapshot()
        {
            if (_requestInFlight) return;
            if (string.IsNullOrEmpty(WebSocketClient.AuthenticatorToken)) return;
            _ = FetchAsync();
        }

        private static async Task FetchAsync()
        {
            if (_requestInFlight) return;
            _requestInFlight = true;

            try
            {
                using UnityWebRequest request = UnityWebRequest.Get(ServerBaseUrl + "/api/v1/crafting/recipes");
                request.SetRequestHeader("Authorization", "Bearer " + WebSocketClient.AuthenticatorToken);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning("Crafting recipe snapshot request failed: " + request.error);
                    return;
                }

                CraftingRecipeSnapshotData data = JsonSerializer.Deserialize<CraftingRecipeSnapshotData>(request.downloadHandler.text);
                if (data == null) return;

                _recipes = data.Recipes ?? new List<CraftingRecipeData>();
                PlayerLevel = data.PlayerLevel;

                // Grouped by profession, then by the level gate inside it -
                // which is the order the tree is actually authored in and the
                // order a player progresses through it.
                _recipes.Sort(CompareRecipes);

                OnRecipesUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("Crafting recipe snapshot parse error: " + ex.Message);
            }
            finally
            {
                _requestInFlight = false;
            }
        }

        private static int CompareRecipes(CraftingRecipeData a, CraftingRecipeData b)
        {
            if (a.ProfessionType != b.ProfessionType) return a.ProfessionType.CompareTo(b.ProfessionType);
            if (a.RequiredLevel != b.RequiredLevel) return a.RequiredLevel.CompareTo(b.RequiredLevel);
            return a.ResultItemId.CompareTo(b.ResultItemId);
        }
    }
}

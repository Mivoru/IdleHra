using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class ForgeEquipmentInstanceData
    {
        public long Id { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public int QualityTier { get; set; }
        public bool IsAffixLocked { get; set; }
        public Dictionary<string, int> Affixes { get; set; } = new Dictionary<string, int>();
    }

    public class ForgeRecipeData
    {
        public int RecipeId { get; set; }
        public string ResultBaseItemId { get; set; } = string.Empty;
        public int TierIndex { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public int MaterialCost { get; set; }
        public long CurrentMaterialStock { get; set; }
    }

    internal sealed class ForgeInventorySnapshotData
    {
        public List<ForgeEquipmentInstanceData> OwnedEquipment { get; set; } = new List<ForgeEquipmentInstanceData>();
        public List<ForgeRecipeData> Recipes { get; set; } = new List<ForgeRecipeData>();
    }

    // Modul 21: on-demand snapshot cache for the Forge crafting/reroll panels.
    // StateUpdatePacket only carries fixed scalars over the 10 Hz binary channel,
    // so the owned-equipment list and per-recipe material stock (both variable
    // length) come from a separate authenticated HTTP GET instead - see
    // NetworkBroadcastSystem.HandleForgeInventorySnapshot on the server. Panels
    // call RequestSnapshot() explicitly (e.g. on open); this never polls on its own.
    public class EquipmentInventoryCache : MonoBehaviour
    {
        public string ServerBaseUrl = "http://localhost:8080";

        public IReadOnlyList<ForgeEquipmentInstanceData> OwnedEquipment => _ownedEquipment;
        public IReadOnlyList<ForgeRecipeData> Recipes => _recipes;

        public event Action OnSnapshotUpdated;

        private List<ForgeEquipmentInstanceData> _ownedEquipment = new List<ForgeEquipmentInstanceData>();
        private List<ForgeRecipeData> _recipes = new List<ForgeRecipeData>();
        private bool _requestInFlight;

        public void RequestSnapshot()
        {
            if (_requestInFlight) return;
            _ = RequestSnapshotAsync();
        }

        private async Task RequestSnapshotAsync()
        {
            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/forge/inventory");
                request.SetRequestHeader("X-Authenticator-Token", WebSocketClient.AuthenticatorToken);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Forge inventory snapshot request failed: {request.error}");
                    return;
                }

                ForgeInventorySnapshotData snapshot = JsonSerializer.Deserialize<ForgeInventorySnapshotData>(request.downloadHandler.text);
                if (snapshot == null) return;

                _ownedEquipment = snapshot.OwnedEquipment ?? new List<ForgeEquipmentInstanceData>();
                _recipes = snapshot.Recipes ?? new List<ForgeRecipeData>();

                OnSnapshotUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Forge inventory snapshot parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class InventoryEquipmentData
    {
        public long Id { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public int QualityTier { get; set; }
        public bool IsEquipped { get; set; }

        // Modul: roster loadouts. Which character slot (0-2) wears this, or -1
        // if carried. The 10Hz wire carries only the ACTIVE character's gear,
        // so this snapshot is the only way the Roster screen can show what
        // characters 2 and 3 are wearing.
        public int EquippedByCharacterSlot { get; set; } = -1;

        // Modul: Affix System Unification. GDD affix id -> magnitude. Rendered
        // through ClientAffixRegistry.Describe, which knows which ids are
        // percentages carried in tenths.
        public Dictionary<string, int> Affixes { get; set; } = new Dictionary<string, int>();

        public bool IsAffixLocked { get; set; }
    }

    public class InventoryStackData
    {
        public string ItemId { get; set; } = string.Empty;
        public long BackpackQuantity { get; set; }
        public long StashQuantity { get; set; }
        public long Total => BackpackQuantity + StashQuantity;
    }

    public class PlayerInventorySnapshotData
    {
        public int BackpackSlotsUsed { get; set; }
        public long MaxStackQuantity { get; set; }
        public List<InventoryEquipmentData> Equipment { get; set; } = new List<InventoryEquipmentData>();
        public List<InventoryStackData> Stacks { get; set; } = new List<InventoryStackData>();
    }

    // Modul: Inventory screen. On-demand snapshot of everything the player
    // owns, from the new /api/v1/player/inventory endpoint.
    //
    // This is genuinely new ground: the closest thing that existed was
    // EquipmentInventoryCache, which is scoped to what the Forge needs
    // (equipment instances plus the few materials Forge recipes consume) and
    // knows nothing about the village stash, the full commodity list, or
    // which items are currently equipped. Mirrors that cache's exact shape
    // otherwise - explicit RequestSnapshot(), never polls on its own.
    public static class PlayerInventoryCache
    {
        // Modul: server config. Reads the one configured server address rather
        // than owning a copy - see ClientServerConfig for why twenty-five
        // independent copies of this made the client localhost-only.
        public static string ServerBaseUrl => FolkIdle.Client.Network.ClientServerConfig.BaseUrl;

        public static event Action OnInventoryUpdated;

        public static IReadOnlyList<InventoryEquipmentData> Equipment => _equipment;
        public static IReadOnlyList<InventoryStackData> Stacks => _stacks;
        public static int BackpackSlotsUsed { get; private set; }
        // Modul: unlimited village chest. 0 means "no cap" - the server sends
        // that now, and the Inventory summary suppresses the cap line entirely
        // rather than printing a limit that no longer exists.
        public static long MaxStackQuantity { get; private set; }

        private static List<InventoryEquipmentData> _equipment = new List<InventoryEquipmentData>();
        private static List<InventoryStackData> _stacks = new List<InventoryStackData>();
        private static bool _requestInFlight;

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
                using UnityWebRequest request = UnityWebRequest.Get(ServerBaseUrl + "/api/v1/player/inventory");
                request.SetRequestHeader("Authorization", "Bearer " + WebSocketClient.AuthenticatorToken);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning("Inventory snapshot request failed: " + request.error);
                    return;
                }

                PlayerInventorySnapshotData data = JsonSerializer.Deserialize<PlayerInventorySnapshotData>(request.downloadHandler.text);
                if (data == null) return;

                _equipment = data.Equipment ?? new List<InventoryEquipmentData>();
                _stacks = data.Stacks ?? new List<InventoryStackData>();
                BackpackSlotsUsed = data.BackpackSlotsUsed;
                MaxStackQuantity = data.MaxStackQuantity;

                // Equipped first, then rarest, so the most relevant gear is
                // at the top of the list without the player having to scroll.
                _equipment.Sort(CompareEquipment);
                _stacks.Sort((a, b) => b.Total.CompareTo(a.Total));

                OnInventoryUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("Inventory snapshot parse error: " + ex.Message);
            }
            finally
            {
                _requestInFlight = false;
            }
        }

        private static int CompareEquipment(InventoryEquipmentData a, InventoryEquipmentData b)
        {
            if (a.IsEquipped != b.IsEquipped) return a.IsEquipped ? -1 : 1;
            if (a.QualityTier != b.QualityTier) return b.QualityTier.CompareTo(a.QualityTier);
            return string.CompareOrdinal(a.BaseItemId, b.BaseItemId);
        }
    }
}

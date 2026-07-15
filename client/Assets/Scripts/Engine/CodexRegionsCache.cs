using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class RegionProgressData
    {
        public int RegionId { get; set; }
        public int CurrentKills { get; set; }
        public int RequiredKills { get; set; }
        public bool IsCompleted { get; set; }
        public int LootLuckBonusPct { get; set; }
    }

    // Modul 13.4.3: on-demand snapshot cache for the Codex region-completion
    // UI. Mirrors CodexInventoryCache's pattern - see
    // NetworkBroadcastSystem.HandleCodexRegionsSnapshot on the server, which
    // reads MonsterCodexEntries/PlayerRegionCompletions and reduces them to
    // one row per region (minimum kill count across the region's monsters,
    // capped at the 1000 completion threshold). Never polls on its own; the
    // UI must call RequestSnapshot() explicitly (e.g. on panel open).
    public static class CodexRegionsCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<RegionProgressData> Regions => _regions;

        public static event Action OnCodexRegionsCacheUpdated;

        private static List<RegionProgressData> _regions = new List<RegionProgressData>();
        private static bool _requestInFlight;

        public static void RequestSnapshot()
        {
            if (_requestInFlight) return;
            _ = FetchRegionsSnapshotAsync();
        }

        public static async Task FetchRegionsSnapshotAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/codex/regions");
                request.SetRequestHeader("X-Authenticator-Token", WebSocketClient.AuthenticatorToken);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Codex regions snapshot request failed: {request.error}");
                    return;
                }

                List<RegionProgressData> snapshot = JsonSerializer.Deserialize<List<RegionProgressData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _regions = snapshot;
                OnCodexRegionsCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Codex regions snapshot parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

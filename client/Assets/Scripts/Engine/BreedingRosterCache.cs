using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class BreedingRosterEntryData
    {
        public string CharacterId { get; set; } = string.Empty;
        public int Level { get; set; }
        public int AgePhase { get; set; }
        public int GenerationIndex { get; set; }
        public bool IsBreedingActive { get; set; }
        public long BreedingCooldownEndEpoch { get; set; }
        public bool IsEpicMutation { get; set; }
        public bool IsInbred { get; set; }
        public int LocusRaceDominant { get; set; }
        public int LocusRaceRecessive { get; set; }
        public int LocusSpeedDominant { get; set; }
        public int LocusSpeedRecessive { get; set; }
        public int LocusCritDominant { get; set; }
        public int LocusCritRecessive { get; set; }
        public int LocusYieldDominant { get; set; }
        public int LocusYieldRecessive { get; set; }
    }

    // Modul 13.4.3: on-demand snapshot cache for the Breeding Lab's parent
    // roster. Mirrors CodexInventoryCache/MarketBrowserCache's pattern - see
    // NetworkBroadcastSystem.HandleBreedingRosterSnapshot on the server. Never
    // polls on its own; the UI must call RequestSnapshot() explicitly (panel
    // open, or after a breeding attempt to detect the outcome - see
    // UiBreedingLabWindow's poll-for-confirmation flow).
    public static class BreedingRosterCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<BreedingRosterEntryData> Entries => _entries;

        public static event Action OnRosterCacheUpdated;

        private static List<BreedingRosterEntryData> _entries = new List<BreedingRosterEntryData>();
        private static bool _requestInFlight;

        public static void RequestSnapshot()
        {
            if (_requestInFlight) return;
            _ = FetchRosterSnapshotAsync();
        }

        public static async Task FetchRosterSnapshotAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/breeding/roster");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Breeding roster snapshot request failed: {request.error}");
                    return;
                }

                List<BreedingRosterEntryData> snapshot = JsonSerializer.Deserialize<List<BreedingRosterEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnRosterCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Breeding roster snapshot parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

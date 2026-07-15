using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class AchievementEntryData
    {
        public int AchievementId { get; set; }
        public long CurrentProgress { get; set; }
        public int CompletedTier { get; set; }
        public long NextTierTarget { get; set; }
        public int NextTierReward { get; set; }
    }

    // Modul 13: on-demand snapshot cache for the player's real lifetime
    // achievement progress. StateUpdatePacket carries no achievement list (fixed-
    // size, scalars only) - this comes from a separate authenticated HTTP GET,
    // see NetworkBroadcastSystem.HandleAchievementsSnapshot on the server. UI
    // must call RequestSnapshot()/FetchAchievementsSnapshotAsync() explicitly
    // (e.g. on panel open); this never polls on its own.
    public static class AchievementCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<AchievementEntryData> Entries => _entries;

        public static event Action OnAchievementsUpdated;

        private static List<AchievementEntryData> _entries = new List<AchievementEntryData>();
        private static bool _requestInFlight;

        public static void RequestSnapshot()
        {
            if (_requestInFlight) return;
            _ = FetchAchievementsSnapshotAsync();
        }

        public static async Task FetchAchievementsSnapshotAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/achievements/snapshot");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Achievements snapshot request failed: {request.error}");
                    return;
                }

                List<AchievementEntryData> snapshot = JsonSerializer.Deserialize<List<AchievementEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnAchievementsUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Achievements snapshot parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

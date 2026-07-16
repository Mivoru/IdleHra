using System;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class AchievementsStateData
    {
        public int ClaimedAchievementFlags { get; set; }
        public int TotalAchievementsClaimedCount { get; set; }
        public ulong ClaimedMilestonesBitmask { get; set; }
    }

    // Modul: Production Release Hardening, Part 2. Replaces the
    // ClaimedAchievementFlags/TotalAchievementsClaimedCount/
    // ClaimedMilestonesBitmask fields removed from StateUpdatePacket (see
    // that struct's own trailing doc comment) - low-frequency/static
    // metadata, fetched via an authenticated HTTP GET
    // (NetworkBroadcastSystem.HandleAchievementsState) instead of the 10Hz
    // binary channel. Mirrors MailboxCache/LeaderboardCache's exact
    // pattern. Never polls on its own; VisualSyncProxy calls Refresh on a
    // slow timer (see its own MetadataRefreshIntervalSeconds).
    public static class AchievementsStateCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static event Action<AchievementsStateData> OnAchievementsStateUpdated;

        private static bool _requestInFlight;

        public static void Refresh()
        {
            if (_requestInFlight) return;
            _ = FetchAsync();
        }

        private static async Task FetchAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                string url = $"{ServerBaseUrl}/api/v1/achievements/state";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Achievements state request failed: {request.error}");
                    return;
                }

                AchievementsStateData data = JsonSerializer.Deserialize<AchievementsStateData>(request.downloadHandler.text);
                if (data == null) return;

                OnAchievementsStateUpdated?.Invoke(data);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Achievements state parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

using System;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class PlayerStatisticsData
    {
        public int Level { get; set; }
        public long Xp { get; set; }
        public long Gold { get; set; }
        public int PremiumDiamonds { get; set; }
        public int LoginStreakDays { get; set; }
        public int AchievementsClaimedCount { get; set; }
        public int RegionsCompletedCount { get; set; }
        public int CharacterCount { get; set; }
        public int AvailableSkillPoints { get; set; }
        public string GuildName { get; set; } = string.Empty;
    }

    // Modul: UI audit follow-up. On-demand snapshot cache for the player's
    // real aggregate statistics, backed by
    // NetworkBroadcastSystem.HandlePlayerStatistics - mirrors
    // AchievementsStateCache's exact "state" (not list) pattern.
    public static class PlayerStatisticsCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static event Action<PlayerStatisticsData> OnStatisticsUpdated;

        private static bool _requestInFlight;

        public static void RequestSnapshot()
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
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/player/statistics");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Player statistics request failed: {request.error}");
                    return;
                }

                PlayerStatisticsData data = JsonSerializer.Deserialize<PlayerStatisticsData>(request.downloadHandler.text);
                if (data == null) return;

                OnStatisticsUpdated?.Invoke(data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Player statistics parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

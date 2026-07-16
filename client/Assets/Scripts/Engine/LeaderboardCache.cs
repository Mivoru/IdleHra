using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class LeaderboardEntryData
    {
        public int Rank { get; set; }
        public long PlayerId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public int Level { get; set; }
        public long Xp { get; set; }
    }

    // Modul: Phase - Full-Stack Production Polish, Part 1.3. On-demand
    // snapshot cache for the leaderboard window, mirroring MarketBrowserCache's
    // exact pattern against NetworkBroadcastSystem.HandleGlobalLeaderboard
    // (/api/v1/leaderboard/global), which already existed server-side (backed
    // by LeaderboardCronEngine's Redis sorted set) before this client cache
    // did. Never polls on its own; the UI must call RequestPage explicitly.
    public static class LeaderboardCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<LeaderboardEntryData> Entries => _entries;
        public static int CurrentSkip { get; private set; }

        public static event Action OnLeaderboardCacheUpdated;

        private static List<LeaderboardEntryData> _entries = new List<LeaderboardEntryData>();
        private static bool _requestInFlight;

        public static void RequestPage(int skip, int take)
        {
            if (_requestInFlight) return;
            _ = FetchAsync(skip, take);
        }

        private static async Task FetchAsync(int skip, int take)
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                string url = $"{ServerBaseUrl}/api/v1/leaderboard/global?skip={skip}&take={take}";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Leaderboard request failed: {request.error}");
                    return;
                }

                List<LeaderboardEntryData> snapshot = JsonSerializer.Deserialize<List<LeaderboardEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                CurrentSkip = skip;
                OnLeaderboardCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Leaderboard parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

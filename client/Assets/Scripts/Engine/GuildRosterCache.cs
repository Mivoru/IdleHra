using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class GuildRosterEntryData
    {
        public long PlayerId { get; set; }
        public int Role { get; set; }
        public long ContributionPoints { get; set; }
        public bool IsOnline { get; set; }
    }

    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1.
    // On-demand snapshot cache for the Guild Roster panel, mirroring
    // MailboxCache/LeaderboardCache's exact pattern against
    // NetworkBroadcastSystem.HandleGuildRoster (/api/v1/guild/roster).
    public static class GuildRosterCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<GuildRosterEntryData> Entries => _entries;

        public static event Action OnGuildRosterCacheUpdated;

        private static List<GuildRosterEntryData> _entries = new List<GuildRosterEntryData>();
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
                string url = $"{ServerBaseUrl}/api/v1/guild/roster";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Guild roster request failed: {request.error}");
                    return;
                }

                List<GuildRosterEntryData> snapshot = JsonSerializer.Deserialize<List<GuildRosterEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnGuildRosterCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Guild roster parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

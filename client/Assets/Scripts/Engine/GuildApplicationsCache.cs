using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class GuildApplicationEntryData
    {
        public long Id { get; set; }
        public long PlayerId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int ApplicantLevel { get; set; }
        public long CreatedAtEpoch { get; set; }
    }

    // Modul: Play Mode audit fix. JoinGuildAsync has always filed a
    // GuildApplication row for Application-Required guilds, but nothing
    // anywhere ever reviewed one - see NetworkBroadcastSystem.
    // HandleGuildApplicationsPending's own comment. Mirrors
    // GuildRosterCache's exact on-demand snapshot pattern against
    // /api/v1/guild/applications/pending (leader-only; returns an empty
    // list for anyone else).
    public static class GuildApplicationsCache
    {
        // Modul: server config. Reads the one configured server address rather
        // than owning a copy - see ClientServerConfig for why twenty-five
        // independent copies of this made the client localhost-only.
        public static string ServerBaseUrl => FolkIdle.Client.Network.ClientServerConfig.BaseUrl;

        public static IReadOnlyList<GuildApplicationEntryData> Entries => _entries;

        public static event Action OnGuildApplicationsCacheUpdated;

        private static List<GuildApplicationEntryData> _entries = new List<GuildApplicationEntryData>();
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
                string url = $"{ServerBaseUrl}/api/v1/guild/applications/pending";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Guild applications request failed: {request.error}");
                    return;
                }

                List<GuildApplicationEntryData> snapshot = JsonSerializer.Deserialize<List<GuildApplicationEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnGuildApplicationsCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Guild applications parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

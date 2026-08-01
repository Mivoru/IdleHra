using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    // Modul: guild discovery, 2026-08-01. One entry of GET /api/v1/guilds/list.
    // Field names must match GuildDirectoryEntryResponse on the server exactly -
    // this is a JSON contract, so a rename on either side silently produces
    // zeroed fields rather than an error.
    public class GuildDirectoryEntryData
    {
        public long GuildId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int CurrentTier { get; set; }
        public int ActiveMembers { get; set; }
        public int MaxMembers { get; set; }
        public int GuildMMR { get; set; }
        public int TaxRatePct { get; set; }
        public int JoinType { get; set; }
        public int MinApplicationLevel { get; set; }
    }

    // Deliberately modelled on LeaderboardCache rather than inventing a second
    // fetch convention: same in-flight guard, same Authorization header, same
    // event-driven refresh. Two REST list screens behaving differently is how
    // small inconsistencies become maintenance cost.
    public static class GuildDirectoryCache
    {
        public static string ServerBaseUrl => ClientServerConfig.BaseUrl;

        private static List<GuildDirectoryEntryData> _entries = new List<GuildDirectoryEntryData>();
        private static bool _requestInFlight;

        public static IReadOnlyList<GuildDirectoryEntryData> Entries => _entries;
        public static int CurrentSkip { get; private set; }
        public static string CurrentNameFilter { get; private set; } = string.Empty;

        public static event Action OnGuildDirectoryUpdated;

        public static void RequestPage(int skip, int take, string nameFilter)
        {
            if (_requestInFlight) return;
            _ = FetchAsync(skip, take, nameFilter);
        }

        private static async Task FetchAsync(int skip, int take, string nameFilter)
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                string url = $"{ServerBaseUrl}/api/v1/guilds/list?skip={skip}&take={take}";
                if (!string.IsNullOrWhiteSpace(nameFilter))
                {
                    // Escaped because guild names are player-authored and may
                    // contain spaces or characters that would otherwise break
                    // the query string.
                    url += "&name=" + UnityWebRequest.EscapeURL(nameFilter.Trim());
                }

                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Guild directory request failed: {request.error}");
                    return;
                }

                List<GuildDirectoryEntryData> snapshot =
                    System.Text.Json.JsonSerializer.Deserialize<List<GuildDirectoryEntryData>>(request.downloadHandler.text);

                if (snapshot == null) return;

                _entries = snapshot;
                CurrentSkip = skip;
                CurrentNameFilter = nameFilter ?? string.Empty;
                OnGuildDirectoryUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Guild directory parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

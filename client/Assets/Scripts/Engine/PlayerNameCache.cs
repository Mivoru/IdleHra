using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class PlayerNameEntryData
    {
        public long PlayerId { get; set; }
        public string Username { get; set; } = string.Empty;
    }

    // Modul: UI rework. id -> username resolution for every social surface
    // that only ever receives a numeric player id over the wire.
    // ResponseChatMessagePacket carries SenderPlayerId and nothing else, so
    // before this every chat line read "Player #1042".
    //
    // Deliberately a write-once cache with no invalidation: usernames are
    // effectively immutable in this game (the register flow sets one and no
    // endpoint anywhere changes it), so a resolved name never goes stale.
    // Requests are batched and coalesced - Request(id) only marks an id as
    // wanted; the actual HTTP GET fires for the whole pending set at once
    // (matching the server's /api/v1/players/names batch endpoint and its
    // 64-id cap), so a chat log filling with 15 new senders costs one
    // request, not fifteen.
    public static class PlayerNameCache
    {
        // Modul: server config. Reads the one configured server address rather
        // than owning a copy - see ClientServerConfig for why twenty-five
        // independent copies of this made the client localhost-only.
        public static string ServerBaseUrl => FolkIdle.Client.Network.ClientServerConfig.BaseUrl;

        private const int MaxIdsPerRequest = 64;

        public static event Action OnPlayerNamesUpdated;

        private static readonly Dictionary<long, string> _namesById = new Dictionary<long, string>(64);
        private static readonly HashSet<long> _pendingIds = new HashSet<long>();
        private static readonly HashSet<long> _inFlightIds = new HashSet<long>();
        private static readonly StringBuilder _queryBuilder = new StringBuilder(512);

        private static bool _requestInFlight;

        // Returns the resolved username, or null if it is not known yet -
        // in which case the id is queued for the next batch. Callers render
        // a "Player #id" fallback until OnPlayerNamesUpdated tells them to
        // re-render.
        public static string GetOrRequest(long playerId)
        {
            if (playerId <= 0) return null;

            if (_namesById.TryGetValue(playerId, out string username))
            {
                return username;
            }

            if (!_inFlightIds.Contains(playerId) && _pendingIds.Add(playerId))
            {
                Flush();
            }

            return null;
        }

        public static bool TryGet(long playerId, out string username)
        {
            return _namesById.TryGetValue(playerId, out username);
        }

        // Locally seeds a name learned through some other path (the Friends
        // roster already resolves both id and username server-side, so a
        // whisper thread with a friend should never have to look the same
        // name up a second time over HTTP).
        public static void Seed(long playerId, string username)
        {
            if (playerId <= 0 || string.IsNullOrEmpty(username)) return;
            _namesById[playerId] = username;
        }

        public static void Flush()
        {
            if (_requestInFlight || _pendingIds.Count == 0) return;
            if (string.IsNullOrEmpty(WebSocketClient.AuthenticatorToken)) return;

            _ = FetchAsync();
        }

        private static async Task FetchAsync()
        {
            if (_requestInFlight) return;
            _requestInFlight = true;

            try
            {
                _inFlightIds.Clear();
                _queryBuilder.Clear();

                foreach (long id in _pendingIds)
                {
                    if (_inFlightIds.Count >= MaxIdsPerRequest) break;
                    if (_queryBuilder.Length > 0) _queryBuilder.Append(',');
                    _queryBuilder.Append(id);
                    _inFlightIds.Add(id);
                }

                foreach (long id in _inFlightIds)
                {
                    _pendingIds.Remove(id);
                }

                if (_inFlightIds.Count == 0) return;

                string url = ServerBaseUrl + "/api/v1/players/names?ids=" + _queryBuilder;
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", "Bearer " + WebSocketClient.AuthenticatorToken);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning("Player name lookup failed: " + request.error);
                    return;
                }

                List<PlayerNameEntryData> entries = JsonSerializer.Deserialize<List<PlayerNameEntryData>>(request.downloadHandler.text);
                if (entries == null) return;

                for (int i = 0; i < entries.Count; i++)
                {
                    if (!string.IsNullOrEmpty(entries[i].Username))
                    {
                        _namesById[entries[i].PlayerId] = entries[i].Username;
                    }
                }

                OnPlayerNamesUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("Player name lookup parse error: " + ex.Message);
            }
            finally
            {
                _inFlightIds.Clear();
                _requestInFlight = false;

                // Anything queued while this batch was in flight (or spilled
                // past the 64-id cap) goes out as the next batch.
                if (_pendingIds.Count > 0)
                {
                    Flush();
                }
            }
        }
    }
}

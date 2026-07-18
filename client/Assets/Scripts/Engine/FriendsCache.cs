using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class FriendEntryData
    {
        public long PlayerId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int Level { get; set; }
        public bool IsBlocked { get; set; }
    }

    // Modul: UI audit follow-up. On-demand snapshot cache for the player's
    // real friend/block relationship list, backed by
    // NetworkBroadcastSystem.HandleFriendsList - mirrors MailboxCache/
    // LeaderboardCache's exact pattern. AddFriend/RemoveFriend/BlockPlayer/
    // UnblockPlayer themselves stay WebSocket commands (RelationshipEngine
    // already handled those); this cache only covers listing, plus the
    // one-off username->PlayerId resolve needed before AddFriend can be
    // sent at all (see HandlePlayerResolve).
    public static class FriendsCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<FriendEntryData> Entries => _entries;

        public static event Action OnFriendsCacheUpdated;

        private static List<FriendEntryData> _entries = new List<FriendEntryData>();
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
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/friends/list");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Friends list request failed: {request.error}");
                    return;
                }

                List<FriendEntryData> snapshot = JsonSerializer.Deserialize<List<FriendEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnFriendsCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Friends list parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }

        // Modul: resolves a typed username to the numeric PlayerId
        // AddFriend/BlockPlayer actually require. One-off request, not part
        // of the persistent Entries snapshot - onResolved/onNotFound/onError
        // fire exactly once each call, never cached.
        public static void RequestResolve(string username, Action<long> onResolved, Action onNotFound, Action<string> onError)
        {
            _ = ResolveAsync(username, onResolved, onNotFound, onError);
        }

        private static async Task ResolveAsync(string username, Action<long> onResolved, Action onNotFound, Action<string> onError)
        {
            try
            {
                string encodedUsername = UnityWebRequest.EscapeURL(username);
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/players/resolve?username={encodedUsername}");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.responseCode == 404)
                {
                    onNotFound?.Invoke();
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke(request.error);
                    return;
                }

                PlayerResolveData resolved = JsonSerializer.Deserialize<PlayerResolveData>(request.downloadHandler.text);
                if (resolved == null)
                {
                    onError?.Invoke("Empty response");
                    return;
                }

                onResolved?.Invoke(resolved.PlayerId);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex.Message);
            }
        }

        // Mirrors MailboxCache.RemoveEntryLocally's optimistic-update
        // pattern - AddFriend/RemoveFriend/BlockPlayer/UnblockPlayer are
        // fire-and-forget WebSocket commands processed asynchronously
        // server-side, so the list is updated immediately rather than
        // waiting on a re-fetch. A stale entry self-corrects the next time
        // the Friends window is reopened (RequestSnapshot).
        public static void AddEntryLocally(long playerId, string username)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].PlayerId == playerId) return;
            }

            _entries.Add(new FriendEntryData { PlayerId = playerId, Username = username, Level = 0, IsBlocked = false });
            OnFriendsCacheUpdated?.Invoke();
        }

        public static void RemoveEntryLocally(long playerId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].PlayerId == playerId)
                {
                    _entries.RemoveAt(i);
                    OnFriendsCacheUpdated?.Invoke();
                    return;
                }
            }
        }

        public static void MarkBlockedLocally(long playerId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].PlayerId == playerId)
                {
                    _entries[i].IsBlocked = true;
                    OnFriendsCacheUpdated?.Invoke();
                    return;
                }
            }
        }

        private sealed class PlayerResolveData
        {
            public long PlayerId { get; set; }
        }
    }
}

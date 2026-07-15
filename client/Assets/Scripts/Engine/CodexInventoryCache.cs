using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class CodexSnapshotEntryData
    {
        public int MonsterId { get; set; }
        public int Level { get; set; }
        public long Kills { get; set; }
        public long NextLevelKills { get; set; }
    }

    // Modul 23: on-demand snapshot cache for the player's real Monster Codex
    // progress. StateUpdatePacket carries no per-monster list (fixed-size,
    // scalars only), so this comes from a separate authenticated HTTP GET - see
    // NetworkBroadcastSystem.HandleCodexSnapshot on the server, which reads the
    // MonsterCodexEntries rows CodexEngine's kill-event cron already maintains.
    // UI must call RequestSnapshot()/FetchCodexSnapshotAsync() explicitly (e.g.
    // on panel open); this never polls on its own.
    public static class CodexInventoryCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<CodexSnapshotEntryData> Entries => _entries;

        public static event Action OnCodexCacheUpdated;

        private static List<CodexSnapshotEntryData> _entries = new List<CodexSnapshotEntryData>();
        private static bool _requestInFlight;

        public static void RequestSnapshot()
        {
            if (_requestInFlight) return;
            _ = FetchCodexSnapshotAsync();
        }

        public static async Task FetchCodexSnapshotAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/codex/snapshot");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Codex snapshot request failed: {request.error}");
                    return;
                }

                List<CodexSnapshotEntryData> snapshot = JsonSerializer.Deserialize<List<CodexSnapshotEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnCodexCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Codex snapshot parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

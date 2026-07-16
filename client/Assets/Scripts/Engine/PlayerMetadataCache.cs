using System;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class PlayerMetadataData
    {
        public int ChroniclePassLevel { get; set; }
        public int AccumulatedSeasonalXp { get; set; }
        public int EventHorizonTransactionCount { get; set; }
    }

    // Modul: Production Release Hardening, Part 2. Replaces the
    // ActiveChroniclePassLevel/AccumulatedSeasonalXp/EventHorizonTransactionCount
    // fields removed from StateUpdatePacket (see that struct's own trailing
    // doc comment) - low-frequency metadata, fetched via an authenticated
    // HTTP GET (NetworkBroadcastSystem.HandlePlayerMetadata) instead of the
    // 10Hz binary channel. Mirrors MailboxCache/LeaderboardCache's exact
    // pattern. Never polls on its own; VisualSyncProxy calls Refresh on a
    // slow timer (see its own MetadataRefreshIntervalSeconds).
    public static class PlayerMetadataCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static event Action<PlayerMetadataData> OnPlayerMetadataUpdated;

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
                string url = $"{ServerBaseUrl}/api/v1/player/metadata";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Player metadata request failed: {request.error}");
                    return;
                }

                PlayerMetadataData data = JsonSerializer.Deserialize<PlayerMetadataData>(request.downloadHandler.text);
                if (data == null) return;

                OnPlayerMetadataUpdated?.Invoke(data);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Player metadata parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

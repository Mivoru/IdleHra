using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class RaceMasteryEntryData
    {
        public int RaceId { get; set; }
        public int Level { get; set; }
        public long Experience { get; set; }
        public long NextLevelExperience { get; set; }
    }

    // Modul 13: on-demand snapshot cache for the player's real Race Mastery
    // progress. StateUpdatePacket only mirrors Human/Vila/Draugr mastery levels
    // (session-scalar fields used to gate live passive bonuses) - it carries no
    // XP or next-level threshold for any race, and no fields at all for Kobold/
    // Vodnik/Moosleute. This comes from a separate authenticated HTTP GET - see
    // NetworkBroadcastSystem.HandleMasterySnapshot on the server. UI must call
    // RequestSnapshot()/FetchMasterySnapshotAsync() explicitly (e.g. on panel
    // open); this never polls on its own.
    public static class RaceMasteryCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<RaceMasteryEntryData> Entries => _entries;

        public static event Action OnMasteryCacheUpdated;

        private static List<RaceMasteryEntryData> _entries = new List<RaceMasteryEntryData>();
        private static bool _requestInFlight;

        public static void RequestSnapshot()
        {
            if (_requestInFlight) return;
            _ = FetchMasterySnapshotAsync();
        }

        public static async Task FetchMasterySnapshotAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                using UnityWebRequest request = UnityWebRequest.Get($"{ServerBaseUrl}/api/v1/mastery/snapshot");
                request.SetRequestHeader("X-Authenticator-Token", WebSocketClient.AuthenticatorToken);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Race mastery snapshot request failed: {request.error}");
                    return;
                }

                List<RaceMasteryEntryData> snapshot = JsonSerializer.Deserialize<List<RaceMasteryEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnMasteryCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Race mastery snapshot parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

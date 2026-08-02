using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    // Modul: drop preview, 2026-08-02. One row of
    // GET /api/v1/monsters/loot. Property names must match
    // MonsterLootEntryResponse on the server exactly - a JSON contract, so a
    // rename silently yields zeroes rather than an error.
    public class MonsterLootEntryData
    {
        public int ItemId { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public double ChancePct { get; set; }
        public int MinQuantity { get; set; }
        public int MaxQuantity { get; set; }
        public bool IsEquipment { get; set; }
    }

    // Cached per monster and never refetched: drop tables are static content
    // that only changes with a server deploy, so re-requesting on every
    // selection would be pure traffic. Cleared only by a client restart, which
    // is also when new content would arrive.
    public static class MonsterLootCache
    {
        private static readonly Dictionary<int, List<MonsterLootEntryData>> _byMonsterId = new();
        private static readonly HashSet<int> _inFlight = new();

        public static event Action<int> OnMonsterLootLoaded;

        public static bool TryGet(int monsterId, out List<MonsterLootEntryData> entries)
        {
            return _byMonsterId.TryGetValue(monsterId, out entries);
        }

        public static void Request(int monsterId)
        {
            if (monsterId <= 0) return;
            if (_byMonsterId.ContainsKey(monsterId)) return;
            if (!_inFlight.Add(monsterId)) return;

            _ = FetchAsync(monsterId);
        }

        private static async Task FetchAsync(int monsterId)
        {
            try
            {
                string url = $"{ClientServerConfig.BaseUrl}/api/v1/monsters/loot?monsterId={monsterId}";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Monster loot request failed: {request.error}");
                    return;
                }

                var rows = System.Text.Json.JsonSerializer.Deserialize<List<MonsterLootEntryData>>(request.downloadHandler.text);
                if (rows == null) return;

                _byMonsterId[monsterId] = rows;
                OnMonsterLootLoaded?.Invoke(monsterId);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Monster loot parse error: {ex.Message}");
            }
            finally
            {
                // Released even on failure, so a transient error does not
                // permanently block this monster from ever loading.
                _inFlight.Remove(monsterId);
            }
        }
    }
}

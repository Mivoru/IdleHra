using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class StoreCatalogEntryData
    {
        public string ProductId { get; set; } = string.Empty;
        public int DiamondAmount { get; set; }
    }

    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1.
    // On-demand snapshot cache for the Store window, mirroring
    // MarketBrowserCache/MailboxCache's exact pattern against
    // NetworkBroadcastSystem.HandleStoreCatalog (/api/v1/store/catalog),
    // which reads ContentRegistry.Balance.IapProductPrices - the same
    // GameBalanceConfig.json-driven price table BillingVerificationEngine
    // resolves purchases against server-side, so the client's displayed
    // prices can never drift from what a purchase actually grants.
    public static class StoreCatalogCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<StoreCatalogEntryData> Entries => _entries;

        public static event Action OnStoreCatalogUpdated;

        private static List<StoreCatalogEntryData> _entries = new List<StoreCatalogEntryData>();
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
                string url = $"{ServerBaseUrl}/api/v1/store/catalog";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Store catalog request failed: {request.error}");
                    return;
                }

                List<StoreCatalogEntryData> snapshot = JsonSerializer.Deserialize<List<StoreCatalogEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnStoreCatalogUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Store catalog parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

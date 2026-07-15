using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class MarketListingData
    {
        public long OrderId { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public int QualityTier { get; set; }
        public long Price { get; set; }
        public long CreatedAtEpoch { get; set; }
    }

    // Modul 40: on-demand snapshot cache for the marketplace browser page.
    // Mirrors CodexInventoryCache's pattern - listings are inherently
    // variable-length and paginated, so this comes from an authenticated HTTP
    // GET (NetworkBroadcastSystem.HandleMarketBrowserListings) rather than
    // StateUpdatePacket's fixed-size binary layout. Never polls on its own;
    // the UI must call RequestPage explicitly (panel open, page change).
    public static class MarketBrowserCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<MarketListingData> Entries => _entries;
        public static int CurrentPageIndex { get; private set; }

        public static event Action OnMarketCacheUpdated;

        private static List<MarketListingData> _entries = new List<MarketListingData>();
        private static bool _requestInFlight;

        public static void RequestPage(string baseItemId, int qualityTier, int pageIndex, int pageSize)
        {
            if (_requestInFlight) return;
            _ = FetchListingsAsync(baseItemId, qualityTier, pageIndex, pageSize);
        }

        // Optimistic local removal so a bought listing disappears from the
        // visible page immediately, without waiting on a full re-fetch round
        // trip. The next RequestPage call still reconciles against the server.
        public static void RemoveListingLocally(long orderId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].OrderId == orderId)
                {
                    _entries.RemoveAt(i);
                    OnMarketCacheUpdated?.Invoke();
                    return;
                }
            }
        }

        public static async Task FetchListingsAsync(string baseItemId, int qualityTier, int pageIndex, int pageSize)
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                string url = $"{ServerBaseUrl}/api/v1/market/listings?baseItemId={UnityWebRequest.EscapeURL(baseItemId)}&qualityTier={qualityTier}&pageIndex={pageIndex}&pageSize={pageSize}";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("X-Authenticator-Token", WebSocketClient.AuthenticatorToken);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Market browser listings request failed: {request.error}");
                    return;
                }

                List<MarketListingData> snapshot = JsonSerializer.Deserialize<List<MarketListingData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                CurrentPageIndex = pageIndex;
                OnMarketCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Market browser listings parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

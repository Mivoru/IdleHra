using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class MailboxEntryData
    {
        public long Id { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public int QualityTier { get; set; }
        public int Quantity { get; set; }
        public long GoldAttachment { get; set; }
        public bool HasEquipmentAttachment { get; set; }
        public long ReceivedTimestamp { get; set; }
    }

    // Modul: Phase - Full-Stack Production Polish, Part 1.2. On-demand
    // snapshot cache for the Inbox panel, mirroring MarketBrowserCache's
    // exact pattern - the mail list is variable-length and per-player, so
    // it comes from an authenticated HTTP GET (NetworkBroadcastSystem.
    // HandleMailboxListSnapshot) rather than StateUpdatePacket's fixed
    // binary layout. Never polls on its own; the UI must call Refresh
    // explicitly (panel open, after a claim result).
    public static class MailboxCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<MailboxEntryData> Entries => _entries;

        public static event Action OnMailboxCacheUpdated;

        private static List<MailboxEntryData> _entries = new List<MailboxEntryData>();
        private static bool _requestInFlight;

        public static void Refresh()
        {
            if (_requestInFlight) return;
            _ = FetchAsync();
        }

        // Optimistic local removal so a claimed mail item disappears from
        // the visible list immediately, without waiting on a full re-fetch
        // round trip - mirrors MarketBrowserCache.RemoveListingLocally.
        public static void RemoveEntryLocally(long mailId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id == mailId)
                {
                    _entries.RemoveAt(i);
                    OnMailboxCacheUpdated?.Invoke();
                    return;
                }
            }
        }

        private static async Task FetchAsync()
        {
            if (_requestInFlight) return;

            _requestInFlight = true;
            try
            {
                string url = $"{ServerBaseUrl}/api/v1/mailbox/list";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Mailbox list request failed: {request.error}");
                    return;
                }

                List<MailboxEntryData> snapshot = JsonSerializer.Deserialize<List<MailboxEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnMailboxCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Mailbox list parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine.Networking;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class BankVaultEntryData
    {
        public long Id { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public int QualityTier { get; set; }
        public bool IsAffixLocked { get; set; }
    }

    // Modul: Phase - Full-Stack Production Polish, Part 1.2. On-demand
    // snapshot cache for the Vault (bank) panel, mirroring MarketBrowserCache/
    // MailboxCache's exact pattern - see MailboxCache's own comment for why
    // this is an authenticated HTTP GET (NetworkBroadcastSystem.
    // HandleBankListSnapshot) rather than a StateUpdatePacket field.
    public static class BankVaultCache
    {
        public static string ServerBaseUrl = "http://localhost:8080";

        public static IReadOnlyList<BankVaultEntryData> Entries => _entries;

        public static event Action OnBankVaultCacheUpdated;

        private static List<BankVaultEntryData> _entries = new List<BankVaultEntryData>();
        private static bool _requestInFlight;

        public static void Refresh()
        {
            if (_requestInFlight) return;
            _ = FetchAsync();
        }

        // Optimistic local removal for a just-withdrawn item; addition for a
        // just-deposited one is not attempted locally (the server assigns
        // the new BankEquipmentInstances.Id) - callers should Refresh()
        // after a deposit instead, mirroring the safety-net re-fetch pattern
        // used elsewhere in this codebase (e.g. UiGuildRaidPanel).
        public static void RemoveEntryLocally(long bankId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id == bankId)
                {
                    _entries.RemoveAt(i);
                    OnBankVaultCacheUpdated?.Invoke();
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
                string url = $"{ServerBaseUrl}/api/v1/bank/list";
                using UnityWebRequest request = UnityWebRequest.Get(url);
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogWarning($"Bank list request failed: {request.error}");
                    return;
                }

                List<BankVaultEntryData> snapshot = JsonSerializer.Deserialize<List<BankVaultEntryData>>(request.downloadHandler.text);
                if (snapshot == null) return;

                _entries = snapshot;
                OnBankVaultCacheUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Bank list parse error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
            }
        }
    }
}

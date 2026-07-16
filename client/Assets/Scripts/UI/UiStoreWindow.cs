using System.Collections.Generic;
using System.Text;
using UnityEngine;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1. Store
    // window - a visual grid of the active premium packages configured in
    // GameBalanceConfig.json (fetched via StoreCatalogCache, never
    // hardcoded client-side), with purchase buttons routed through
    // WebSocketClient.SendPurchaseReceiptCommandZeroAlloc, the existing
    // CommandType.SubmitPurchaseReceipt dispatch that reaches
    // BillingVerificationEngine.VerifyPurchaseAsync server-side. Rows are
    // pooled via UIComponentPool, mirroring UiMarketBrowserWindow.
    //
    // Receipt note: this WebSocket path was never able to carry a real
    // signed store receipt (RawTransactionReceipt is a fixed 64 bytes) -
    // it always was, and remains, an opaque client-generated transaction id
    // string, matching how BillingVerificationEngine.VerifyPurchaseAsync
    // itself documents that field. A real native store purchase flow
    // (Google Play Billing / Apple StoreKit) is not wired into this Unity
    // project - integrating one is a platform-SDK dependency well beyond
    // this UI panel's scope - so this button dispatches the same
    // transaction-id-only path every other command in this codebase already
    // uses, rather than fabricating a fake signed receipt.
    public class UiStoreWindow : MonoBehaviour
    {
        [Header("Store HUD")]
        public Transform RowContainer;
        public UiStoreEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 10;

        public WebSocketClient NetworkClient;

        private UIComponentPool<UiStoreEntryRow> _rowPool;
        private readonly List<UiStoreEntryRow> _activeRows = new List<UiStoreEntryRow>();
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiStoreEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            StoreCatalogCache.OnStoreCatalogUpdated += HandleCacheUpdated;
            StoreCatalogCache.Refresh();
        }

        private void OnDisable()
        {
            StoreCatalogCache.OnStoreCatalogUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            if (!_isDirty) return;

            RefreshRows();
            _isDirty = false;
        }

        private void HandleCacheUpdated()
        {
            _isDirty = true;
        }

        private void RefreshRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<StoreCatalogEntryData> entries = StoreCatalogCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                StoreCatalogEntryData entry = entries[i];
                UiStoreEntryRow row = _rowPool.Spawn();
                row.Bind(entry.ProductId, entry.DiamondAmount, HandlePurchaseClicked);
                _activeRows.Add(row);
            }
        }

        private void HandlePurchaseClicked(string productId)
        {
            if (NetworkClient == null || string.IsNullOrEmpty(productId)) return;

            string transactionId = System.Guid.NewGuid().ToString("N");
            byte[] receiptBytes = new byte[64];
            byte[] transactionIdBytes = Encoding.UTF8.GetBytes(transactionId);
            int copyLength = transactionIdBytes.Length > 64 ? 64 : transactionIdBytes.Length;
            System.Array.Copy(transactionIdBytes, receiptBytes, copyLength);

            uint productIdHash = unchecked((uint)productId.GetHashCode());
            NetworkClient.SendPurchaseReceiptCommandZeroAlloc(receiptBytes, productIdHash, 0);
        }
    }
}

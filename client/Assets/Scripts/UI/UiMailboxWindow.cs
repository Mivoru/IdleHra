using System.Collections.Generic;
using UnityEngine;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.2. Inbox panel -
    // fetches the player's claimable mail via MailboxCache (an authenticated
    // HTTP GET, see that class's own comment) and dispatches
    // CommandType.ClaimMailItem (11) through WebSocketClient.
    // SendMailCommandZeroAlloc, which already existed on the wire protocol
    // before this window did. Rows are pooled via UIComponentPool, mirroring
    // UiMarketBrowserWindow's exact pattern.
    public class UiMailboxWindow : MonoBehaviour
    {
        [Header("Inbox HUD")]
        public Transform RowContainer;
        public UiMailboxEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 10;

        public WebSocketClient NetworkClient;

        private UIComponentPool<UiMailboxEntryRow> _rowPool;
        private readonly List<UiMailboxEntryRow> _activeRows = new List<UiMailboxEntryRow>();
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiMailboxEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            MailboxCache.OnMailboxCacheUpdated += HandleCacheUpdated;
            MailboxCache.Refresh();
        }

        private void OnDisable()
        {
            MailboxCache.OnMailboxCacheUpdated -= HandleCacheUpdated;
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

            IReadOnlyList<MailboxEntryData> entries = MailboxCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                MailboxEntryData entry = entries[i];
                UiMailboxEntryRow row = _rowPool.Spawn();
                row.Bind(entry.Id, entry.BaseItemId, entry.Quantity, entry.GoldAttachment, entry.HasEquipmentAttachment, HandleClaimClicked);
                _activeRows.Add(row);
            }
        }

        private void HandleClaimClicked(long mailId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendMailCommandZeroAlloc((byte)CommandType.ClaimMailItem, mailId);
            }

            MailboxCache.RemoveEntryLocally(mailId);
        }
    }
}

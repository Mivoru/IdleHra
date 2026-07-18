using System.Collections.Generic;
using UnityEngine;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Full-Game UI Architecture, Part 2. Sell-side counterpart to
    // UiMarketBrowserWindow's buy-side list - lists the player's own
    // EquipmentInventoryCache.OwnedEquipment as sell candidates and
    // dispatches CommandType.MarketListItem (9) with a player-chosen price
    // via WebSocketClient.SendMarketCommandZeroAlloc, mirroring
    // UiBankVaultWindow's backpack-half pooling pattern exactly.
    public class UiMarketSellPanel : MonoBehaviour
    {
        public EquipmentInventoryCache InventoryCache;
        public WebSocketClient NetworkClient;

        public Transform RowContainer;
        public UiMarketSellCandidateRow RowPrefab;
        public int InitialRowPoolCapacity = 20;

        private UIComponentPool<UiMarketSellCandidateRow> _rowPool;
        private readonly List<UiMarketSellCandidateRow> _activeRows = new List<UiMarketSellCandidateRow>();
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiMarketSellCandidateRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated += HandleCacheUpdated;
                InventoryCache.RequestSnapshot();
            }
        }

        private void OnDisable()
        {
            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated -= HandleCacheUpdated;
            }
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
            if (_rowPool == null || InventoryCache == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<ForgeEquipmentInstanceData> owned = InventoryCache.OwnedEquipment;
            for (int i = 0; i < owned.Count; i++)
            {
                ForgeEquipmentInstanceData entry = owned[i];
                UiMarketSellCandidateRow row = _rowPool.Spawn();
                row.Bind(entry.Id, entry.BaseItemId, entry.QualityTier, entry.IsAffixLocked, HandleSellClicked);
                _activeRows.Add(row);
            }
        }

        private void HandleSellClicked(long instanceId, int price)
        {
            if (NetworkClient != null)
            {
                // 9 = MarketListItem - dispatches into MarketEscrowEngine's
                // listing handler on the server (see CommandType.MarketListItem).
                NetworkClient.SendMarketCommandZeroAlloc(9, instanceId, price);
            }

            Invoke(nameof(RequestBackpackRefresh), 1.0f);
        }

        private void RequestBackpackRefresh()
        {
            if (InventoryCache != null)
            {
                InventoryCache.RequestSnapshot();
            }
        }
    }
}

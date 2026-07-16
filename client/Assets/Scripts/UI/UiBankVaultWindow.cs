using System.Collections.Generic;
using UnityEngine;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.2, refactored in
    // Phase 2 Part 3.2. Vault (bank) panel - two side-by-side lists:
    // BankVaultCache-backed stored items (withdraw) and
    // EquipmentInventoryCache-backed owned backpack items (deposit). Both
    // dispatch CommandType.WithdrawFromBank (13) / DepositToBank (12)
    // through WebSocketClient.SendMailCommandZeroAlloc directly by each
    // row's real EquipmentInstances.Id/BankEquipmentInstances.Id - the
    // player selects an item from a live-populated grid, never types or
    // pastes a raw instance id (the previous TMP_InputField-based deposit
    // flow this replaces). Rows are pooled via UIComponentPool, mirroring
    // UiMarketBrowserWindow.
    public class UiBankVaultWindow : MonoBehaviour
    {
        [Header("Vault Contents (Withdraw)")]
        public Transform VaultRowContainer;
        public UiBankVaultEntryRow VaultRowPrefab;
        public int InitialVaultRowPoolCapacity = 20;

        [Header("Backpack (Deposit)")]
        public EquipmentInventoryCache InventoryCache;
        public Transform BackpackRowContainer;
        public UiBankDepositCandidateRow BackpackRowPrefab;
        public int InitialBackpackRowPoolCapacity = 20;

        public WebSocketClient NetworkClient;

        private UIComponentPool<UiBankVaultEntryRow> _vaultRowPool;
        private readonly List<UiBankVaultEntryRow> _activeVaultRows = new List<UiBankVaultEntryRow>();
        private bool _isVaultDirty;

        private UIComponentPool<UiBankDepositCandidateRow> _backpackRowPool;
        private readonly List<UiBankDepositCandidateRow> _activeBackpackRows = new List<UiBankDepositCandidateRow>();
        private bool _isBackpackDirty;

        private void Awake()
        {
            if (VaultRowPrefab != null && VaultRowContainer != null)
            {
                _vaultRowPool = new UIComponentPool<UiBankVaultEntryRow>(VaultRowPrefab, VaultRowContainer, InitialVaultRowPoolCapacity);
            }

            if (BackpackRowPrefab != null && BackpackRowContainer != null)
            {
                _backpackRowPool = new UIComponentPool<UiBankDepositCandidateRow>(BackpackRowPrefab, BackpackRowContainer, InitialBackpackRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            BankVaultCache.OnBankVaultCacheUpdated += HandleVaultCacheUpdated;
            BankVaultCache.Refresh();

            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated += HandleBackpackCacheUpdated;
                InventoryCache.RequestSnapshot();
            }
        }

        private void OnDisable()
        {
            BankVaultCache.OnBankVaultCacheUpdated -= HandleVaultCacheUpdated;

            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated -= HandleBackpackCacheUpdated;
            }
        }

        private void Update()
        {
            if (_isVaultDirty)
            {
                RefreshVaultRows();
                _isVaultDirty = false;
            }

            if (_isBackpackDirty)
            {
                RefreshBackpackRows();
                _isBackpackDirty = false;
            }
        }

        private void HandleVaultCacheUpdated()
        {
            _isVaultDirty = true;
        }

        private void HandleBackpackCacheUpdated()
        {
            _isBackpackDirty = true;
        }

        private void RefreshVaultRows()
        {
            if (_vaultRowPool == null) return;

            for (int i = 0; i < _activeVaultRows.Count; i++)
            {
                _vaultRowPool.Despawn(_activeVaultRows[i]);
            }
            _activeVaultRows.Clear();

            IReadOnlyList<BankVaultEntryData> entries = BankVaultCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                BankVaultEntryData entry = entries[i];
                UiBankVaultEntryRow row = _vaultRowPool.Spawn();
                row.Bind(entry.Id, entry.BaseItemId, entry.QualityTier, entry.IsAffixLocked, HandleWithdrawClicked);
                _activeVaultRows.Add(row);
            }
        }

        private void RefreshBackpackRows()
        {
            if (_backpackRowPool == null || InventoryCache == null) return;

            for (int i = 0; i < _activeBackpackRows.Count; i++)
            {
                _backpackRowPool.Despawn(_activeBackpackRows[i]);
            }
            _activeBackpackRows.Clear();

            IReadOnlyList<ForgeEquipmentInstanceData> owned = InventoryCache.OwnedEquipment;
            for (int i = 0; i < owned.Count; i++)
            {
                ForgeEquipmentInstanceData entry = owned[i];
                UiBankDepositCandidateRow row = _backpackRowPool.Spawn();
                row.Bind(entry.Id, entry.BaseItemId, entry.QualityTier, entry.IsAffixLocked, HandleDepositClicked);
                _activeBackpackRows.Add(row);
            }
        }

        private void HandleWithdrawClicked(long bankId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendMailCommandZeroAlloc((byte)CommandType.WithdrawFromBank, bankId);
            }

            BankVaultCache.RemoveEntryLocally(bankId);

            // Safety-net re-fetch shortly after - a rejected withdrawal
            // (e.g. TransactionPending, no inventory space) is not signaled
            // synchronously here, so this reconciles the visible list
            // against real server state a moment later, mirroring
            // UiGuildRaidPanel's identical pattern.
            Invoke(nameof(RequestVaultRefresh), 1.0f);
        }

        private void HandleDepositClicked(long instanceId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendMailCommandZeroAlloc((byte)CommandType.DepositToBank, instanceId);
            }

            Invoke(nameof(RequestBackpackRefresh), 1.0f);
            Invoke(nameof(RequestVaultRefresh), 1.0f);
        }

        private void RequestVaultRefresh()
        {
            BankVaultCache.Refresh();
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

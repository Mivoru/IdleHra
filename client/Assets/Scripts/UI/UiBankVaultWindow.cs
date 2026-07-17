using System.Collections.Generic;
using TMPro;
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
        public VisualSyncProxy SyncProxy;
        public TextMeshProUGUI HeaderText;

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
        private readonly char[] _headerBuffer = new char[32];

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

            if (HeaderText != null)
            {
                byte activeLanguage = SyncProxy == null || SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;
                int offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.HeaderBankVault, _headerBuffer, 0);
                HeaderText.SetCharArray(_headerBuffer, 0, offset);
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

        // Modul: Advanced Economy Refactoring, Part 1.4. Allocation-free
        // vault sorting and filtering. SortVaultByItemPowerDescending
        // orders rows by derived Item Power (RegionTier and QualityTier,
        // mirroring the server's EquipmentLevelGate formula) descending;
        // VaultRarityFilterMask is a bitmask over QualityTier (bit t set =
        // tier t visible; 0 = no filter, everything visible). Both are
        // plain public fields a settings panel can toggle, followed by
        // MarkVaultDirty() to re-render.
        public bool SortVaultByItemPowerDescending;
        public uint VaultRarityFilterMask;

        // Grow-only primitive scratch arrays - resized only when the vault
        // outgrows them, never per refresh, so steady-state re-sorts and
        // re-filters allocate exactly zero managed heap bytes. Parallel
        // index/power arrays + in-place insertion sort instead of
        // List.Sort with a comparator (which allocates the delegate and
        // boxes through the comparer path) or LINQ OrderBy (which
        // allocates enumerators and buffers).
        private int[] _vaultSortIndices = new int[64];
        private int[] _vaultSortPowers = new int[64];

        public void MarkVaultDirty()
        {
            _isVaultDirty = true;
        }

        private static int DeriveItemPower(string baseItemId, int qualityTier)
        {
            int regionTier = 1;
            if (ClientContentRegistry.TryGetItemByBaseId(baseItemId, out var itemEntry))
            {
                regionTier = itemEntry.RegionTier;
            }
            if (regionTier < 1) regionTier = 1;
            if (qualityTier < 0) qualityTier = 0;

            return (regionTier - 1) * 10 + qualityTier * 2;
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
            int count = entries.Count;

            if (_vaultSortIndices.Length < count)
            {
                int newSize = _vaultSortIndices.Length;
                while (newSize < count) newSize *= 2;
                _vaultSortIndices = new int[newSize];
                _vaultSortPowers = new int[newSize];
            }

            for (int i = 0; i < count; i++)
            {
                _vaultSortIndices[i] = i;
                _vaultSortPowers[i] = DeriveItemPower(entries[i].BaseItemId, entries[i].QualityTier);
            }

            if (SortVaultByItemPowerDescending)
            {
                // In-place insertion sort over the parallel primitive
                // arrays - stable, allocation-free, and O(n) on the
                // already-sorted refreshes that dominate in practice.
                for (int i = 1; i < count; i++)
                {
                    int power = _vaultSortPowers[i];
                    int index = _vaultSortIndices[i];
                    int j = i - 1;
                    while (j >= 0 && _vaultSortPowers[j] < power)
                    {
                        _vaultSortPowers[j + 1] = _vaultSortPowers[j];
                        _vaultSortIndices[j + 1] = _vaultSortIndices[j];
                        j--;
                    }
                    _vaultSortPowers[j + 1] = power;
                    _vaultSortIndices[j + 1] = index;
                }
            }

            for (int i = 0; i < count; i++)
            {
                BankVaultEntryData entry = entries[_vaultSortIndices[i]];

                if (VaultRarityFilterMask != 0u)
                {
                    int tierBit = entry.QualityTier;
                    if (tierBit < 0 || tierBit > 31 || (VaultRarityFilterMask & (1u << tierBit)) == 0u)
                    {
                        continue;
                    }
                }

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

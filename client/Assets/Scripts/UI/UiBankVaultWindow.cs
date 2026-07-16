using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.2. Vault (bank)
    // panel - fetches the player's stored equipment via BankVaultCache and
    // dispatches CommandType.WithdrawFromBank (13) / DepositToBank (12)
    // through WebSocketClient.SendMailCommandZeroAlloc, both of which
    // already existed on the wire protocol before this window did (see
    // MailboxAndBankEngine.WithdrawFromBankAsync/DepositToBankAsync). Rows
    // are pooled via UIComponentPool, mirroring UiMarketBrowserWindow.
    //
    // Deposit source note: depositing takes an EquipmentInstances.Id the
    // player currently owns. This panel exposes a plain instance-id input
    // field for that (mirroring UiMarketBrowserWindow's own BaseItemIdInput/
    // QualityTierInput pattern for caller-supplied ids) rather than
    // integrating with the separate equipment-inventory selection UI, which
    // is out of this panel's scope.
    public class UiBankVaultWindow : MonoBehaviour
    {
        [Header("Vault HUD")]
        public Transform RowContainer;
        public UiBankVaultEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 20;

        [Header("Deposit")]
        public TMP_InputField DepositInstanceIdInput;
        public Button DepositButton;

        public WebSocketClient NetworkClient;

        private UIComponentPool<UiBankVaultEntryRow> _rowPool;
        private readonly List<UiBankVaultEntryRow> _activeRows = new List<UiBankVaultEntryRow>();
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiBankVaultEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (DepositButton != null)
            {
                DepositButton.onClick.AddListener(HandleDepositClicked);
            }
        }

        private void OnEnable()
        {
            BankVaultCache.OnBankVaultCacheUpdated += HandleCacheUpdated;
            BankVaultCache.Refresh();
        }

        private void OnDisable()
        {
            BankVaultCache.OnBankVaultCacheUpdated -= HandleCacheUpdated;
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

            IReadOnlyList<BankVaultEntryData> entries = BankVaultCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                BankVaultEntryData entry = entries[i];
                UiBankVaultEntryRow row = _rowPool.Spawn();
                row.Bind(entry.Id, entry.BaseItemId, entry.QualityTier, entry.IsAffixLocked, HandleWithdrawClicked);
                _activeRows.Add(row);
            }
        }

        private void HandleWithdrawClicked(long bankId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendMailCommandZeroAlloc((byte)CommandType.WithdrawFromBank, bankId);
            }

            BankVaultCache.RemoveEntryLocally(bankId);
        }

        private void HandleDepositClicked()
        {
            if (DepositInstanceIdInput == null || NetworkClient == null) return;
            if (!long.TryParse(DepositInstanceIdInput.text, out long instanceId) || instanceId <= 0) return;

            NetworkClient.SendMailCommandZeroAlloc((byte)CommandType.DepositToBank, instanceId);
            DepositInstanceIdInput.text = string.Empty;

            // Safety-net re-fetch shortly after, mirroring UiGuildRaidPanel's
            // identical pattern - deposit success/failure is not signaled
            // synchronously, so this reconciles the visible list against
            // real server state a moment later.
            Invoke(nameof(RequestRefresh), 1.0f);
        }

        private void RequestRefresh()
        {
            BankVaultCache.Refresh();
        }
    }
}

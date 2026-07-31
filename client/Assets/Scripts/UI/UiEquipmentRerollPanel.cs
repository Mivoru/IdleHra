using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.AddressableAssets;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 21/24: Equipment affix reroll panel. Event-driven only - the owned-item
    // list, affix slots, and cost text are rebuilt from
    // EquipmentInventoryCache.OnSnapshotUpdated or a selection click, never from
    // an Update() loop.
    //
    // AssetRegistry note: SelectedItemAssetReference resolves the item's mapped
    // AssetReference and feeds it to ItemViewer (see UiForgeItemViewer) - a compact
    // 3D preview mirroring UiCodex3DViewer's approach, added once
    // UiForgeEquipmentRow/this panel actually had somewhere to show one.
    public class UiEquipmentRerollPanel : MonoBehaviour
    {
        public EquipmentInventoryCache InventoryCache;
        public WebSocketClient NetworkClient;
        public VisualSyncProxy SyncProxy;
        [SerializeField] private AssetRegistry assetRegistry;
        public UiForgeItemViewer ItemViewer;

        public AssetReference SelectedItemAssetReference { get; private set; }

        [Header("Owned Equipment List - Pooled")]
        public Transform RowContainer;
        public UiForgeEquipmentRow RowPrefab;
        public int InitialRowPoolCapacity = 8;

        [Header("Selected Item Detail")]
        public TextMeshProUGUI SelectedItemNameText;
        public TextMeshProUGUI[] AffixSlotTexts;
        public Button[] AffixSlotButtons;
        public GameObject[] AffixSlotSelectedHighlights;

        [Header("Reroll Cost")]
        public TextMeshProUGUI RerollCostText;
        public Button RerollButton;

        [Header("Cost Color - Insufficient Funds Indicator")]
        public Color AffordableCostColor = Color.white;
        public Color UnaffordableCostColor = Color.red;

        // Mirrors AffixRerollEngine.ExecuteRerollAsync's cost formula exactly for
        // preview purposes only; the server remains the sole source of truth for
        // the actual charge.
        // Modul: Affix System Unification. The cost formula now lives in one
        // place per side - ClientAffixRegistry.GetRerollDiamondCost mirrors the
        // server's AffixRegistry.CalculateRerollDiamondCost - instead of being
        // re-derived from loose constants here.

        private UIComponentPool<UiForgeEquipmentRow> _rowPool;
        private readonly List<UiForgeEquipmentRow> _activeRows = new List<UiForgeEquipmentRow>();
        private readonly List<string> _selectedAffixKeys = new List<string>();

        private readonly char[] _nameBuffer = new char[128];
        private readonly char[] _affixBuffer = new char[64];
        private readonly char[] _costBuffer = new char[48];

        private long _selectedItemId = -1L;
        private int _selectedAffixIndex = -1;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiForgeEquipmentRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (AffixSlotButtons != null)
            {
                for (int i = 0; i < AffixSlotButtons.Length; i++)
                {
                    int affixIndex = i;
                    if (AffixSlotButtons[i] != null)
                    {
                        AffixSlotButtons[i].onClick.AddListener(() => HandleAffixSlotSelected(affixIndex));
                    }
                }
            }

            if (RerollButton != null)
            {
                RerollButton.onClick.AddListener(HandleRerollClicked);
            }
        }

        private void OnEnable()
        {
            if (InventoryCache == null) return;

            InventoryCache.OnSnapshotUpdated += HandleSnapshotUpdated;
            InventoryCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            if (InventoryCache == null) return;

            InventoryCache.OnSnapshotUpdated -= HandleSnapshotUpdated;
        }

        private void HandleSnapshotUpdated()
        {
            RefreshRowList();
            RefreshSelectedItemDetail();
        }

        private void RefreshRowList()
        {
            if (_rowPool == null || InventoryCache == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<ForgeEquipmentInstanceData> owned = InventoryCache.OwnedEquipment;

            if (_selectedItemId < 0 && owned.Count > 0)
            {
                _selectedItemId = owned[0].Id;
            }

            for (int i = 0; i < owned.Count; i++)
            {
                ForgeEquipmentInstanceData item = owned[i];
                // Modul: 6-slot equipment. Checked only weapon and the single
                // old "Armor" slot, so a helmet, gloves, boots or leggings the
                // character was wearing did not read as equipped here.
                bool isEquipped = SyncProxy != null && (
                    item.Id == SyncProxy.VisualEquippedWeaponId
                    || item.Id == SyncProxy.VisualEquippedArmorId
                    || item.Id == SyncProxy.VisualEquippedHelmetId
                    || item.Id == SyncProxy.VisualEquippedGlovesId
                    || item.Id == SyncProxy.VisualEquippedBootsId
                    || item.Id == SyncProxy.VisualEquippedOffhandId
                    || item.Id == SyncProxy.VisualEquippedLeggingsId);
                UiForgeEquipmentRow row = _rowPool.Spawn();
                row.Bind(item.Id, item.BaseItemId, item.QualityTier, item.IsAffixLocked, item.Id == _selectedItemId, HandleItemSelected, isEquipped, HandleItemEquipClicked);
                _activeRows.Add(row);
            }
        }

        private void HandleItemEquipClicked(long itemId)
        {
            if (NetworkClient == null) return;

            NetworkClient.SendEquipItemCommandZeroAlloc(itemId);
            Invoke(nameof(RefreshAfterDispatch), 0.5f);
        }

        private void HandleItemSelected(long itemId)
        {
            if (_selectedItemId == itemId) return;

            _selectedItemId = itemId;
            _selectedAffixIndex = -1;
            RefreshRowList();
            RefreshSelectedItemDetail();
        }

        private void HandleAffixSlotSelected(int affixIndex)
        {
            if (affixIndex >= _selectedAffixKeys.Count) return;

            _selectedAffixIndex = affixIndex;
            RefreshAffixSelectionHighlights();
            RefreshRerollAvailability();
        }

        private void RefreshSelectedItemDetail()
        {
            ForgeEquipmentInstanceData selected = FindSelectedItem();

            _selectedAffixKeys.Clear();

            if (selected == null)
            {
                if (SelectedItemNameText != null) SelectedItemNameText.SetCharArray(Array.Empty<char>(), 0, 0);
                ClearAffixSlots();
                if (RerollCostText != null) RerollCostText.SetCharArray(Array.Empty<char>(), 0, 0);
                if (RerollButton != null) RerollButton.interactable = false;
                SelectedItemAssetReference = null;
                if (ItemViewer != null) ItemViewer.Clear();
                return;
            }

            if (SelectedItemNameText != null)
            {
                int offset = WriteTextToBuffer(_nameBuffer, 0, "T");
                offset = WriteIntToBuffer(_nameBuffer, offset, selected.QualityTier);
                offset = WriteTextToBuffer(_nameBuffer, offset, " - ");
                offset = WriteTextToBuffer(_nameBuffer, offset, selected.BaseItemId);
                SelectedItemNameText.SetCharArray(_nameBuffer, 0, offset);
            }

            SelectedItemAssetReference = (assetRegistry != null && assetRegistry.TryGetItemAsset(selected.BaseItemId, out AssetReference itemAssetRef))
                ? itemAssetRef
                : null;

            if (ItemViewer != null)
            {
                ItemViewer.ShowItem(SelectedItemAssetReference != null ? SelectedItemAssetReference.AssetGUID : null);
            }

            BindAffixSlots(selected);

            if (_selectedAffixIndex < 0 && _selectedAffixKeys.Count > 0 && !selected.IsAffixLocked)
            {
                _selectedAffixIndex = 0;
            }
            else if (selected.IsAffixLocked || _selectedAffixIndex >= _selectedAffixKeys.Count)
            {
                _selectedAffixIndex = -1;
            }

            RefreshAffixSelectionHighlights();

            long cost = ClientAffixRegistry.GetRerollDiamondCost(selected.QualityTier);
            uint balance = SyncProxy != null ? SyncProxy.VisualPremiumCurrencyBalance : 0u;
            bool canAfford = balance >= cost;

            if (RerollCostText != null)
            {
                int offset = WriteLongToBuffer(_costBuffer, 0, cost);
                offset = WriteTextToBuffer(_costBuffer, offset, " Premium Diamonds");
                RerollCostText.SetCharArray(_costBuffer, 0, offset);
                RerollCostText.color = canAfford ? AffordableCostColor : UnaffordableCostColor;
            }

            RefreshRerollAvailability();
        }

        private void RefreshRerollAvailability()
        {
            ForgeEquipmentInstanceData selected = FindSelectedItem();
            if (RerollButton == null) return;

            if (selected == null || selected.IsAffixLocked || _selectedAffixIndex < 0)
            {
                RerollButton.interactable = false;
                return;
            }

            long cost = ClientAffixRegistry.GetRerollDiamondCost(selected.QualityTier);
            uint balance = SyncProxy != null ? SyncProxy.VisualPremiumCurrencyBalance : 0u;
            RerollButton.interactable = balance >= cost;
        }

        private void BindAffixSlots(ForgeEquipmentInstanceData selected)
        {
            foreach (KeyValuePair<string, int> affix in selected.Affixes)
            {
                _selectedAffixKeys.Add(affix.Key);
            }

            int slotCount = AffixSlotTexts != null ? AffixSlotTexts.Length : 0;

            for (int i = 0; i < slotCount; i++)
            {
                TextMeshProUGUI slotText = AffixSlotTexts[i];
                if (slotText == null) continue;

                if (i >= _selectedAffixKeys.Count)
                {
                    slotText.gameObject.SetActive(false);
                    continue;
                }

                slotText.gameObject.SetActive(true);
                string key = _selectedAffixKeys[i];
                int magnitude = selected.Affixes[key];

                // Describe applies the flat-versus-percentage distinction, so a
                // crit_dmg_pct magnitude of 75 renders "+7.5%" rather than "75".
                slotText.text = ClientAffixRegistry.Describe(key, magnitude);
            }
        }

        private void ClearAffixSlots()
        {
            int slotCount = AffixSlotTexts != null ? AffixSlotTexts.Length : 0;
            for (int i = 0; i < slotCount; i++)
            {
                if (AffixSlotTexts[i] != null) AffixSlotTexts[i].gameObject.SetActive(false);
            }

            RefreshAffixSelectionHighlights();
        }

        private void RefreshAffixSelectionHighlights()
        {
            if (AffixSlotSelectedHighlights == null) return;

            for (int i = 0; i < AffixSlotSelectedHighlights.Length; i++)
            {
                if (AffixSlotSelectedHighlights[i] != null)
                {
                    AffixSlotSelectedHighlights[i].SetActive(i == _selectedAffixIndex);
                }
            }
        }

        // Server-generated affix keys are plain numeric slot ids (EquipmentGenerator:
        // "1"=attack, "2"=defense, "3"=crit, "4"=luck). Anything outside that range
        // (future affix types) falls back to the raw key so nothing is hidden.
        // Modul: Affix System Unification. Was a four-case switch over the
        // legacy numeric keys only, so every GDD-named affix rendered as its
        // raw id ("crit_dmg_pct") with a magnitude in tenths of a percent that
        // read as a nonsense whole number. ClientAffixRegistry knows every id
        // and which are percentages.
        private static string ResolveAffixLabel(string key)
        {
            return ClientAffixRegistry.Describe(key, 0);
        }

        private ForgeEquipmentInstanceData FindSelectedItem()
        {
            if (InventoryCache == null) return null;

            IReadOnlyList<ForgeEquipmentInstanceData> owned = InventoryCache.OwnedEquipment;
            for (int i = 0; i < owned.Count; i++)
            {
                if (owned[i].Id == _selectedItemId) return owned[i];
            }
            return null;
        }

        private void HandleRerollClicked()
        {
            if (NetworkClient == null || _selectedItemId < 0 || _selectedAffixIndex < 0) return;

            NetworkClient.SendRerollCommandZeroAlloc(_selectedItemId, _selectedAffixIndex);
            Invoke(nameof(RefreshAfterDispatch), 0.5f);
        }

        private void RefreshAfterDispatch()
        {
            if (InventoryCache != null) InventoryCache.RequestSnapshot();
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteIntToBuffer(char[] buffer, int offset, int value)
        {
            return (int)WriteLongToBuffer(buffer, offset, value);
        }

        private static int WriteLongToBuffer(char[] buffer, int offset, long value)
        {
            if (value == 0)
            {
                buffer[offset++] = '0';
                return offset;
            }

            if (value < 0)
            {
                buffer[offset++] = '-';
                value = -value;
            }

            long temp = value;
            int length = 0;
            while (temp > 0)
            {
                temp /= 10;
                length++;
            }

            int endOffset = offset + length;
            temp = value;
            for (int i = endOffset - 1; i >= offset; i--)
            {
                buffer[i] = (char)('0' + (temp % 10));
                temp /= 10;
            }
            return endOffset;
        }
    }
}

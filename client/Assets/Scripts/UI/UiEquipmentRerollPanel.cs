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

        // Modul: reroll operations, 2026-08-01.
        //
        // Three buttons rather than a dropdown: the operations are not variants
        // of one action, they cost different currencies and do different things,
        // and a dropdown would hide two thirds of that behind a click.
        [Header("Operation")]
        public Button OperationValueButton;
        public Button OperationStatTypeButton;
        public Button OperationUpgradeRarityButton;
        public GameObject[] OperationSelectedHighlights;
        public TextMeshProUGUI OperationDescriptionText;

        [Header("Auto-Reroll")]
        public Toggle AutoRerollToggle;
        public Button StopRarityCycleButton;
        public TextMeshProUGUI StopRarityText;
        public Button StopAffixCycleButton;
        public TextMeshProUGUI StopAffixText;
        public TextMeshProUGUI AutoRerollEstimateText;

        // Requested attempts. The server clamps this to
        // AutoRerollPlanner.MaxAttemptsPerRequest regardless of what is sent.
        public int AutoRerollAttempts = 25;

        // 0 = Value, 1 = StatType, 2 = UpgradeRarity. Matches
        // RerollOperation server-side and the packet's RerollOperationKind.
        private int _operationKind;

        // Stop condition. Rarity is a FLOOR (1-5); affix index is 1-based into
        // the registry with 0 meaning "any stat".
        private int _stopMinRarity = 4;
        private int _stopAffixIndex;

        // Consecutive attempts on this item, driving the escalating gold price.
        // Client-side only and advisory - the server owns the real counter, so
        // this exists purely so the quoted price does not lie between rerolls.
        private int _consecutiveAttempts;
        private long _costStreakItemId = -1;

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

            (long cost, bool payWithDiamonds) = ResolveCost(selected);
            bool canAfford = HasFunds(cost, payWithDiamonds);

            if (RerollCostText != null)
            {
                int offset = WriteLongToBuffer(_costBuffer, 0, cost);
                offset = WriteTextToBuffer(_costBuffer, offset, payWithDiamonds ? " Diamonds" : " Gold");
                RerollCostText.SetCharArray(_costBuffer, 0, offset);
                RerollCostText.color = canAfford ? AffordableCostColor : UnaffordableCostColor;
            }

            RefreshOperationUi();
            RefreshAutoRerollUi(selected);
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

            (long cost, bool payWithDiamonds) = ResolveCost(selected);

            // A Legendary affix cannot be upgraded, so the button must be dead
            // rather than charging for a no-op - the server rejects it, but the
            // player should never get far enough to be rejected.
            if (_operationKind == OperationUpgradeRarity && GetSelectedAffixRarity() >= 5)
            {
                RerollButton.interactable = false;
                return;
            }

            RerollButton.interactable = HasFunds(cost, payWithDiamonds);
        }

        private const int OperationValue = 0;
        private const int OperationStatType = 1;
        private const int OperationUpgradeRarity = 2;

        private int GetSelectedAffixRarity()
        {
            if (_selectedAffixIndex < 0 || _selectedAffixIndex >= _selectedAffixKeys.Count) return 1;
            return ClientAffixRegistry.ParseAffixRarity(_selectedAffixKeys[_selectedAffixIndex]);
        }

        // Value/stat rerolls are gold; only a rarity upgrade costs Diamonds.
        private (long Cost, bool PayWithDiamonds) ResolveCost(ForgeEquipmentInstanceData selected)
        {
            if (_operationKind == OperationUpgradeRarity)
            {
                return (ClientAffixRegistry.GetRarityUpgradeDiamondCost(GetSelectedAffixRarity()), true);
            }

            long gold = ClientAffixRegistry.GetRerollGoldCost(
                selected.QualityTier,
                _consecutiveAttempts,
                _operationKind == OperationStatType);

            return (gold, false);
        }

        private bool HasFunds(long cost, bool payWithDiamonds)
        {
            if (SyncProxy == null) return false;

            return payWithDiamonds
                ? SyncProxy.VisualPremiumCurrencyBalance >= (ulong)cost
                : SyncProxy.GetGoldBalance() >= cost;
        }

        public void HandleSelectOperationValue() => SetOperation(OperationValue);
        public void HandleSelectOperationStatType() => SetOperation(OperationStatType);
        public void HandleSelectOperationUpgradeRarity() => SetOperation(OperationUpgradeRarity);

        private void SetOperation(int operationKind)
        {
            if (_operationKind == operationKind) return;

            _operationKind = operationKind;

            // Switching operation resets the escalating gold streak: the price
            // curve is per run of the same action, and carrying a value-reroll
            // streak into a stat reroll would quote a number the server will
            // not charge.
            _consecutiveAttempts = 0;
            RefreshSelectedItemDetail();
        }

        // Cycles the rarity FLOOR. Starts at Epic because that is where the
        // announcement and glow thresholds sit - stopping below it is possible
        // but is rarely what someone turns auto-reroll on for.
        public void HandleCycleStopRarity()
        {
            _stopMinRarity++;
            if (_stopMinRarity > 5) _stopMinRarity = 2;
            RefreshSelectedItemDetail();
        }

        // Cycles through "any stat" plus every affix LEGAL for this item's
        // slot. Offering illegal affixes would let the player pick a target the
        // server will reject as unreachable.
        public void HandleCycleStopAffix()
        {
            _stopAffixIndex++;
            if (_stopAffixIndex > ClientAffixRegistry.DefinitionCount) _stopAffixIndex = 0;
            RefreshSelectedItemDetail();
        }

        private void RefreshOperationUi()
        {
            if (OperationSelectedHighlights != null)
            {
                for (int i = 0; i < OperationSelectedHighlights.Length; i++)
                {
                    if (OperationSelectedHighlights[i] != null)
                    {
                        OperationSelectedHighlights[i].SetActive(i == _operationKind);
                    }
                }
            }

            if (OperationDescriptionText != null)
            {
                OperationDescriptionText.text = _operationKind switch
                {
                    OperationStatType => "Reroll the stat type. Rarity is kept.",
                    OperationUpgradeRarity => "Raise this affix one rarity step.",
                    _ => "Reroll the value. Stat and rarity are kept."
                };
            }
        }

        private void RefreshAutoRerollUi(ForgeEquipmentInstanceData selected)
        {
            bool autoEnabled = AutoRerollToggle != null && AutoRerollToggle.isOn;

            if (StopRarityText != null)
            {
                StopRarityText.text = "Stop at " + UiRarityPalette.GetAffixRarityName(_stopMinRarity) + "+";
                StopRarityText.color = UiRarityPalette.GetAffixRarityColor(_stopMinRarity);
            }

            if (StopAffixText != null)
            {
                StopAffixText.text = _stopAffixIndex == 0
                    ? "Any stat"
                    : ClientAffixRegistry.GetAffixLabel(_stopAffixIndex - 1);
            }

            if (AutoRerollEstimateText != null)
            {
                if (!autoEnabled || selected == null)
                {
                    AutoRerollEstimateText.text = string.Empty;
                }
                else
                {
                    // Worst case, summed over the escalating curve - the number
                    // that matters before committing, not the first attempt's
                    // price which is always the cheapest one.
                    long worstCase = 0L;
                    for (int i = 0; i < AutoRerollAttempts; i++)
                    {
                        worstCase += ClientAffixRegistry.GetRerollGoldCost(
                            selected.QualityTier, i, _operationKind == OperationStatType);
                    }
                    AutoRerollEstimateText.text = "Up to " + AutoRerollAttempts + " tries, max " + worstCase + " gold";
                }
            }

            if (StopRarityCycleButton != null) StopRarityCycleButton.gameObject.SetActive(autoEnabled);
            if (StopAffixCycleButton != null) StopAffixCycleButton.gameObject.SetActive(autoEnabled);
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

                // Colour and glow the row by the affix's own rarity, through the
                // same palette the chat announcements and item names use.
                UiRarityPalette.ApplyAffixRarity(slotText, ClientAffixRegistry.ParseAffixRarity(key));

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

            bool auto = AutoRerollToggle != null && AutoRerollToggle.isOn;

            NetworkClient.SendRerollCommandZeroAlloc(
                _selectedItemId,
                _selectedAffixIndex,
                (byte)_operationKind,
                auto ? (uint)Mathf.Max(1, AutoRerollAttempts) : 0u,
                (byte)_stopMinRarity,
                (byte)_stopAffixIndex);

            // The escalating gold price is per consecutive attempt on the same
            // item. Tracked here only so the quoted number stays honest between
            // clicks; the server keeps its own count and is authoritative.
            if (_costStreakItemId != _selectedItemId)
            {
                _costStreakItemId = _selectedItemId;
                _consecutiveAttempts = 0;
            }
            _consecutiveAttempts++;

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

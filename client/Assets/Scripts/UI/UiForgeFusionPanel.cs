using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit fix. ForgeSplicingEngine.ExecuteFusionAsync is
    // a complete, real risk/reward item-enhancement mechanic (sacrifice two
    // items sharing the target's BaseItemId to roll a quality-tier upgrade,
    // at gold cost and a real chance of losing the sacrifices, locking an
    // affix slot, or vaporizing the target outright) with a working
    // zero-alloc sender (WebSocketClient.SendFusionCommandZeroAlloc) - but
    // no panel anywhere ever called it. Slot-select interaction mirrors
    // UiBreedingLabWindow's Parent A/B pattern exactly, extended to 3 slots
    // (Target + 2 Sacrifices) over the player's real EquipmentInventoryCache
    // list instead of the breeding roster.
    //
    // There is no dedicated fusion-result packet the client can await -
    // ForgeSplicingEngine's non-success outcomes (sacrifices lost, affix
    // locked, target vaporized) only log server-side and mutate
    // EquipmentInstances directly. Mirroring Breeding Lab's own solution:
    // lock the interface, request a fresh inventory snapshot after a short
    // delay, and let the refreshed row list itself be the result - whatever
    // instances still exist afterward is the real, authoritative outcome.
    public class UiForgeFusionPanel : MonoBehaviour
    {
        private const float ResultPollDelaySeconds = 1.5f;

        public EquipmentInventoryCache InventoryCache;
        public WebSocketClient NetworkClient;

        [Header("Candidate List")]
        public Transform RowContainer;
        public UiForgeFusionCandidateRow RowPrefab;
        public int InitialRowPoolCapacity = 20;

        [Header("Slots")]
        public TMP_Text TargetSlotText;
        public TMP_Text Sacrifice1SlotText;
        public TMP_Text Sacrifice2SlotText;
        public Button SelectTargetButton;
        public Button SelectSacrifice1Button;
        public Button SelectSacrifice2Button;

        [Header("Action")]
        public Button FuseButton;
        public TMP_Text StatusText;

        private enum FusionSlot { Target, Sacrifice1, Sacrifice2 }

        private UIComponentPool<UiForgeFusionCandidateRow> _rowPool;
        private readonly List<UiForgeFusionCandidateRow> _activeRows = new List<UiForgeFusionCandidateRow>();
        private readonly char[] _slotBuffer = new char[64];

        private FusionSlot _armedSlot = FusionSlot.Target;
        private long _targetId;
        private long _sacrifice1Id;
        private long _sacrifice2Id;
        private bool _hasTarget;
        private bool _hasSacrifice1;
        private bool _hasSacrifice2;
        private bool _isAwaitingResult;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiForgeFusionCandidateRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (SelectTargetButton != null) SelectTargetButton.onClick.AddListener(() => _armedSlot = FusionSlot.Target);
            if (SelectSacrifice1Button != null) SelectSacrifice1Button.onClick.AddListener(() => _armedSlot = FusionSlot.Sacrifice1);
            if (SelectSacrifice2Button != null) SelectSacrifice2Button.onClick.AddListener(() => _armedSlot = FusionSlot.Sacrifice2);
            if (FuseButton != null) FuseButton.onClick.AddListener(HandleFuseClicked);
        }

        private void OnEnable()
        {
            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated += HandleInventoryUpdated;
                InventoryCache.RequestSnapshot();
            }

            RefreshRows();
            RefreshSlotLabels();
        }

        private void OnDisable()
        {
            if (InventoryCache != null)
            {
                InventoryCache.OnSnapshotUpdated -= HandleInventoryUpdated;
            }
        }

        private void HandleInventoryUpdated()
        {
            RefreshRows();

            if (_isAwaitingResult)
            {
                _isAwaitingResult = false;
                ClearSlots();
                SetInterfaceLocked(false);
                if (StatusText != null) StatusText.text = "Result: see updated inventory above.";
            }
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
                UiForgeFusionCandidateRow row = _rowPool.Spawn();
                row.Bind(entry.Id, entry.BaseItemId, entry.QualityTier, HandleCandidateSelected);
                _activeRows.Add(row);
            }
        }

        private void HandleCandidateSelected(long instanceId, string baseItemId, int qualityTier)
        {
            switch (_armedSlot)
            {
                case FusionSlot.Target:
                    _targetId = instanceId;
                    _hasTarget = true;
                    break;
                case FusionSlot.Sacrifice1:
                    _sacrifice1Id = instanceId;
                    _hasSacrifice1 = true;
                    break;
                case FusionSlot.Sacrifice2:
                    _sacrifice2Id = instanceId;
                    _hasSacrifice2 = true;
                    break;
            }

            RefreshSlotLabels();
        }

        private void RefreshSlotLabels()
        {
            SetSlotText(TargetSlotText, "Target", _hasTarget, _targetId);
            SetSlotText(Sacrifice1SlotText, "Sacrifice 1", _hasSacrifice1, _sacrifice1Id);
            SetSlotText(Sacrifice2SlotText, "Sacrifice 2", _hasSacrifice2, _sacrifice2Id);

            if (FuseButton != null)
            {
                bool distinct = !(_hasTarget && _hasSacrifice1 && _targetId == _sacrifice1Id)
                    && !(_hasTarget && _hasSacrifice2 && _targetId == _sacrifice2Id)
                    && !(_hasSacrifice1 && _hasSacrifice2 && _sacrifice1Id == _sacrifice2Id);
                FuseButton.interactable = _hasTarget && _hasSacrifice1 && _hasSacrifice2 && distinct && !_isAwaitingResult;
            }
        }

        private void SetSlotText(TMP_Text text, string label, bool hasValue, long instanceId)
        {
            if (text == null) return;

            int offset = WriteTextToBuffer(_slotBuffer, 0, label);
            offset = WriteTextToBuffer(_slotBuffer, offset, ": ");
            offset = hasValue
                ? WriteLongToBuffer(_slotBuffer, offset, instanceId)
                : WriteTextToBuffer(_slotBuffer, offset, "(none)");
            text.SetCharArray(_slotBuffer, 0, offset);
        }

        private void HandleFuseClicked()
        {
            if (_isAwaitingResult || !_hasTarget || !_hasSacrifice1 || !_hasSacrifice2) return;
            if (NetworkClient == null) return;

            NetworkClient.SendFusionCommandZeroAlloc(_targetId, _sacrifice1Id, _sacrifice2Id);

            _isAwaitingResult = true;
            SetInterfaceLocked(true);
            if (StatusText != null) StatusText.text = "Fusing...";

            Invoke(nameof(RequestResultSnapshot), ResultPollDelaySeconds);
        }

        private void RequestResultSnapshot()
        {
            if (InventoryCache != null)
            {
                InventoryCache.RequestSnapshot();
            }
        }

        private void ClearSlots()
        {
            _hasTarget = false;
            _hasSacrifice1 = false;
            _hasSacrifice2 = false;
            RefreshSlotLabels();
        }

        private void SetInterfaceLocked(bool isLocked)
        {
            if (FuseButton != null) FuseButton.interactable = !isLocked;
            if (SelectTargetButton != null) SelectTargetButton.interactable = !isLocked;
            if (SelectSacrifice1Button != null) SelectSacrifice1Button.interactable = !isLocked;
            if (SelectSacrifice2Button != null) SelectSacrifice2Button.interactable = !isLocked;
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteLongToBuffer(char[] buffer, int offset, long value)
        {
            if (value == 0)
            {
                buffer[offset++] = '0';
                return offset;
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

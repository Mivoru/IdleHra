using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 13.4.3: Breeding Lab window. Parent slots are filled from
    // BreedingRosterCache (the player's own character roster); selecting
    // both slots requests a read-only preview from BreedingPreviewCache and
    // renders it across the 4 fixed UiGeneVectorRenderer rows (Race/Speed/
    // Crit/Yield). "Fuse Genes" dispatches the EXISTING
    // CommandType.ExecuteBreeding WebSocket command (already implemented end
    // to end by BreedingEngine.ExecuteBreedingAsync/ClientCommandValidator.
    // ValidateBreedingRequest - no new execute packet was needed) and locks
    // the interface optimistically.
    //
    // There is no dedicated "your breeding request succeeded" packet -
    // BirthNotification only bumps VillagePopulation server-side (see
    // SimulationEngine's BirthNotificationQueue consumer), which cannot be
    // correlated back to a specific request. Confirmation instead polls
    // BreedingRosterCache after sending Execute: ExecuteBreedingAsync sets
    // both parents' IsBreedingActive=true synchronously within the same
    // transaction on success, so observing that flip on the selected parents
    // is a reliable, race-free success signal; a timeout with no flip is
    // treated as a silent rejection (insufficient gold, ineligible pairing,
    // etc.) and simply unlocks the interface without playing the hatch
    // animation.
    public class UiBreedingLabWindow : MonoBehaviour
    {
        private const float ConfirmationPollIntervalSeconds = 1f;
        private const float ConfirmationPollTimeoutSeconds = 8f;

        public WebSocketClient NetworkClient;

        [Header("Roster List")]
        public ScrollRect RosterScrollRect;
        public Transform RosterRowContainer;
        public UiBreedingRosterRow RosterRowPrefab;
        public int InitialRosterPoolCapacity = 16;

        [Header("Parent Slots")]
        public TMP_Text ParentASlotText;
        public TMP_Text ParentBSlotText;
        public Button SelectParentAButton;
        public Button SelectParentBButton;

        [Header("Gene Preview Rows")]
        public UiGeneVectorRenderer RaceLocusRenderer;
        public UiGeneVectorRenderer SpeedLocusRenderer;
        public UiGeneVectorRenderer CritLocusRenderer;
        public UiGeneVectorRenderer YieldLocusRenderer;

        [Header("Breeding Summary")]
        public TMP_Text EligibilityText;
        public TMP_Text CostText;
        public TMP_Text InbredRiskText;

        [Header("Fuse Action")]
        public Button FuseGenesButton;
        public GameObject HatchingAnimationRoot;

        private UIComponentPool<UiBreedingRosterRow> _rosterRowPool;
        private readonly List<UiBreedingRosterRow> _activeRosterRows = new List<UiBreedingRosterRow>();
        private readonly char[] _slotUiBuffer = new char[64];
        private readonly char[] _costUiBuffer = new char[48];

        private string _selectedParentAId = string.Empty;
        private string _selectedParentBId = string.Empty;
        private bool _selectingSlotA = true;
        private bool _isAwaitingConfirmation;
        private float _confirmationElapsedSeconds;
        private float _confirmationNextPollAt;

        private void Awake()
        {
            Transform poolParent = RosterRowContainer != null ? RosterRowContainer : (RosterScrollRect != null ? RosterScrollRect.content : null);
            if (RosterRowPrefab != null && poolParent != null)
            {
                _rosterRowPool = new UIComponentPool<UiBreedingRosterRow>(RosterRowPrefab, poolParent, InitialRosterPoolCapacity);
            }

            if (SelectParentAButton != null) SelectParentAButton.onClick.AddListener(HandleSelectParentAClicked);
            if (SelectParentBButton != null) SelectParentBButton.onClick.AddListener(HandleSelectParentBClicked);
            if (FuseGenesButton != null) FuseGenesButton.onClick.AddListener(HandleFuseGenesClicked);

            if (HatchingAnimationRoot != null) HatchingAnimationRoot.SetActive(false);
        }

        private void OnEnable()
        {
            BreedingRosterCache.OnRosterCacheUpdated += HandleRosterUpdated;
            BreedingPreviewCache.OnPreviewCacheUpdated += HandlePreviewUpdated;
            BreedingPreviewCache.ClearPreview();
            BreedingRosterCache.RequestSnapshot();
            RefreshSlotLabels();
        }

        private void OnDisable()
        {
            BreedingRosterCache.OnRosterCacheUpdated -= HandleRosterUpdated;
            BreedingPreviewCache.OnPreviewCacheUpdated -= HandlePreviewUpdated;
        }

        // Confirmation polling only - all other state is event-driven.
        private void Update()
        {
            if (!_isAwaitingConfirmation) return;

            _confirmationElapsedSeconds += Time.deltaTime;

            if (_confirmationElapsedSeconds >= ConfirmationPollTimeoutSeconds)
            {
                _isAwaitingConfirmation = false;
                SetInterfaceLocked(false);
                return;
            }

            if (_confirmationElapsedSeconds >= _confirmationNextPollAt)
            {
                _confirmationNextPollAt = _confirmationElapsedSeconds + ConfirmationPollIntervalSeconds;
                BreedingRosterCache.RequestSnapshot();
            }
        }

        private void HandleSelectParentAClicked()
        {
            _selectingSlotA = true;
        }

        private void HandleSelectParentBClicked()
        {
            _selectingSlotA = false;
        }

        private void HandleRosterRowSelected(string characterId)
        {
            if (_selectingSlotA)
            {
                _selectedParentAId = characterId;
            }
            else
            {
                _selectedParentBId = characterId;
            }

            RefreshSlotLabels();
            RequestPreviewIfBothSlotsFilled();
        }

        private void HandleRosterUpdated()
        {
            RefreshRosterRows();

            if (_isAwaitingConfirmation)
            {
                CheckBreedingConfirmation();
            }
        }

        private void CheckBreedingConfirmation()
        {
            bool parentAConfirmed = false;
            bool parentBConfirmed = false;

            IReadOnlyList<BreedingRosterEntryData> entries = BreedingRosterCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].CharacterId == _selectedParentAId && entries[i].IsBreedingActive)
                {
                    parentAConfirmed = true;
                }
                else if (entries[i].CharacterId == _selectedParentBId && entries[i].IsBreedingActive)
                {
                    parentBConfirmed = true;
                }
            }

            if (parentAConfirmed && parentBConfirmed)
            {
                _isAwaitingConfirmation = false;
                if (HatchingAnimationRoot != null) HatchingAnimationRoot.SetActive(true);
                SetInterfaceLocked(false);
                _selectedParentAId = string.Empty;
                _selectedParentBId = string.Empty;
                RefreshSlotLabels();
                BreedingPreviewCache.ClearPreview();
            }
        }

        private void HandlePreviewUpdated()
        {
            BreedingPreviewData preview = BreedingPreviewCache.Preview;

            if (preview == null)
            {
                RaceLocusRenderer?.Clear();
                SpeedLocusRenderer?.Clear();
                CritLocusRenderer?.Clear();
                YieldLocusRenderer?.Clear();
                if (EligibilityText != null) EligibilityText.text = string.Empty;
                if (CostText != null) CostText.SetCharArray(_costUiBuffer, 0, 0);
                if (InbredRiskText != null) InbredRiskText.text = string.Empty;
                if (FuseGenesButton != null) FuseGenesButton.interactable = false;
                return;
            }

            BindLocusRenderer(RaceLocusRenderer, preview, "Race");
            BindLocusRenderer(SpeedLocusRenderer, preview, "Speed");
            BindLocusRenderer(CritLocusRenderer, preview, "Crit");
            BindLocusRenderer(YieldLocusRenderer, preview, "Yield");

            if (EligibilityText != null)
            {
                EligibilityText.text = preview.IsEligible ? "Eligible" : "Not Eligible: " + preview.IneligibleReason;
            }

            if (CostText != null)
            {
                int offset = WriteTextToBuffer(_costUiBuffer, 0, "Cost: ");
                offset = WriteLongToBuffer(_costUiBuffer, offset, preview.BreedingCostGold);
                offset = WriteTextToBuffer(_costUiBuffer, offset, "g");
                CostText.SetCharArray(_costUiBuffer, 0, offset);
            }

            if (InbredRiskText != null)
            {
                InbredRiskText.text = preview.IsInbredRisk ? "Inbreeding Risk: gene quality penalty" : string.Empty;
            }

            if (FuseGenesButton != null)
            {
                FuseGenesButton.interactable = preview.IsEligible && preview.HasSufficientGold && !_isAwaitingConfirmation;
            }
        }

        private static void BindLocusRenderer(UiGeneVectorRenderer renderer, BreedingPreviewData preview, string locusName)
        {
            if (renderer == null) return;

            for (int i = 0; i < preview.Loci.Count; i++)
            {
                if (preview.Loci[i].LocusName == locusName)
                {
                    renderer.Bind(preview.Loci[i]);
                    return;
                }
            }

            renderer.Clear();
        }

        private void RequestPreviewIfBothSlotsFilled()
        {
            if (string.IsNullOrEmpty(_selectedParentAId) || string.IsNullOrEmpty(_selectedParentBId))
            {
                BreedingPreviewCache.ClearPreview();
                return;
            }

            BreedingPreviewCache.RequestPreview(_selectedParentAId, _selectedParentBId);
        }

        private void RefreshRosterRows()
        {
            if (_rosterRowPool == null) return;

            for (int i = 0; i < _activeRosterRows.Count; i++)
            {
                _rosterRowPool.Despawn(_activeRosterRows[i]);
            }
            _activeRosterRows.Clear();

            IReadOnlyList<BreedingRosterEntryData> entries = BreedingRosterCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                UiBreedingRosterRow row = _rosterRowPool.Spawn();
                row.Bind(entries[i], HandleRosterRowSelected);
                _activeRosterRows.Add(row);
            }
        }

        private void RefreshSlotLabels()
        {
            if (ParentASlotText != null)
            {
                int offset = WriteTextToBuffer(_slotUiBuffer, 0, string.IsNullOrEmpty(_selectedParentAId) ? "Parent A: (none)" : "Parent A: " + ShortId(_selectedParentAId));
                ParentASlotText.SetCharArray(_slotUiBuffer, 0, offset);
            }

            if (ParentBSlotText != null)
            {
                int offset = WriteTextToBuffer(_slotUiBuffer, 0, string.IsNullOrEmpty(_selectedParentBId) ? "Parent B: (none)" : "Parent B: " + ShortId(_selectedParentBId));
                ParentBSlotText.SetCharArray(_slotUiBuffer, 0, offset);
            }
        }

        private static string ShortId(string characterId)
        {
            return characterId.Length > 8 ? characterId.Substring(0, 8) : characterId;
        }

        private void HandleFuseGenesClicked()
        {
            if (_isAwaitingConfirmation) return;
            if (string.IsNullOrEmpty(_selectedParentAId) || string.IsNullOrEmpty(_selectedParentBId)) return;
            if (!System.Guid.TryParse(_selectedParentAId, out System.Guid paternalGuid)) return;
            if (!System.Guid.TryParse(_selectedParentBId, out System.Guid maternalGuid)) return;

            SetInterfaceLocked(true);
            _isAwaitingConfirmation = true;
            _confirmationElapsedSeconds = 0f;
            _confirmationNextPollAt = ConfirmationPollIntervalSeconds;

            if (NetworkClient != null)
            {
                // 15 = ExecuteBreeding, dispatches into BreedingEngine.
                // ExecuteBreedingAsync on the server.
                NetworkClient.SendBreedingCommandZeroAlloc(15, paternalGuid, maternalGuid);
            }
        }

        private void SetInterfaceLocked(bool isLocked)
        {
            if (FuseGenesButton != null) FuseGenesButton.interactable = !isLocked;
            if (SelectParentAButton != null) SelectParentAButton.interactable = !isLocked;
            if (SelectParentBButton != null) SelectParentBButton.interactable = !isLocked;
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

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit fix. MentorshipEngine.EstablishMentorshipContractAsync/
    // ExecuteTerminateMentorshipAsync are complete, real cross-player features
    // (a mentee gets a real XP bonus multiplier from a level-10+ mentor,
    // terminating early - before a 7-day maturation window - penalizes the
    // mentee's XP for a day) with working zero-alloc senders, but no panel
    // anywhere ever called either. Player lookup reuses FriendsCache.
    // RequestResolve (a genuinely generic username->playerId endpoint, not
    // friend-specific) rather than duplicating that HTTP call.
    //
    // EstablishMentorshipContractAsync force-disconnects the requester on
    // any InvalidRequest outcome (no academy built, mentor below level 10,
    // mentee already has a contract) - a pre-existing, harsh-but-consistent
    // pattern this codebase uses for other commands too, not something
    // introduced here. The only one of those this panel can cheaply guard
    // against client-side is "no academy built" (VisualAcademyLevel is
    // already synced); the other two are left to the server's existing
    // behavior rather than adding new client-side validation duplication.
    public class UiMentorshipContractPanel : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 1f;

        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        [Header("Status")]
        public TextMeshProUGUI StatusText;

        [Header("Establish")]
        public TMP_InputField MentorUsernameField;
        public Button EstablishButton;

        [Header("Terminate")]
        public Button TerminateButton;

        // Modul: Play Mode audit fix. MentorshipEngine.ExecuteAssignMentorAsync
        // (Academy character-mentor-slot assignment - a village-internal
        // feature distinct from the player-to-player contract above; it
        // drives CachedMentorCount, a real XP-multiplier bonus already
        // applying server-side but with AssignMentor only ever called from
        // the dead UiCommandDispatcher grab-bag) had a real, validated
        // command (ValidateMentorshipAssignment) and a working sender
        // (SendMentorshipCommandZeroAlloc) but no UI anywhere. Reuses
        // BreedingRosterCache/UiBreedingRosterRow as-is (a generic "this
        // player's characters" list with a Bind(entry, Action<string>)
        // selection callback, not breeding-specific) rather than a new
        // roster endpoint. "Arm a slot, then click a character row to fill
        // it" mirrors UiForgeFusionPanel's own slot-select pattern. Current
        // per-slot assignments are not displayed (no client-visible read of
        // MentorshipAcademyAssignments exists) - deliberately out of scope
        // for this pass; assigning still works without seeing prior state.
        [Header("Academy Assignment")]
        public Button[] SlotButtons = System.Array.Empty<Button>();
        public GameObject[] SlotArmedIndicators = System.Array.Empty<GameObject>();
        public Transform CharacterRowContainer;
        public UiBreedingRosterRow CharacterRowPrefab;
        public int InitialCharacterRowPoolCapacity = 10;

        private UIComponentPool<UiBreedingRosterRow> _characterRowPool;
        private readonly List<UiBreedingRosterRow> _activeCharacterRows = new List<UiBreedingRosterRow>();
        private int _armedSlot = -1;
        private bool _isCharacterListDirty;

        private readonly char[] _lineBuffer = new char[96];
        private float _refreshAccumulatorSeconds;

        private void Awake()
        {
            if (EstablishButton != null) EstablishButton.onClick.AddListener(HandleEstablishClicked);
            if (TerminateButton != null) TerminateButton.onClick.AddListener(HandleTerminateClicked);

            for (int i = 0; i < SlotButtons.Length; i++)
            {
                int slotIndex = i;
                if (SlotButtons[i] != null) SlotButtons[i].onClick.AddListener(() => HandleSlotButtonClicked(slotIndex));
            }

            if (CharacterRowPrefab != null && CharacterRowContainer != null)
            {
                _characterRowPool = new UIComponentPool<UiBreedingRosterRow>(CharacterRowPrefab, CharacterRowContainer, InitialCharacterRowPoolCapacity);
            }
        }

        private void OnEnable()
        {
            _refreshAccumulatorSeconds = RefreshIntervalSeconds;

            BreedingRosterCache.OnRosterCacheUpdated += HandleCharacterCacheUpdated;
            BreedingRosterCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            BreedingRosterCache.OnRosterCacheUpdated -= HandleCharacterCacheUpdated;
        }

        private void HandleCharacterCacheUpdated()
        {
            _isCharacterListDirty = true;
        }

        private void HandleSlotButtonClicked(int slotIndex)
        {
            _armedSlot = slotIndex;

            for (int i = 0; i < SlotArmedIndicators.Length; i++)
            {
                if (SlotArmedIndicators[i] != null) SlotArmedIndicators[i].SetActive(i == slotIndex);
            }
        }

        private void RefreshCharacterRows()
        {
            if (_characterRowPool == null) return;

            for (int i = 0; i < _activeCharacterRows.Count; i++)
            {
                _characterRowPool.Despawn(_activeCharacterRows[i]);
            }
            _activeCharacterRows.Clear();

            IReadOnlyList<BreedingRosterEntryData> entries = BreedingRosterCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                UiBreedingRosterRow row = _characterRowPool.Spawn();
                row.Bind(entries[i], HandleCharacterSelected);
                _activeCharacterRows.Add(row);
            }
        }

        private void HandleCharacterSelected(string characterId)
        {
            if (NetworkClient == null || _armedSlot < 0) return;
            if (!Guid.TryParse(characterId, out Guid characterGuid)) return;

            NetworkClient.SendMentorshipCommandZeroAlloc(characterGuid, _armedSlot);
            if (StatusText != null) StatusText.text = "Assigning mentor...";
        }

        private void Update()
        {
            if (_isCharacterListDirty)
            {
                RefreshCharacterRows();
                _isCharacterListDirty = false;
            }

            _refreshAccumulatorSeconds += Time.unscaledDeltaTime;
            if (_refreshAccumulatorSeconds < RefreshIntervalSeconds) return;
            _refreshAccumulatorSeconds = 0f;

            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (SyncProxy == null) return;

            bool hasMentor = SyncProxy.VisualActiveMentorPlayerId > 0L;

            if (StatusText != null)
            {
                int offset;
                if (hasMentor)
                {
                    offset = WriteTextToBuffer(_lineBuffer, 0, "Mentor: Player ");
                    offset = WriteLongToBuffer(_lineBuffer, offset, SyncProxy.VisualActiveMentorPlayerId);
                    offset = WriteTextToBuffer(_lineBuffer, offset, "  (+");
                    offset = WriteIntToBuffer(_lineBuffer, offset, Mathf.RoundToInt((float)(SyncProxy.VisualMentorshipExpBonusMultiplier - 1.0) * 100f));
                    offset = WriteTextToBuffer(_lineBuffer, offset, "% XP)");
                }
                else if (SyncProxy.VisualAcademyLevel <= 0)
                {
                    offset = WriteTextToBuffer(_lineBuffer, 0, "No Mentor  (build a Mentorship Academy first)");
                }
                else
                {
                    offset = WriteTextToBuffer(_lineBuffer, 0, "No Mentor");
                }
                StatusText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (EstablishButton != null) EstablishButton.interactable = !hasMentor && SyncProxy.VisualAcademyLevel > 0;
            if (TerminateButton != null) TerminateButton.interactable = hasMentor;

            int academyLevel = SyncProxy.VisualAcademyLevel;
            for (int i = 0; i < SlotButtons.Length; i++)
            {
                if (SlotButtons[i] != null) SlotButtons[i].interactable = i < academyLevel;
            }
        }

        private void HandleEstablishClicked()
        {
            if (MentorUsernameField == null) return;

            string username = MentorUsernameField.text.Trim();
            if (string.IsNullOrEmpty(username)) return;

            FriendsCache.RequestResolve(username, HandleMentorResolved, HandleMentorNotFound, HandleResolveError);
        }

        private void HandleMentorResolved(long mentorPlayerId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendEstablishMentorshipCommandZeroAlloc((uint)mentorPlayerId);
            }

            if (MentorUsernameField != null) MentorUsernameField.text = string.Empty;
            if (StatusText != null) StatusText.text = "Requesting mentorship...";
        }

        private void HandleMentorNotFound()
        {
            if (StatusText != null) StatusText.text = "No player with that username.";
        }

        private void HandleResolveError(string error)
        {
            if (StatusText != null) StatusText.text = "Could not look up player: " + error;
        }

        private void HandleTerminateClicked()
        {
            if (NetworkClient != null && SyncProxy != null && SyncProxy.VisualActiveMentorPlayerId > 0L)
            {
                NetworkClient.SendTerminateMentorshipCommandZeroAlloc((uint)SyncProxy.VisualActiveMentorPlayerId);
            }
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
            return WriteLongToBuffer(buffer, offset, value);
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

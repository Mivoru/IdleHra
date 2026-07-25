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

        private readonly char[] _lineBuffer = new char[96];
        private float _refreshAccumulatorSeconds;

        private void Awake()
        {
            if (EstablishButton != null) EstablishButton.onClick.AddListener(HandleEstablishClicked);
            if (TerminateButton != null) TerminateButton.onClick.AddListener(HandleTerminateClicked);
        }

        private void OnEnable()
        {
            _refreshAccumulatorSeconds = RefreshIntervalSeconds;
        }

        private void Update()
        {
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

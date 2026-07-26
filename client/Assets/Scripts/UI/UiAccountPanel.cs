using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: UI rework. The Account screen - who you are logged in as, and
    // the two account-level actions that exist.
    //
    // Log Off already existed, buried at the bottom of the Settings screen's
    // Profile section. Account deletion did not: WebSocketClient.
    // SendGdprPurgeCommandZeroAlloc and the server's CommandType.
    // TriggerGdprPurge handler have both been real and validated for a long
    // time, but there was no UI anywhere that could reach them, so the
    // feature was unreachable dead weight (recorded as an open gap in a
    // previous audit).
    //
    // Deletion is deliberately two-step: the first tap arms the button and
    // swaps its label, the second actually sends. This is irreversible and
    // server-authoritative, so a single mis-tap must not be able to trigger
    // it. There is no server-side "are you sure" round trip to lean on.
    public class UiAccountPanel : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        [Header("Identity")]
        public TMP_Text UsernameText;
        public TMP_Text PlayerIdText;
        public TMP_Text LevelText;
        public TMP_Text GuildText;

        [Header("Danger Zone")]
        public Button DeleteAccountButton;
        public TMP_Text DeleteAccountButtonLabel;
        public TMP_Text DeleteWarningText;

        private const string DeleteIdleLabel = "Delete Account";
        private const string DeleteArmedLabel = "Tap again to confirm deletion";

        private bool _deleteArmed;

        private void Awake()
        {
            if (DeleteAccountButton != null)
            {
                DeleteAccountButton.onClick.AddListener(HandleDeleteAccountClicked);
            }
        }

        private void OnEnable()
        {
            PlayerStatisticsCache.OnStatisticsUpdated += HandleStatisticsUpdated;
            PlayerNameCache.OnPlayerNamesUpdated += RefreshIdentity;
            PlayerStatisticsCache.RequestSnapshot();

            // Re-arming has to reset every time the screen is opened -
            // leaving it armed across a screen switch would turn a stale tap
            // into an irreversible deletion.
            SetDeleteArmed(false);
            RefreshIdentity();
        }

        private void OnDisable()
        {
            PlayerStatisticsCache.OnStatisticsUpdated -= HandleStatisticsUpdated;
            PlayerNameCache.OnPlayerNamesUpdated -= RefreshIdentity;
        }

        private void RefreshIdentity()
        {
            long playerId = NetworkClient != null ? NetworkClient.LocalPlayerId : 0L;

            if (PlayerIdText != null)
            {
                PlayerIdText.text = playerId > 0 ? "Player ID: " + playerId : "Player ID: (connecting...)";
            }

            if (UsernameText != null)
            {
                string username = playerId > 0 ? PlayerNameCache.GetOrRequest(playerId) : null;
                UsernameText.text = string.IsNullOrEmpty(username) ? "Signed in" : username;
            }
        }

        private void HandleStatisticsUpdated(PlayerStatisticsData data)
        {
            if (LevelText != null)
            {
                LevelText.text = "Level " + data.Level + "   -   " + data.Xp + " XP";
            }

            if (GuildText != null)
            {
                GuildText.text = string.IsNullOrEmpty(data.GuildName) ? "Guild: none" : "Guild: " + data.GuildName;
            }

            RefreshIdentity();
        }

        private void HandleDeleteAccountClicked()
        {
            if (!_deleteArmed)
            {
                SetDeleteArmed(true);
                return;
            }

            NetworkClient?.SendGdprPurgeCommandZeroAlloc();
            SetDeleteArmed(false);

            if (DeleteWarningText != null)
            {
                DeleteWarningText.text = "Deletion requested. Your data is being purged server-side.";
            }
        }

        private void SetDeleteArmed(bool armed)
        {
            _deleteArmed = armed;

            if (DeleteAccountButtonLabel != null)
            {
                DeleteAccountButtonLabel.text = armed ? DeleteArmedLabel : DeleteIdleLabel;
            }

            if (DeleteWarningText != null)
            {
                DeleteWarningText.text = armed
                    ? "This permanently erases your characters, gold, guild membership and progress. It cannot be undone."
                    : "Permanently erases this account and all of its progress.";
            }
        }
    }
}

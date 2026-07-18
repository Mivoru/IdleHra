using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Full-Game UI Architecture, Part 1. Guild creation and invite
    // controls. GuildManagementEngine.CreateGuildAsync/JoinGuildAsync exist
    // server-side (server/FolkIdle.Server/Domain/Social/GuildManagementEngine.cs)
    // but are not exposed through any HTTP route or WebSocket CommandType -
    // unlike every other panel in this codebase, there is currently no real
    // network contract to dispatch these two actions through. The buttons
    // below are wired to a clearly-labeled no-op that logs a warning
    // instead of guessing at an unofficial packet shape (sending an
    // unrelated CommandType byte would silently misroute into whatever
    // that byte actually means server-side, e.g. SyncBillingStatus or
    // CraftItem - worse than doing nothing). Replace
    // HandleCreateGuildClicked/HandleInvitePlayerClicked once a real
    // endpoint exists.
    public class UiGuildCreatePanel : MonoBehaviour
    {
        public TMP_InputField CreateGuildNameInputField;
        public Button CreateGuildButton;

        public TMP_InputField InvitePlayerInputField;
        public Button InvitePlayerButton;

        private void Awake()
        {
            if (CreateGuildButton != null)
            {
                CreateGuildButton.onClick.AddListener(HandleCreateGuildClicked);
            }

            if (InvitePlayerButton != null)
            {
                InvitePlayerButton.onClick.AddListener(HandleInvitePlayerClicked);
            }
        }

        private void HandleCreateGuildClicked()
        {
            Debug.LogWarning("UiGuildCreatePanel: guild creation has no server endpoint yet - GuildManagementEngine.CreateGuildAsync is not exposed over the network.");
        }

        private void HandleInvitePlayerClicked()
        {
            Debug.LogWarning("UiGuildCreatePanel: guild invites have no server endpoint yet - GuildManagementEngine.JoinGuildAsync is not exposed over the network.");
        }
    }
}

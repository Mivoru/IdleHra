using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: UI audit follow-up. GuildManagementEngine.CreateGuildAsync/
    // JoinGuildAsync are now exposed over HTTP (see
    // NetworkBroadcastSystem.HandleGuildCreate/HandleGuildJoin -
    // /api/v1/guilds/create, /api/v1/guilds/join), mirroring UiLoginWindow's
    // BuildJsonPostRequest + Bearer-token pattern. "Invite" is really a
    // self-service join-by-name (JoinGuildAsync) - there is no player-to-
    // player invite/notification mechanism anywhere server-side (no
    // pending-invite table, no accept/decline flow), so the second field
    // takes a guild name to join, not a player name to invite.
    public class UiGuildCreatePanel : MonoBehaviour
    {
        public string ServerBaseUrl = "http://localhost:8080";

        public TMP_InputField CreateGuildNameInputField;
        public Button CreateGuildButton;

        public TMP_InputField InvitePlayerInputField;
        public Button InvitePlayerButton;

        public TMP_Text StatusText;

        private sealed class GuildNameRequestBody
        {
            [JsonPropertyName("guildName")] public string GuildName { get; set; } = string.Empty;
        }

        private sealed class GuildCreateResponseBody
        {
            public long GuildId { get; set; }
        }

        private sealed class GuildJoinResponseBody
        {
            public bool Joined { get; set; }
        }

        private bool _requestInFlight;

        private void Awake()
        {
            if (CreateGuildButton != null)
            {
                CreateGuildButton.onClick.AddListener(HandleCreateGuildClicked);
            }

            if (InvitePlayerButton != null)
            {
                InvitePlayerButton.onClick.AddListener(HandleJoinGuildClicked);
            }
        }

        private async void HandleCreateGuildClicked()
        {
            if (_requestInFlight) return;

            string guildName = CreateGuildNameInputField != null ? CreateGuildNameInputField.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(guildName))
            {
                SetStatus("Enter a guild name.");
                return;
            }

            _requestInFlight = true;
            SetStatus("Creating guild...");
            SetInteractable(CreateGuildButton, false);

            try
            {
                string json = JsonSerializer.Serialize(new GuildNameRequestBody { GuildName = guildName });
                using UnityWebRequest request = BuildAuthorizedJsonPostRequest("/api/v1/guilds/create", json);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.responseCode == 409)
                {
                    SetStatus("Could not create guild - name taken, already in a guild, or below level 20.");
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"Guild creation failed: {request.error}");
                    return;
                }

                GuildCreateResponseBody response = JsonSerializer.Deserialize<GuildCreateResponseBody>(request.downloadHandler.text);
                if (response == null || response.GuildId <= 0)
                {
                    SetStatus("Guild creation failed.");
                    return;
                }

                SetStatus("Guild created.");
                if (CreateGuildNameInputField != null) CreateGuildNameInputField.text = string.Empty;
            }
            catch (Exception ex)
            {
                SetStatus($"Guild creation error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
                SetInteractable(CreateGuildButton, true);
            }
        }

        private async void HandleJoinGuildClicked()
        {
            if (_requestInFlight) return;

            string guildName = InvitePlayerInputField != null ? InvitePlayerInputField.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(guildName))
            {
                SetStatus("Enter a guild name to join.");
                return;
            }

            _requestInFlight = true;
            SetStatus("Joining guild...");
            SetInteractable(InvitePlayerButton, false);

            try
            {
                string json = JsonSerializer.Serialize(new GuildNameRequestBody { GuildName = guildName });
                using UnityWebRequest request = BuildAuthorizedJsonPostRequest("/api/v1/guilds/join", json);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.responseCode == 404)
                {
                    SetStatus("No guild found with that name.");
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"Join failed: {request.error}");
                    return;
                }

                GuildJoinResponseBody response = JsonSerializer.Deserialize<GuildJoinResponseBody>(request.downloadHandler.text);
                if (response != null && response.Joined)
                {
                    SetStatus("Joined guild.");
                    if (InvitePlayerInputField != null) InvitePlayerInputField.text = string.Empty;
                }
                else
                {
                    // Modul: JoinGuildAsync also returns false when the
                    // guild requires application approval (the application
                    // row is still recorded server-side) - this message
                    // covers both that case and a genuine failure (full
                    // guild, already in a guild, below level requirement),
                    // since the response has no way to distinguish them.
                    SetStatus("Not joined - guild may require approval, be full, or you may already be in a guild.");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Join error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
                SetInteractable(InvitePlayerButton, true);
            }
        }

        private UnityWebRequest BuildAuthorizedJsonPostRequest(string path, string json)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
            UnityWebRequest request = new UnityWebRequest($"{ServerBaseUrl}{path}", "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");
            return request;
        }

        private void SetStatus(string message)
        {
            if (StatusText != null) StatusText.text = message;
        }

        private static void SetInteractable(Button button, bool interactable)
        {
            if (button != null) button.interactable = interactable;
        }
    }
}

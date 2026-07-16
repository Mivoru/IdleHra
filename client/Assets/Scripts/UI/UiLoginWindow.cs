using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: mandatory login gate shown before any gameplay UI. Auto-attempts
    // a device-ID login on Start using a GUID persisted in PlayerPrefs (the
    // idle-game convention of "just works" on first launch, no credentials
    // screen); DeviceIdInputField/LoginButton exist purely as a manual
    // override so a tester can link a second install to an existing account
    // by pasting in its device ID. A successful POST to /api/v1/auth/login
    // (see NetworkBroadcastSystem.HandleAuthLogin) stores the returned JWT on
    // WebSocketClient.AuthenticatorToken and opens the socket via
    // NetworkClient.Connect() - the socket is never opened before a JWT
    // exists. BlockingPanelRoot stays active, covering the rest of the UI,
    // until WebSocketClient.OnStateConfirmed fires (the first StateUpdatePacket
    // after a successful WS handshake), so no gameplay screen is ever visible
    // before the server has confirmed the session.
    public class UiLoginWindow : MonoBehaviour
    {
        private const string DeviceIdPrefsKey = "folkidle_device_id";

        public WebSocketClient NetworkClient;
        public string ServerBaseUrl = "http://localhost:8080";

        [Header("Blocking Panel")]
        public GameObject BlockingPanelRoot;

        // Modul: onboarding - arms the FTUE exactly once, on the first
        // state confirmation of a fresh account (see HandleStateConfirmed).
        // Both fields are optional (null-checked below) so a scene without
        // the tutorial wired up behaves exactly as before this feature
        // existed.
        [Header("Onboarding")]
        public VisualSyncProxy SyncProxy;
        public UiTutorialController TutorialController;

        [Header("Manual Override")]
        public TMP_InputField DeviceIdInputField;
        public Button LoginButton;
        public TMP_Text StatusText;

        private sealed class LoginRequestBody
        {
            [JsonPropertyName("deviceId")]
            public string DeviceId { get; set; } = string.Empty;
        }

        private sealed class LoginResponseBody
        {
            public string Token { get; set; } = string.Empty;
            public long ExpiresAtEpoch { get; set; }
        }

        private bool _loginInFlight;

        private void Awake()
        {
            if (LoginButton != null) LoginButton.onClick.AddListener(HandleLoginButtonClicked);
            if (BlockingPanelRoot != null) BlockingPanelRoot.SetActive(true);
        }

        private void OnEnable()
        {
            if (NetworkClient != null) NetworkClient.OnStateConfirmed += HandleStateConfirmed;
        }

        private void OnDisable()
        {
            if (NetworkClient != null) NetworkClient.OnStateConfirmed -= HandleStateConfirmed;
        }

        private void Start()
        {
            string deviceId = LoadOrCreateDeviceId();
            if (DeviceIdInputField != null) DeviceIdInputField.text = deviceId;

            // Modul: the OTA content catalog check runs to completion (or
            // falls back to whatever content is already available locally
            // - see AssetManager.InitializeRemoteCatalog) before the first
            // login attempt fires, so a fresh catalog is in effect before
            // any gameplay content is ever requested. If AssetManager is
            // not present in this scene, login proceeds immediately rather
            // than blocking startup on a component that does not exist.
            if (AssetManager.Instance != null)
            {
                SetStatus("Checking for content updates...");
                AssetManager.Instance.InitializeRemoteCatalog(() => { _ = AttemptLoginAsync(deviceId); });
            }
            else
            {
                _ = AttemptLoginAsync(deviceId);
            }
        }

        private void HandleLoginButtonClicked()
        {
            if (_loginInFlight) return;

            string deviceId = DeviceIdInputField != null ? DeviceIdInputField.text : string.Empty;
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                SetStatus("Enter a device ID to log in.");
                return;
            }

            PlayerPrefs.SetString(DeviceIdPrefsKey, deviceId);
            PlayerPrefs.Save();
            _ = AttemptLoginAsync(deviceId);
        }

        private static string LoadOrCreateDeviceId()
        {
            string existing = PlayerPrefs.GetString(DeviceIdPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }

            string generated = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(DeviceIdPrefsKey, generated);
            PlayerPrefs.Save();
            return generated;
        }

        private async Task AttemptLoginAsync(string deviceId)
        {
            if (_loginInFlight) return;
            _loginInFlight = true;
            SetStatus("Logging in...");
            if (LoginButton != null) LoginButton.interactable = false;

            try
            {
                string json = JsonSerializer.Serialize(new LoginRequestBody { DeviceId = deviceId });
                byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

                using UnityWebRequest request = new UnityWebRequest($"{ServerBaseUrl}/api/v1/auth/login", "POST");
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"Login failed: {request.error}");
                    return;
                }

                LoginResponseBody response = JsonSerializer.Deserialize<LoginResponseBody>(request.downloadHandler.text);
                if (response == null || string.IsNullOrEmpty(response.Token))
                {
                    SetStatus("Login failed: malformed server response.");
                    return;
                }

                WebSocketClient.AuthenticatorToken = response.Token;
                SetStatus("Connecting...");

                if (NetworkClient != null)
                {
                    NetworkClient.Connect();
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Login error: {ex.Message}");
            }
            finally
            {
                _loginInFlight = false;
                if (LoginButton != null) LoginButton.interactable = true;
            }
        }

        private void HandleStateConfirmed()
        {
            if (BlockingPanelRoot != null) BlockingPanelRoot.SetActive(false);

            // Modul: fires on every reconnect, not just the very first
            // login - TutorialController.BeginTutorial() is itself
            // idempotent (both the PlayerPrefs completion check and
            // TutorialStateMachine.Begin only arming from Inactive), so a
            // reconnect on an account that already progressed or completed
            // never restarts the flow.
            if (TutorialController != null && SyncProxy != null && SyncProxy.VisualIsFreshAccount)
            {
                TutorialController.BeginTutorial();
            }
        }

        private void SetStatus(string message)
        {
            if (StatusText != null) StatusText.text = message;
        }
    }
}

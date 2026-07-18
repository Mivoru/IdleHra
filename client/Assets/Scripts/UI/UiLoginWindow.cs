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
    // Modul: Email/Password Auth. Mandatory gate shown before any gameplay
    // UI. On Start(), silently attempts a "remembered device" login (see
    // AuthenticationEngine.TryLoginByDeviceIdAsync on the server - a
    // read-only lookup that never auto-provisions) using a GUID persisted
    // in PlayerPrefs. A hit means this device previously completed a real
    // Register/email login and skips straight past any UI into Connect();
    // a miss shows the Choice screen (Login vs Register), never an
    // anonymous auto-created account. BlockingPanelRoot stays active,
    // covering the rest of the game, until WebSocketClient.OnStateConfirmed
    // fires (the first StateUpdatePacket after a successful WS handshake) -
    // no gameplay screen is ever visible before the server has confirmed
    // the session. LogOff() (wired from Settings/Profile) reverses all of
    // this: disconnects, forgets the remembered device, and returns to the
    // Choice screen without restarting the app.
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

        [Header("Choice Screen")]
        public GameObject ChoiceRoot;
        public Button ShowLoginButton;
        public Button ShowRegisterButton;

        [Header("Login Screen")]
        public GameObject LoginRoot;
        public TMP_InputField LoginEmailField;
        public TMP_InputField LoginPasswordField;
        public Button LoginSubmitButton;
        public Button LoginBackButton;

        [Header("Register Screen - Step 1 (email)")]
        public GameObject RegisterStep1Root;
        public TMP_InputField RegisterEmailField;
        public Button RegisterNextButton;
        public Button RegisterStep1BackButton;

        [Header("Register Screen - Step 2 (username/password)")]
        public GameObject RegisterStep2Root;
        public TMP_Text RegisterStep2EmailLabel;
        public TMP_InputField RegisterUsernameField;
        public TMP_InputField RegisterPasswordField;
        public TMP_InputField RegisterConfirmPasswordField;
        public Button RegisterSubmitButton;
        public Button RegisterStep2BackButton;

        [Header("Status")]
        public TMP_Text StatusText;

        private sealed class LoginRequestBody
        {
            [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
            [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
            [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
            [JsonPropertyName("rememberedDeviceId")] public string RememberedDeviceId { get; set; } = string.Empty;
        }

        private sealed class RegisterRequestBody
        {
            [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
            [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
            [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
            [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
        }

        private sealed class CheckEmailRequestBody
        {
            [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
        }

        private sealed class AuthResponseBody
        {
            public string Token { get; set; } = string.Empty;
            public long ExpiresAtEpoch { get; set; }
        }

        private sealed class CheckEmailResponseBody
        {
            public bool Available { get; set; }
        }

        private sealed class RegisterErrorResponseBody
        {
            public string Reason { get; set; } = string.Empty;
        }

        private string _deviceId = string.Empty;
        private string _pendingRegisterEmail = string.Empty;
        private bool _requestInFlight;

        private void Awake()
        {
            if (BlockingPanelRoot != null) BlockingPanelRoot.SetActive(true);

            if (ShowLoginButton != null) ShowLoginButton.onClick.AddListener(HandleShowLoginClicked);
            if (ShowRegisterButton != null) ShowRegisterButton.onClick.AddListener(HandleShowRegisterClicked);

            if (LoginSubmitButton != null) LoginSubmitButton.onClick.AddListener(HandleLoginSubmitClicked);
            if (LoginBackButton != null) LoginBackButton.onClick.AddListener(ShowChoiceScreen);

            if (RegisterNextButton != null) RegisterNextButton.onClick.AddListener(HandleRegisterNextClicked);
            if (RegisterStep1BackButton != null) RegisterStep1BackButton.onClick.AddListener(ShowChoiceScreen);

            if (RegisterSubmitButton != null) RegisterSubmitButton.onClick.AddListener(HandleRegisterSubmitClicked);
            if (RegisterStep2BackButton != null) RegisterStep2BackButton.onClick.AddListener(HandleRegisterStep2BackClicked);
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
            _deviceId = LoadOrCreateDeviceId();
            HideAllScreens();

            // Modul: the OTA content catalog check runs to completion (or
            // falls back to whatever content is already available locally
            // - see AssetManager.InitializeRemoteCatalog) before the first
            // login attempt fires, so a fresh catalog is in effect before
            // any gameplay content is ever requested. If AssetManager is
            // not present in this scene, the remember-me check proceeds
            // immediately rather than blocking startup on a component that
            // does not exist.
            if (AssetManager.Instance != null)
            {
                SetStatus("Checking for content updates...");
                AssetManager.Instance.InitializeRemoteCatalog(() => { _ = AttemptRememberedLoginAsync(); });
            }
            else
            {
                _ = AttemptRememberedLoginAsync();
            }
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

        // Modul: Settings/Profile's Log Off button calls this directly.
        // Forgetting the device (not just disconnecting) is what actually
        // returns the player to the Choice screen on next launch too, not
        // just this session - a bare Disconnect() without this would leave
        // the remembered device binding intact server-side and silently
        // log the same account back in on the very next app start.
        public void LogOff()
        {
            if (NetworkClient != null) NetworkClient.Disconnect();

            WebSocketClient.AuthenticatorToken = string.Empty;
            PlayerPrefs.DeleteKey(DeviceIdPrefsKey);
            PlayerPrefs.Save();
            _deviceId = LoadOrCreateDeviceId();

            if (BlockingPanelRoot != null) BlockingPanelRoot.SetActive(true);
            ShowChoiceScreen();
        }

        private async Task AttemptRememberedLoginAsync()
        {
            if (_requestInFlight) return;
            _requestInFlight = true;
            SetStatus("Signing in...");

            try
            {
                string json = JsonSerializer.Serialize(new LoginRequestBody { RememberedDeviceId = _deviceId });
                using UnityWebRequest request = BuildJsonPostRequest("/api/v1/auth/login", json);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result == UnityWebRequest.Result.Success)
                {
                    AuthResponseBody response = JsonSerializer.Deserialize<AuthResponseBody>(request.downloadHandler.text);
                    if (response != null && !string.IsNullOrEmpty(response.Token))
                    {
                        ProceedWithToken(response.Token);
                        return;
                    }
                }

                // Modul: any failure here (404 = no remembered device, or a
                // network error) falls through to the Choice screen rather
                // than retrying or auto-provisioning - a fresh device with
                // no bound account is expected to see Login/Register, not
                // an error.
                ShowChoiceScreen();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remembered-device login failed: {ex.Message}");
                ShowChoiceScreen();
            }
            finally
            {
                _requestInFlight = false;
            }
        }

        private void HandleShowLoginClicked()
        {
            HideAllScreens();
            if (LoginRoot != null) LoginRoot.SetActive(true);
            SetStatus(string.Empty);
        }

        private void HandleShowRegisterClicked()
        {
            HideAllScreens();
            if (RegisterStep1Root != null) RegisterStep1Root.SetActive(true);
            if (RegisterEmailField != null) RegisterEmailField.text = string.Empty;
            SetStatus(string.Empty);
        }

        private void HandleRegisterStep2BackClicked()
        {
            HideAllScreens();
            if (RegisterStep1Root != null) RegisterStep1Root.SetActive(true);
            SetStatus(string.Empty);
        }

        private void ShowChoiceScreen()
        {
            HideAllScreens();
            if (ChoiceRoot != null) ChoiceRoot.SetActive(true);
            SetStatus(string.Empty);
        }

        private void HideAllScreens()
        {
            if (ChoiceRoot != null) ChoiceRoot.SetActive(false);
            if (LoginRoot != null) LoginRoot.SetActive(false);
            if (RegisterStep1Root != null) RegisterStep1Root.SetActive(false);
            if (RegisterStep2Root != null) RegisterStep2Root.SetActive(false);
        }

        private async void HandleLoginSubmitClicked()
        {
            if (_requestInFlight) return;

            string email = LoginEmailField != null ? LoginEmailField.text.Trim() : string.Empty;
            string password = LoginPasswordField != null ? LoginPasswordField.text : string.Empty;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                SetStatus("Enter your email and password.");
                return;
            }

            _requestInFlight = true;
            SetStatus("Logging in...");
            SetInteractable(LoginSubmitButton, false);

            try
            {
                string json = JsonSerializer.Serialize(new LoginRequestBody { Email = email, Password = password, DeviceId = _deviceId });
                using UnityWebRequest request = BuildJsonPostRequest("/api/v1/auth/login", json);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.responseCode == 401)
                {
                    SetStatus("Incorrect email or password.");
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"Login failed: {request.error}");
                    return;
                }

                AuthResponseBody response = JsonSerializer.Deserialize<AuthResponseBody>(request.downloadHandler.text);
                if (response == null || string.IsNullOrEmpty(response.Token))
                {
                    SetStatus("Login failed: malformed server response.");
                    return;
                }

                ProceedWithToken(response.Token);
            }
            catch (Exception ex)
            {
                SetStatus($"Login error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
                SetInteractable(LoginSubmitButton, true);
            }
        }

        private async void HandleRegisterNextClicked()
        {
            if (_requestInFlight) return;

            string email = RegisterEmailField != null ? RegisterEmailField.text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(email) || !email.Contains('@'))
            {
                SetStatus("Enter a valid email address.");
                return;
            }

            _requestInFlight = true;
            SetStatus("Checking email...");
            SetInteractable(RegisterNextButton, false);

            try
            {
                string json = JsonSerializer.Serialize(new CheckEmailRequestBody { Email = email });
                using UnityWebRequest request = BuildJsonPostRequest("/api/v1/auth/check-email", json);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"Could not check email: {request.error}");
                    return;
                }

                CheckEmailResponseBody response = JsonSerializer.Deserialize<CheckEmailResponseBody>(request.downloadHandler.text);
                if (response == null || !response.Available)
                {
                    SetStatus("That email is already registered.");
                    return;
                }

                _pendingRegisterEmail = email;
                if (RegisterStep2EmailLabel != null) RegisterStep2EmailLabel.text = email;
                if (RegisterUsernameField != null) RegisterUsernameField.text = string.Empty;
                if (RegisterPasswordField != null) RegisterPasswordField.text = string.Empty;
                if (RegisterConfirmPasswordField != null) RegisterConfirmPasswordField.text = string.Empty;

                HideAllScreens();
                if (RegisterStep2Root != null) RegisterStep2Root.SetActive(true);
                SetStatus(string.Empty);
            }
            catch (Exception ex)
            {
                SetStatus($"Email check error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
                SetInteractable(RegisterNextButton, true);
            }
        }

        private async void HandleRegisterSubmitClicked()
        {
            if (_requestInFlight) return;

            string username = RegisterUsernameField != null ? RegisterUsernameField.text.Trim() : string.Empty;
            string password = RegisterPasswordField != null ? RegisterPasswordField.text : string.Empty;
            string confirmPassword = RegisterConfirmPasswordField != null ? RegisterConfirmPasswordField.text : string.Empty;

            if (username.Length < 3 || username.Length > 20)
            {
                SetStatus("Username must be 3-20 characters.");
                return;
            }

            if (password.Length < 6)
            {
                SetStatus("Password must be at least 6 characters.");
                return;
            }

            if (password != confirmPassword)
            {
                SetStatus("Passwords do not match.");
                return;
            }

            _requestInFlight = true;
            SetStatus("Creating account...");
            SetInteractable(RegisterSubmitButton, false);

            try
            {
                string json = JsonSerializer.Serialize(new RegisterRequestBody { Email = _pendingRegisterEmail, Username = username, Password = password, DeviceId = _deviceId });
                using UnityWebRequest request = BuildJsonPostRequest("/api/v1/auth/register", json);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.responseCode == 409 || request.responseCode == 400)
                {
                    RegisterErrorResponseBody errorResponse = null;
                    try { errorResponse = JsonSerializer.Deserialize<RegisterErrorResponseBody>(request.downloadHandler.text); } catch (JsonException) { }
                    SetStatus(DescribeRegisterError(errorResponse?.Reason));
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"Registration failed: {request.error}");
                    return;
                }

                AuthResponseBody response = JsonSerializer.Deserialize<AuthResponseBody>(request.downloadHandler.text);
                if (response == null || string.IsNullOrEmpty(response.Token))
                {
                    SetStatus("Registration failed: malformed server response.");
                    return;
                }

                ProceedWithToken(response.Token);
            }
            catch (Exception ex)
            {
                SetStatus($"Registration error: {ex.Message}");
            }
            finally
            {
                _requestInFlight = false;
                SetInteractable(RegisterSubmitButton, true);
            }
        }

        private static string DescribeRegisterError(string reason)
        {
            return reason switch
            {
                "EmailInUse" => "That email is already registered.",
                "UsernameInUse" => "That username is already taken.",
                "InvalidEmail" => "Enter a valid email address.",
                "InvalidUsername" => "Username must be 3-20 characters.",
                "InvalidPassword" => "Password must be at least 6 characters.",
                _ => "Registration failed. Please try again."
            };
        }

        private UnityWebRequest BuildJsonPostRequest(string path, string json)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
            UnityWebRequest request = new UnityWebRequest($"{ServerBaseUrl}{path}", "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            return request;
        }

        private void ProceedWithToken(string token)
        {
            WebSocketClient.AuthenticatorToken = token;
            HideAllScreens();
            SetStatus("Connecting...");

            if (NetworkClient != null)
            {
                NetworkClient.Connect();
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

        private static void SetInteractable(Button button, bool interactable)
        {
            if (button != null) button.interactable = interactable;
        }
    }
}

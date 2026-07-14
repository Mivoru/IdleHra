using System;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace FolkIdle.Client.Network
{
    // Local-development-only token provider. There is no production login flow
    // yet on either side (NetworkBroadcastSystem.ActiveTokenCache is only ever
    // populated by test fixtures and DbSeeder, and both mint a fresh random Guid
    // on every run - there is no fixed, known token anywhere in this codebase to
    // hard-code as a working fallback). This reads an optional, git-ignored local
    // config file so a developer can paste in a token copied from their own dev
    // database (e.g. the AuthenticatorToken column on the PlayerRecords row for
    // DbSeeder.PlayerLowId) and connect against a real running server.
    public static class AuthTokenBootstrap
    {
        private const string ConfigFileName = "auth_config.json";

        private sealed class AuthConfigData
        {
            public string AuthenticatorToken { get; set; } = string.Empty;
        }

        public static void Initialize()
        {
            if (!string.IsNullOrEmpty(WebSocketClient.AuthenticatorToken))
            {
                return;
            }

            string configPath = Path.Combine(Application.streamingAssetsPath, ConfigFileName);

            if (!File.Exists(configPath))
            {
                Debug.LogWarning($"AuthTokenBootstrap: no {ConfigFileName} found under StreamingAssets. WebSocketClient.AuthenticatorToken stays empty until one is provided - see StreamingAssets/{ConfigFileName}.example.");
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                AuthConfigData config = JsonSerializer.Deserialize<AuthConfigData>(json);

                if (config != null && Guid.TryParse(config.AuthenticatorToken, out _))
                {
                    WebSocketClient.AuthenticatorToken = config.AuthenticatorToken;
                }
                else
                {
                    Debug.LogWarning($"AuthTokenBootstrap: {ConfigFileName} did not contain a valid AuthenticatorToken Guid.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AuthTokenBootstrap: failed to read {ConfigFileName}: {ex.Message}");
            }
        }
    }
}

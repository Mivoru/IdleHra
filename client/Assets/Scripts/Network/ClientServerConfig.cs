using UnityEngine;

namespace FolkIdle.Client.Network
{
    // Modul: server config. THE one place the client stores which server it
    // talks to.
    //
    // Before this, twenty-five separate classes each declared their own
    // `ServerBaseUrl = "http://localhost:8080"` and NOTHING anywhere ever
    // assigned a different value to any of them - not the scene builder, not
    // the login window, nothing. UiLoginWindow's copy was the only one that
    // mattered for authentication and the WebSocket handshake, so a build could
    // authenticate against a real server and then have all twenty-two HTTP
    // caches (inventory, market, guild roster, mailbox, leaderboard, codex...)
    // silently query localhost and come back empty. In practice the client
    // could only ever work on the machine that was also running the server.
    //
    // Everything now reads this. UiLoginWindow still owns the value the player
    // or the Editor supplies - it pushes into here rather than keeping a
    // private copy - so there is exactly one writer and many readers.
    public static class ClientServerConfig
    {
        // Kept as the default rather than removed: local development is the
        // normal case for this project, and a fresh install with no override
        // must still just work.
        public const string DefaultBaseUrl = "http://localhost:8080";

        private const string BaseUrlPrefsKey = "FolkIdle.Server.BaseUrl";

        // Lets a headless/CI/automated run point the client at a server without
        // touching the scene or a saved preference. Checked once, at first read.
        private const string BaseUrlEnvironmentVariable = "FOLKIDLE_SERVER_URL";

        private static string _baseUrl;
        private static bool _resolved;

        public static string BaseUrl
        {
            get
            {
                if (!_resolved) Resolve();
                return _baseUrl;
            }
        }

        // Resolution order, most specific first. The environment variable beats
        // the saved preference so an automated run cannot be derailed by
        // whatever host a previous manual session happened to save.
        private static void Resolve()
        {
            _resolved = true;

            string fromEnvironment = System.Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                _baseUrl = Normalize(fromEnvironment);
                return;
            }

            string fromPrefs = PlayerPrefs.GetString(BaseUrlPrefsKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(fromPrefs))
            {
                _baseUrl = Normalize(fromPrefs);
                return;
            }

            _baseUrl = DefaultBaseUrl;
        }

        // Called by UiLoginWindow with whatever the Editor/player supplied.
        // Persisted so the choice survives a restart, which is the whole point
        // of being able to change it at all.
        public static void SetBaseUrl(string baseUrl, bool persist = true)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return;

            _baseUrl = Normalize(baseUrl);
            _resolved = true;

            if (persist)
            {
                PlayerPrefs.SetString(BaseUrlPrefsKey, _baseUrl);
            }
        }

        // Every caller builds URLs as $"{BaseUrl}/api/v1/...", so a stored
        // value with a trailing slash would produce a double slash. Trimmed
        // here rather than at twenty-five call sites.
        private static string Normalize(string baseUrl)
        {
            string trimmed = baseUrl.Trim();
            return trimmed.EndsWith("/") ? trimmed.TrimEnd('/') : trimmed;
        }
    }
}

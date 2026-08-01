using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: guild discovery, 2026-08-01.
    //
    // The screen that made /api/v1/guilds/list reachable. Before this, joining
    // required typing a guild's EXACT name, so a player who had not been told
    // one had no way to find any guild at all.
    //
    // Paging and pooling follow UiLeaderboardWindow, which is the other REST
    // list screen in this project - same skip/take, same pooled rows, same
    // event-driven refresh.
    public class UiGuildDirectoryPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("List")]
        public Transform RowContainer;
        public UiGuildDirectoryRow RowPrefab;
        public int InitialRowPoolCapacity = 10;

        [Header("Search")]
        public TMP_InputField SearchField;
        public Button SearchButton;

        [Header("Paging")]
        public Button NextPageButton;
        public Button PrevPageButton;
        public TMP_Text PageLabelText;

        [Header("Status")]
        public TMP_Text StatusText;

        public int PageSize = 10;

        private UIComponentPool<UiGuildDirectoryRow> _rowPool;
        private readonly List<UiGuildDirectoryRow> _activeRows = new List<UiGuildDirectoryRow>();
        private int _skip;
        private bool _isDirty;
        private bool _joinInFlight;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiGuildDirectoryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (SearchButton != null) SearchButton.onClick.AddListener(HandleSearchClicked);
            if (NextPageButton != null) NextPageButton.onClick.AddListener(HandleNextPageClicked);
            if (PrevPageButton != null) PrevPageButton.onClick.AddListener(HandlePrevPageClicked);
        }

        private void OnEnable()
        {
            GuildDirectoryCache.OnGuildDirectoryUpdated += HandleCacheUpdated;
            _skip = 0;
            RequestCurrentPage();
        }

        private void OnDisable()
        {
            GuildDirectoryCache.OnGuildDirectoryUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            // Rebuilt on the main thread rather than inside the cache callback,
            // which arrives from a web request continuation and must not touch
            // Unity objects.
            if (!_isDirty) return;

            _isDirty = false;
            RefreshRows();
        }

        private void HandleCacheUpdated()
        {
            _isDirty = true;
        }

        private void HandleSearchClicked()
        {
            // A new search always restarts at page one; keeping the old offset
            // would silently show an empty page for a filter with few matches.
            _skip = 0;
            RequestCurrentPage();
        }

        private void HandleNextPageClicked()
        {
            // Only advance on a full page. A short page is the last one, and
            // advancing past it would show an empty list with no way back other
            // than paging backwards.
            if (GuildDirectoryCache.Entries.Count < PageSize) return;

            _skip += PageSize;
            RequestCurrentPage();
        }

        private void HandlePrevPageClicked()
        {
            if (_skip <= 0) return;

            _skip -= PageSize;
            if (_skip < 0) _skip = 0;
            RequestCurrentPage();
        }

        private void RequestCurrentPage()
        {
            string filter = SearchField != null ? SearchField.text : string.Empty;
            GuildDirectoryCache.RequestPage(_skip, PageSize, filter);
            SetStatus("Searching...");
        }

        private void RefreshRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                if (_activeRows[i] == null) continue;

                // Unsubscribed before release so a pooled row cannot fire a
                // join for a guild it no longer displays.
                _activeRows[i].OnJoinClicked -= HandleJoinRequested;
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<GuildDirectoryEntryData> entries = GuildDirectoryCache.Entries;
            int viewerLevel = SyncProxy != null ? SyncProxy.VisualPlayerLevel : 0;

            for (int i = 0; i < entries.Count; i++)
            {
                UiGuildDirectoryRow row = _rowPool.Spawn();
                if (row == null) continue;

                row.Bind(entries[i], viewerLevel);
                row.OnJoinClicked += HandleJoinRequested;
                _activeRows.Add(row);
            }

            if (PageLabelText != null)
            {
                PageLabelText.text = $"Page {(_skip / PageSize) + 1}";
            }

            SetStatus(entries.Count == 0
                ? "No guilds found."
                : $"{entries.Count} guild(s).");
        }

        private void HandleJoinRequested(string guildName)
        {
            if (_joinInFlight || string.IsNullOrEmpty(guildName)) return;

            _ = JoinGuildAsync(guildName);
        }

        // Posts to the SAME /api/v1/guilds/join endpoint UiGuildCreatePanel
        // uses, by name. Deliberately not a second join implementation - the
        // server resolves name to id, and duplicating that resolution here
        // would be a second answer to the same question.
        private async Task JoinGuildAsync(string guildName)
        {
            _joinInFlight = true;
            SetStatus($"Joining {guildName}...");

            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(new GuildJoinBody { GuildName = guildName });
                byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

                using UnityWebRequest request = new UnityWebRequest($"{ClientServerConfig.BaseUrl}/api/v1/guilds/join", "POST");
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {WebSocketClient.AuthenticatorToken}");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.responseCode == 404)
                {
                    SetStatus("That guild no longer exists.");
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SetStatus($"Join failed: {request.error}");
                    return;
                }

                SetStatus($"Joined {guildName}.");
            }
            catch (System.Exception ex)
            {
                SetStatus("Join failed.");
                Debug.LogWarning($"Guild join error: {ex.Message}");
            }
            finally
            {
                _joinInFlight = false;
            }
        }

        private void SetStatus(string message)
        {
            if (StatusText != null) StatusText.text = message;
        }

        private sealed class GuildJoinBody
        {
            public string GuildName { get; set; } = string.Empty;
        }
    }
}

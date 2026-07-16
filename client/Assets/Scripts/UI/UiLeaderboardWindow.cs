using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.3. Leaderboard
    // window - fetches ranked entries via LeaderboardCache (backed by
    // NetworkBroadcastSystem.HandleGlobalLeaderboard / LeaderboardCronEngine's
    // Redis sorted set, which already existed server-side) and paginates via
    // skip/take. Rows are pooled via UIComponentPool, mirroring
    // UiMarketBrowserWindow's exact pattern.
    public class UiLeaderboardWindow : MonoBehaviour
    {
        private const int PageSize = 50;

        [Header("Leaderboard HUD")]
        public Transform RowContainer;
        public UiLeaderboardEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 50;
        public Button NextPageButton;
        public Button PrevPageButton;
        public TMP_Text PageLabelText;

        private UIComponentPool<UiLeaderboardEntryRow> _rowPool;
        private readonly List<UiLeaderboardEntryRow> _activeRows = new List<UiLeaderboardEntryRow>();
        private readonly char[] _pageUiBuffer = new char[32];
        private int _skip;
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiLeaderboardEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (NextPageButton != null) NextPageButton.onClick.AddListener(HandleNextPageClicked);
            if (PrevPageButton != null) PrevPageButton.onClick.AddListener(HandlePrevPageClicked);
        }

        private void OnEnable()
        {
            LeaderboardCache.OnLeaderboardCacheUpdated += HandleCacheUpdated;
            _skip = 0;
            RequestCurrentPage();
        }

        private void OnDisable()
        {
            LeaderboardCache.OnLeaderboardCacheUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            if (!_isDirty) return;

            RefreshRows();
            _isDirty = false;
        }

        private void HandleNextPageClicked()
        {
            _skip += PageSize;
            RequestCurrentPage();
        }

        private void HandlePrevPageClicked()
        {
            if (_skip <= 0) return;
            _skip = Mathf.Max(0, _skip - PageSize);
            RequestCurrentPage();
        }

        private void RequestCurrentPage()
        {
            LeaderboardCache.RequestPage(_skip, PageSize);
        }

        private void HandleCacheUpdated()
        {
            _isDirty = true;
        }

        private void RefreshRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<LeaderboardEntryData> entries = LeaderboardCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                LeaderboardEntryData entry = entries[i];
                UiLeaderboardEntryRow row = _rowPool.Spawn();
                row.Bind(entry.Rank, entry.DisplayName, entry.Level, entry.Xp);
                _activeRows.Add(row);
            }

            if (PageLabelText != null)
            {
                int offset = WriteTextToBuffer(_pageUiBuffer, 0, "Rank ");
                offset = WriteIntToBuffer(_pageUiBuffer, offset, LeaderboardCache.CurrentSkip + 1);
                offset = WriteTextToBuffer(_pageUiBuffer, offset, "+");
                PageLabelText.SetCharArray(_pageUiBuffer, 0, offset);
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

            int temp = value;
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

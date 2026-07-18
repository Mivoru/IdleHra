using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: UI audit follow-up. Friends roster - replaces the old static
    // "Friends list is not implemented yet." placeholder. AddFriend/
    // RemoveFriend/BlockPlayer/UnblockPlayer (RelationshipEngine) already
    // existed and worked over the WebSocket wire; this is the first UI that
    // can actually see the list or trigger them. Rows are pooled via
    // UIComponentPool, mirroring UiMailboxWindow's exact pattern.
    public class UiFriendsWindow : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        [Header("Friends List - Pooled")]
        public Transform RowContainer;
        public UiFriendEntryRow RowPrefab;
        public int InitialRowPoolCapacity = 10;

        [Header("Add Friend")]
        public TMP_InputField AddFriendUsernameField;
        public Button AddFriendButton;
        public TMP_Text StatusText;

        private UIComponentPool<UiFriendEntryRow> _rowPool;
        private readonly List<UiFriendEntryRow> _activeRows = new List<UiFriendEntryRow>();
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiFriendEntryRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (AddFriendButton != null)
            {
                AddFriendButton.onClick.AddListener(HandleAddFriendClicked);
            }
        }

        private void OnEnable()
        {
            FriendsCache.OnFriendsCacheUpdated += HandleCacheUpdated;
            FriendsCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            FriendsCache.OnFriendsCacheUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            if (!_isDirty) return;

            RefreshRows();
            _isDirty = false;
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

            IReadOnlyList<FriendEntryData> entries = FriendsCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                FriendEntryData entry = entries[i];
                UiFriendEntryRow row = _rowPool.Spawn();
                row.Bind(entry.PlayerId, entry.Username, entry.Level, entry.IsBlocked, HandleRemoveClicked, HandleBlockClicked, HandleUnblockClicked);
                _activeRows.Add(row);
            }
        }

        private void HandleAddFriendClicked()
        {
            if (AddFriendUsernameField == null) return;

            string username = AddFriendUsernameField.text.Trim();
            if (string.IsNullOrEmpty(username))
            {
                SetStatus("Enter a username first.");
                return;
            }

            SetStatus("Looking up " + username + "...");
            FriendsCache.RequestResolve(username, HandleUsernameResolved, HandleUsernameNotFound, HandleResolveError);
        }

        private void HandleUsernameResolved(long targetPlayerId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendAddFriendCommandZeroAlloc(targetPlayerId);
            }

            string username = AddFriendUsernameField != null ? AddFriendUsernameField.text.Trim() : string.Empty;
            FriendsCache.AddEntryLocally(targetPlayerId, username);
            SetStatus("Friend request sent.");

            if (AddFriendUsernameField != null)
            {
                AddFriendUsernameField.text = string.Empty;
            }
        }

        private void HandleUsernameNotFound()
        {
            SetStatus("No player with that username.");
        }

        private void HandleResolveError(string error)
        {
            SetStatus("Could not check username: " + error);
        }

        private void HandleRemoveClicked(long targetPlayerId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendRemoveFriendCommandZeroAlloc(targetPlayerId);
            }

            FriendsCache.RemoveEntryLocally(targetPlayerId);
        }

        private void HandleBlockClicked(long targetPlayerId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendBlockPlayerCommandZeroAlloc(targetPlayerId);
            }

            FriendsCache.MarkBlockedLocally(targetPlayerId);
        }

        private void HandleUnblockClicked(long targetPlayerId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendUnblockPlayerCommandZeroAlloc(targetPlayerId);
            }

            FriendsCache.RemoveEntryLocally(targetPlayerId);
        }

        private void SetStatus(string message)
        {
            if (StatusText != null)
            {
                StatusText.text = message;
            }
        }
    }
}

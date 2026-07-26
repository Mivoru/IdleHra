using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // One chat channel's window. Virtualized: exactly RowCount
    // UiChatMessageRow GameObjects are instantiated once (as soon as
    // RowPrefabAddressableKey finishes loading through AssetManager) and
    // never again - message history (up to HistoryCapacity entries) lives in
    // a pre-allocated circular buffer of plain data, and scrolling only ever
    // changes WHICH history entries the fixed rows are bound to display
    // (Bind(), not Instantiate). See CreateRows for the one and only place
    // Object.Instantiate appears in this file. The row prefab itself is an
    // Addressable rather than a baked scene/prefab reference so it can ship
    // and update over-the-air independently of the client build.
    //
    // Modul: UI rework. Three behavioural changes from the original
    // single-global-window design:
    //
    // 1. It no longer drains WebSocketClient.ChatMessageQueue itself.
    //    That queue is a ConcurrentQueue, so with more than one chat window
    //    alive each would consume a random share of the messages. ChatRelay
    //    (on the Managers root) drains it exactly once and fans out; see its
    //    own comment.
    // 2. It subscribes in Awake/OnDestroy rather than OnEnable/OnDisable, so
    //    a guild message still lands in the guild log while the player is
    //    looking at some other screen, the way any real chat client behaves.
    // 3. Channel selects which messages this instance accepts, so World chat
    //    (map hub), Guild chat (Guild screen) and Whispers (Friends screen)
    //    are three genuinely separate logs rather than one mixed firehose.
    public class UiChatWindow : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        [Header("Channel")]
        // 0 = Global/World, 1 = Guild, 2 = Whisper. Mirrors
        // ChatChannelType / the server's ChatEngine channel constants.
        public byte Channel;

        // Whisper channel only: which conversation is currently on screen.
        // 0 means "no friend selected yet", in which case the log is empty
        // and sending is disabled - see RefreshComposeAvailability.
        public long WhisperTargetPlayerId;

        [Header("Optional chrome")]
        public TMP_Text HeaderLabel;
        public TMP_Text EmptyStateText;

        [Header("Virtualization")]
        public ScrollRect ChatScrollRect;
        public RectTransform RowContainer;
        public string RowPrefabAddressableKey = "UiChatMessageRow";
        public int RowCount = 15;
        public int HistoryCapacity = 200;
        public float RowHeight = 30f;

        [Header("Compose")]
        public TMP_InputField MessageInputField;
        public Button SendButton;

        private struct ChatHistoryEntry
        {
            public long SenderPlayerId;
            public long ConversationPartnerId;
            public long TimestampEpochMs;
            public string MessageText;
        }

        // Fixed-size circular buffer - message data only, never GameObjects.
        // Index into it via (globalIndex % HistoryCapacity); once
        // _totalMessagesAccepted exceeds HistoryCapacity, the oldest entries
        // are overwritten in place.
        private ChatHistoryEntry[] _history;
        private long _totalMessagesAccepted;

        // Which history entries are currently on display, oldest first.
        // For World/Guild this is simply every entry the buffer still holds;
        // for Whisper it is only the selected friend's thread, which is what
        // lets one window host every conversation and switch between them
        // without re-fetching anything. Rebuilt wholesale only when the
        // whisper target changes, appended to on every accepted message.
        private readonly List<long> _visibleGlobalIndices = new List<long>(256);

        // The RowCount row objects themselves, created once and reused for
        // the lifetime of this window - never resized, never destroyed.
        private UiChatMessageRow[] _rows;
        private RectTransform[] _rowRectTransforms;

        // Which position in _visibleGlobalIndices each fixed row slot
        // currently displays, so redundant rebinds can be skipped. -1 means
        // the slot currently shows nothing.
        private int[] _rowBoundVisibleIndex;

        // True while the view is following live chat (newest messages always
        // visible, matching standard chat UX - false once the user scrolls
        // up to read history, true again once they scroll back to the
        // bottom).
        private bool _pinnedToBottom = true;

        private bool _suppressScrollCallback;

        private void Awake()
        {
            _history = new ChatHistoryEntry[HistoryCapacity];

            _rows = new UiChatMessageRow[RowCount];
            _rowRectTransforms = new RectTransform[RowCount];
            _rowBoundVisibleIndex = new int[RowCount];
            for (int i = 0; i < RowCount; i++)
            {
                _rowBoundVisibleIndex[i] = -1;
            }

            if (MessageInputField != null)
            {
                MessageInputField.characterLimit = RequestChatMessagePacket.MessageCapacity;
                MessageInputField.onSubmit.AddListener(HandleSubmit);
            }

            if (SendButton != null)
            {
                SendButton.onClick.AddListener(HandleSendButtonClicked);
            }

            if (ChatScrollRect != null)
            {
                ChatScrollRect.onValueChanged.AddListener(HandleScrollValueChanged);
            }

            // Awake, not OnEnable - see this class's own header comment.
            ChatRelay.OnChatMessageReceived += HandleRelayMessage;
            PlayerNameCache.OnPlayerNamesUpdated += HandlePlayerNamesResolved;

            if (AssetManager.Instance != null)
            {
                AssetManager.Instance.LoadAsync<GameObject>(RowPrefabAddressableKey, HandleRowPrefabLoaded);
            }

            RefreshComposeAvailability();
        }

        private void OnDestroy()
        {
            ChatRelay.OnChatMessageReceived -= HandleRelayMessage;
            PlayerNameCache.OnPlayerNamesUpdated -= HandlePlayerNamesResolved;

            if (AssetManager.Instance != null)
            {
                AssetManager.Instance.Release(RowPrefabAddressableKey);
            }
        }

        // Modul: whisper thread switching. Called by the Friends screen when
        // the player picks a different friend to talk to. The underlying
        // history is untouched - only which slice of it is visible changes,
        // so switching back to an earlier conversation still shows it.
        public void SetWhisperTarget(long targetPlayerId, string displayName)
        {
            WhisperTargetPlayerId = targetPlayerId;

            if (!string.IsNullOrEmpty(displayName))
            {
                PlayerNameCache.Seed(targetPlayerId, displayName);
            }

            RebuildVisibleIndices();
            RefreshComposeAvailability();

            _pinnedToBottom = true;
            UpdateContentHeight();
            SnapScrollToBottom();
            RebindVisibleRows();
        }

        // Fires exactly once, when RowPrefabAddressableKey finishes loading
        // through AssetManager.
        private void HandleRowPrefabLoaded(GameObject prefabAsset)
        {
            UiChatMessageRow rowPrefab = prefabAsset != null ? prefabAsset.GetComponent<UiChatMessageRow>() : null;
            Transform parent = RowContainer != null ? RowContainer : (ChatScrollRect != null ? ChatScrollRect.content : null);

            for (int i = 0; i < RowCount; i++)
            {
                // The only Instantiate call in this file - fires exactly
                // RowCount times, once, here, regardless of how many chat
                // messages this window ever displays over its lifetime.
                UiChatMessageRow row = rowPrefab != null && parent != null
                    ? Instantiate(rowPrefab, parent)
                    : null;

                _rows[i] = row;

                if (row != null)
                {
                    RectTransform rect = row.GetComponent<RectTransform>();
                    _rowRectTransforms[i] = rect;
                    if (rect != null)
                    {
                        // Rows never move once placed - row slot i is always
                        // physically at -(i * RowHeight) from the top of the
                        // content area. Only the bound content changes.
                        rect.anchorMin = new Vector2(0f, 1f);
                        rect.anchorMax = new Vector2(1f, 1f);
                        rect.pivot = new Vector2(0.5f, 1f);
                        rect.anchoredPosition = new Vector2(0f, -i * RowHeight);
                        rect.sizeDelta = new Vector2(rect.sizeDelta.x, RowHeight);
                    }
                    row.Clear();

                    // Modul: Full-Stack Social Layer, Part 5. Subscribed
                    // once per fixed row slot (not per bind) - the row
                    // itself reports whichever SenderPlayerId is currently
                    // bound to it at click time (see
                    // UiChatMessageRow.HandleNameClicked).
                    row.OnNameClicked += HandlePlayerNameClicked;
                }
            }

            if (RowContainer != null)
            {
                RowContainer.sizeDelta = new Vector2(RowContainer.sizeDelta.x, RowCount * RowHeight);
            }

            // Messages may already have arrived before the prefab finished
            // loading (ChatRelay keeps delivering regardless) - rebind
            // immediately so they are not invisible until the next one.
            UpdateContentHeight();
            SnapScrollToBottom();
            RebindVisibleRows();
        }

        // ------------------------------------------------------------
        // Inbound
        // ------------------------------------------------------------
        private void HandleRelayMessage(byte channelType, long senderPlayerId, long conversationPartnerId, long timestampEpochMs, string messageText)
        {
            if (channelType != Channel) return;

            long globalIndex = _totalMessagesAccepted;
            _history[globalIndex % HistoryCapacity] = new ChatHistoryEntry
            {
                SenderPlayerId = senderPlayerId,
                ConversationPartnerId = conversationPartnerId,
                TimestampEpochMs = timestampEpochMs,
                MessageText = messageText
            };
            _totalMessagesAccepted++;

            // An overwritten slot may still be referenced by
            // _visibleGlobalIndices - drop anything that has aged out of the
            // circular buffer before appending the new entry.
            TrimVisibleIndicesToBuffer();

            if (IsVisibleInCurrentThread(senderPlayerId, conversationPartnerId))
            {
                _visibleGlobalIndices.Add(globalIndex);

                // Warm the name cache so the row can render a username
                // rather than "Player #1042"; HandlePlayerNamesResolved
                // re-binds once the lookup lands.
                PlayerNameCache.GetOrRequest(senderPlayerId);

                if (_pinnedToBottom)
                {
                    UpdateContentHeight();
                    SnapScrollToBottom();
                }

                RebindVisibleRows();
                RefreshEmptyState();
            }
        }

        private void HandlePlayerNamesResolved()
        {
            // Names arriving is a pure re-render: force every bound slot to
            // rebind by invalidating the "already showing this" guard.
            for (int i = 0; i < RowCount; i++)
            {
                _rowBoundVisibleIndex[i] = -1;
            }
            RebindVisibleRows();
        }

        private bool IsVisibleInCurrentThread(long senderPlayerId, long conversationPartnerId)
        {
            if (Channel != (byte)ChatChannelType.Whisper) return true;
            if (WhisperTargetPlayerId <= 0) return false;

            // Both directions of the selected thread: what the friend sent
            // us (partner == them) and what we sent them (locally echoed
            // with partner == them, see ChatRelay.PublishLocalEcho).
            return conversationPartnerId == WhisperTargetPlayerId;
        }

        private void RebuildVisibleIndices()
        {
            _visibleGlobalIndices.Clear();

            long oldest = _totalMessagesAccepted - AvailableBufferedCount();
            for (long globalIndex = oldest; globalIndex < _totalMessagesAccepted; globalIndex++)
            {
                ChatHistoryEntry entry = _history[globalIndex % HistoryCapacity];
                if (IsVisibleInCurrentThread(entry.SenderPlayerId, entry.ConversationPartnerId))
                {
                    _visibleGlobalIndices.Add(globalIndex);
                }
            }

            RefreshEmptyState();
        }

        private void TrimVisibleIndicesToBuffer()
        {
            long oldest = _totalMessagesAccepted - AvailableBufferedCount();

            int dropCount = 0;
            while (dropCount < _visibleGlobalIndices.Count && _visibleGlobalIndices[dropCount] < oldest)
            {
                dropCount++;
            }

            if (dropCount > 0)
            {
                _visibleGlobalIndices.RemoveRange(0, dropCount);
            }
        }

        // Total number of history entries still retrievable from the
        // circular buffer (bounded by HistoryCapacity even if more messages
        // than that have ever arrived).
        private long AvailableBufferedCount()
        {
            return _totalMessagesAccepted < HistoryCapacity ? _totalMessagesAccepted : HistoryCapacity;
        }

        // ------------------------------------------------------------
        // Rendering
        // ------------------------------------------------------------
        private void UpdateContentHeight()
        {
            if (RowContainer == null) return;

            float rows = Mathf.Max(RowCount, _visibleGlobalIndices.Count);
            RowContainer.sizeDelta = new Vector2(RowContainer.sizeDelta.x, rows * RowHeight);
        }

        private void SnapScrollToBottom()
        {
            if (ChatScrollRect == null) return;

            _suppressScrollCallback = true;
            ChatScrollRect.verticalNormalizedPosition = 0f;
            _suppressScrollCallback = false;
        }

        private void HandleScrollValueChanged(Vector2 normalizedPosition)
        {
            if (_suppressScrollCallback || ChatScrollRect == null) return;

            const float bottomEpsilon = 0.01f;
            _pinnedToBottom = ChatScrollRect.verticalNormalizedPosition <= bottomEpsilon;

            RebindVisibleRows();
        }

        // Maps the current scroll position to a window of visible entries
        // and rebinds only the fixed row slots whose mapped entry actually
        // changed - no Instantiate, just UiChatMessageRow.Bind/Clear calls
        // on the same RowCount objects created once in HandleRowPrefabLoaded.
        private void RebindVisibleRows()
        {
            int available = _visibleGlobalIndices.Count;
            if (available <= 0 || ChatScrollRect == null || _rows == null)
            {
                for (int i = 0; i < RowCount; i++)
                {
                    ClearRowIfBound(i);
                }
                return;
            }

            float contentHeight = RowContainer != null ? RowContainer.sizeDelta.y : available * RowHeight;
            float viewportHeight = ChatScrollRect.viewport != null ? ChatScrollRect.viewport.rect.height : RowCount * RowHeight;
            float scrollableHeight = Mathf.Max(0f, contentHeight - viewportHeight);

            float normalizedTop = ChatScrollRect.verticalNormalizedPosition;
            float pixelOffsetFromTop = (1f - normalizedTop) * scrollableHeight;
            int topVisibleIndex = Mathf.FloorToInt(pixelOffsetFromTop / RowHeight);

            for (int slot = 0; slot < RowCount; slot++)
            {
                int visibleIndex = topVisibleIndex + slot;

                if (visibleIndex < 0 || visibleIndex >= available)
                {
                    ClearRowIfBound(slot);
                    continue;
                }

                if (_rowBoundVisibleIndex[slot] == visibleIndex)
                {
                    continue;
                }

                ChatHistoryEntry entry = _history[_visibleGlobalIndices[visibleIndex] % HistoryCapacity];
                bool isOwnMessage = NetworkClient != null && entry.SenderPlayerId == NetworkClient.LocalPlayerId;
                string senderName = PlayerNameCache.GetOrRequest(entry.SenderPlayerId);

                _rows[slot]?.Bind(entry.SenderPlayerId, senderName, isOwnMessage, entry.MessageText);
                _rowBoundVisibleIndex[slot] = visibleIndex;
            }
        }

        private void ClearRowIfBound(int slot)
        {
            if (_rowBoundVisibleIndex[slot] == -1) return;

            _rows[slot]?.Clear();
            _rowBoundVisibleIndex[slot] = -1;
        }

        private void RefreshEmptyState()
        {
            if (EmptyStateText == null) return;

            if (_visibleGlobalIndices.Count > 0)
            {
                EmptyStateText.gameObject.SetActive(false);
                return;
            }

            EmptyStateText.gameObject.SetActive(true);
            EmptyStateText.text = Channel switch
            {
                (byte)ChatChannelType.Guild => "No guild messages yet. Say hello to your guild.",
                (byte)ChatChannelType.Whisper => WhisperTargetPlayerId > 0
                    ? "No messages with this friend yet."
                    : "Pick a friend on the left to start a private chat.",
                _ => "No messages yet. Be the first to say something."
            };
        }

        private void RefreshComposeAvailability()
        {
            // Whispers need a recipient; the other two channels never do.
            bool canCompose = Channel != (byte)ChatChannelType.Whisper || WhisperTargetPlayerId > 0;

            if (MessageInputField != null)
            {
                MessageInputField.interactable = canCompose;
            }

            if (SendButton != null)
            {
                SendButton.interactable = canCompose;
            }

            if (HeaderLabel != null)
            {
                HeaderLabel.text = Channel switch
                {
                    (byte)ChatChannelType.Guild => "Guild Chat",
                    (byte)ChatChannelType.Whisper => WhisperTargetPlayerId > 0
                        ? "Private Chat - " + (PlayerNameCache.GetOrRequest(WhisperTargetPlayerId) ?? ("Player #" + WhisperTargetPlayerId))
                        : "Private Chat",
                    _ => "World Chat"
                };
            }

            RefreshEmptyState();
        }

        // ------------------------------------------------------------
        // Outbound
        // ------------------------------------------------------------
        private void HandleSendButtonClicked()
        {
            TrySendCurrentInput();
        }

        private void HandleSubmit(string _)
        {
            TrySendCurrentInput();
        }

        private void TrySendCurrentInput()
        {
            if (NetworkClient == null || MessageInputField == null) return;

            string text = MessageInputField.text;
            if (string.IsNullOrWhiteSpace(text)) return;

            if (Channel == (byte)ChatChannelType.Whisper)
            {
                if (WhisperTargetPlayerId <= 0) return;

                NetworkClient.SendWhisperMessageZeroAlloc(WhisperTargetPlayerId, text);

                // Modul: the server routes a whisper to the recipient only
                // (NetworkBroadcastSystem.HandleChatDispatchAsync's
                // DispatchModeWhisper branch returns after that single
                // send), unlike Global/Guild which fan out to the sender
                // too. Without this echo the player would watch their own
                // message vanish.
                ChatRelay.PublishLocalEcho((byte)ChatChannelType.Whisper, NetworkClient.LocalPlayerId, WhisperTargetPlayerId, text);
            }
            else
            {
                NetworkClient.SendChatMessageZeroAlloc(text, (ChatChannelType)Channel);
            }

            MessageInputField.text = string.Empty;
            MessageInputField.ActivateInputField();
        }

        // Modul: Full-Stack Social Layer, Part 5. Click-to-action protocol.
        // The three actions a click on a player's name in the chat log can
        // trigger - InspectProfile has no server round-trip of its own (it
        // opens a local UI panel; see OnProfileInspectionRequested), while
        // AddFriend/BlockUser map directly onto the existing
        // WebSocketClient.SendAddFriendCommandZeroAlloc/
        // SendBlockPlayerCommandZeroAlloc hooks, which in turn ride the
        // pre-existing TargetPlayerId field on ClientCommandPacket - no new
        // wire field required.
        public enum ChatPlayerContextAction
        {
            InspectProfile,
            AddFriend,
            BlockUser
        }

        // Modul: fired instead of a direct network call for
        // InspectProfile - opening a profile panel is a local UI concern
        // this window does not own; whichever component displays player
        // profiles subscribes here rather than UiChatWindow reaching into
        // it directly.
        public event System.Action<long> OnProfileInspectionRequested;

        // Modul: which row slot most recently reported a name click - the
        // pending target for whichever ChatPlayerContextAction the
        // player's context-menu selection resolves to.
        private long _pendingContextTargetPlayerId;
        public long PendingContextTargetPlayerId => _pendingContextTargetPlayerId;

        private void HandlePlayerNameClicked(long senderPlayerId)
        {
            _pendingContextTargetPlayerId = senderPlayerId;
        }

        // Modul: called by whatever context-menu UI presents the
        // InspectProfile/AddFriend/BlockUser choices after
        // HandlePlayerNameClicked has recorded which player was clicked -
        // this method is the single mapping point from that choice onto
        // the actual network/UI hook.
        public void ExecutePlayerContextAction(long targetPlayerId, ChatPlayerContextAction action)
        {
            if (targetPlayerId == 0) return;

            switch (action)
            {
                case ChatPlayerContextAction.InspectProfile:
                    OnProfileInspectionRequested?.Invoke(targetPlayerId);
                    break;
                case ChatPlayerContextAction.AddFriend:
                    NetworkClient?.SendAddFriendCommandZeroAlloc(targetPlayerId);
                    break;
                case ChatPlayerContextAction.BlockUser:
                    NetworkClient?.SendBlockPlayerCommandZeroAlloc(targetPlayerId);
                    break;
            }
        }
    }
}

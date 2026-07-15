using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Global chat window. Virtualized: exactly RowCount UiChatMessageRow
    // GameObjects are instantiated once in Awake and never again - message
    // history (up to HistoryCapacity entries) lives in a pre-allocated
    // circular buffer of plain data, and scrolling only ever changes WHICH
    // history entries the fixed rows are bound to display (Bind(), not
    // Instantiate). This satisfies the task's explicit "no Instantiate per
    // message" constraint - see Awake for the one and only place
    // Object.Instantiate appears in this file.
    public class UiChatWindow : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        [Header("Virtualization")]
        public ScrollRect ChatScrollRect;
        public RectTransform RowContainer;
        public UiChatMessageRow RowPrefab;
        public int RowCount = 15;
        public int HistoryCapacity = 200;
        public float RowHeight = 30f;

        [Header("Compose")]
        public TMP_InputField MessageInputField;
        public Button SendButton;

        private struct ChatHistoryEntry
        {
            public long SenderPlayerId;
            public long TimestampEpochMs;
            public string MessageText;
        }

        // Fixed-size circular buffer - message data only, never GameObjects.
        // Index into it via (globalIndex % HistoryCapacity); once
        // _totalMessagesReceived exceeds HistoryCapacity, the oldest entries
        // are overwritten in place.
        private ChatHistoryEntry[] _history;
        private long _totalMessagesReceived;

        // The RowCount row objects themselves, created once and reused for
        // the lifetime of this window - never resized, never destroyed.
        private UiChatMessageRow[] _rows;
        private RectTransform[] _rowRectTransforms;

        // Which global history index each fixed row slot currently displays,
        // so redundant rebinds (same index already bound) can be skipped.
        // -1 means the slot currently shows nothing (not enough history yet).
        private long[] _rowBoundGlobalIndex;

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
            _rowBoundGlobalIndex = new long[RowCount];

            Transform parent = RowContainer != null ? RowContainer : (ChatScrollRect != null ? ChatScrollRect.content : null);

            for (int i = 0; i < RowCount; i++)
            {
                // The only Instantiate call in this file - fires exactly
                // RowCount times, once, here, regardless of how many chat
                // messages this window ever displays over its lifetime.
                UiChatMessageRow row = RowPrefab != null && parent != null
                    ? Instantiate(RowPrefab, parent)
                    : null;

                _rows[i] = row;
                _rowBoundGlobalIndex[i] = -1;

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
                }
            }

            if (RowContainer != null)
            {
                RowContainer.sizeDelta = new Vector2(RowContainer.sizeDelta.x, RowCount * RowHeight);
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
        }

        private void Update()
        {
            bool receivedAny = false;

            while (NetworkClient != null && NetworkClient.ChatMessageQueue.TryDequeue(out ResponseChatMessagePacket packet))
            {
                AppendToHistory(packet);
                receivedAny = true;
            }

            if (receivedAny && _pinnedToBottom)
            {
                UpdateContentHeight();
                SnapScrollToBottom();
                RebindVisibleRows();
            }
        }

        private unsafe void AppendToHistory(ResponseChatMessagePacket packet)
        {
            int length = packet.MessageLength;
            if (length < 0 || length > ResponseChatMessagePacket.MessageCapacity)
            {
                length = 0;
            }

            string messageText;
            byte* source = packet.MessageText;
            messageText = System.Text.Encoding.UTF8.GetString(source, length);

            long slot = _totalMessagesReceived % HistoryCapacity;
            _history[slot] = new ChatHistoryEntry
            {
                SenderPlayerId = packet.SenderPlayerId,
                TimestampEpochMs = packet.TimestampEpochMs,
                MessageText = messageText
            };
            _totalMessagesReceived++;
        }

        // Total number of history entries currently retrievable (bounded by
        // HistoryCapacity even if more messages than that have ever arrived,
        // since older ones have been overwritten in the circular buffer).
        private long AvailableHistoryCount()
        {
            return _totalMessagesReceived < HistoryCapacity ? _totalMessagesReceived : HistoryCapacity;
        }

        private void UpdateContentHeight()
        {
            if (RowContainer == null) return;

            long available = AvailableHistoryCount();
            float rows = Mathf.Max(RowCount, available);
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

        // Maps the current scroll position to a window of history indices
        // and rebinds only the fixed row slots whose mapped index actually
        // changed - no Instantiate, just UiChatMessageRow.Bind/Clear calls
        // on the same RowCount objects created in Awake.
        private void RebindVisibleRows()
        {
            long available = AvailableHistoryCount();
            if (available <= 0 || ChatScrollRect == null)
            {
                for (int i = 0; i < RowCount; i++)
                {
                    ClearRowIfBound(i);
                }
                return;
            }

            long oldestGlobalIndex = _totalMessagesReceived - available;
            long newestGlobalIndex = _totalMessagesReceived - 1;

            float contentHeight = RowContainer != null ? RowContainer.sizeDelta.y : available * RowHeight;
            float viewportHeight = ChatScrollRect.viewport != null ? ChatScrollRect.viewport.rect.height : RowCount * RowHeight;
            float scrollableHeight = Mathf.Max(0f, contentHeight - viewportHeight);

            float normalizedTop = ChatScrollRect.verticalNormalizedPosition;
            float pixelOffsetFromTop = (1f - normalizedTop) * scrollableHeight;
            long topVisibleGlobalIndex = oldestGlobalIndex + Mathf.FloorToInt(pixelOffsetFromTop / RowHeight);

            for (int slot = 0; slot < RowCount; slot++)
            {
                long globalIndex = topVisibleGlobalIndex + slot;

                if (globalIndex < oldestGlobalIndex || globalIndex > newestGlobalIndex)
                {
                    ClearRowIfBound(slot);
                    continue;
                }

                if (_rowBoundGlobalIndex[slot] == globalIndex)
                {
                    continue;
                }

                ChatHistoryEntry entry = _history[globalIndex % HistoryCapacity];
                _rows[slot]?.Bind(entry.SenderPlayerId, entry.TimestampEpochMs, entry.MessageText);
                _rowBoundGlobalIndex[slot] = globalIndex;
            }
        }

        private void ClearRowIfBound(int slot)
        {
            if (_rowBoundGlobalIndex[slot] == -1) return;

            _rows[slot]?.Clear();
            _rowBoundGlobalIndex[slot] = -1;
        }

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

            NetworkClient.SendChatMessageZeroAlloc(text);
            MessageInputField.text = string.Empty;
            MessageInputField.ActivateInputField();
        }
    }
}

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: UI audit follow-up. Single pooled row for UiFriendsWindow,
    // mirroring UiMailboxEntryRow's exact shape. Bind() only runs when the
    // owning window rebuilds its list, never from Update().
    public class UiFriendEntryRow : MonoBehaviour
    {
        public TMP_Text NameText;
        public Button RemoveButton;
        public Button BlockButton;
        public Button UnblockButton;

        // Modul: UI rework. Opens this friend's private (whisper) thread in
        // the chat panel alongside the roster. The whisper channel has
        // existed on the wire since the social layer shipped
        // (ChatChannelType.Whisper / SendWhisperMessageZeroAlloc) but there
        // was no way anywhere in the client to pick a recipient, so it was
        // unreachable.
        public Button ChatButton;

        private readonly char[] _nameBuffer = new char[48];
        private long _playerId;
        private string _username = string.Empty;
        private Action<long> _onRemoveClicked;
        private Action<long> _onBlockClicked;
        private Action<long> _onUnblockClicked;
        private Action<long, string> _onChatClicked;

        private void Awake()
        {
            if (RemoveButton != null) RemoveButton.onClick.AddListener(HandleRemoveClicked);
            if (BlockButton != null) BlockButton.onClick.AddListener(HandleBlockClicked);
            if (UnblockButton != null) UnblockButton.onClick.AddListener(HandleUnblockClicked);
            if (ChatButton != null) ChatButton.onClick.AddListener(HandleChatClicked);
        }

        public void Bind(long playerId, string username, int level, bool isBlocked, Action<long> onRemoveClicked, Action<long> onBlockClicked, Action<long> onUnblockClicked, Action<long, string> onChatClicked)
        {
            _playerId = playerId;
            _username = username ?? string.Empty;
            _onRemoveClicked = onRemoveClicked;
            _onBlockClicked = onBlockClicked;
            _onUnblockClicked = onUnblockClicked;
            _onChatClicked = onChatClicked;

            if (NameText != null)
            {
                int offset = WriteTextToBuffer(_nameBuffer, 0, username);
                offset = WriteTextToBuffer(_nameBuffer, offset, " (Lv. ");
                offset = WriteIntToBuffer(_nameBuffer, offset, level);
                offset = WriteTextToBuffer(_nameBuffer, offset, isBlocked ? ") [Blocked]" : ")");
                NameText.SetCharArray(_nameBuffer, 0, offset);
            }

            if (RemoveButton != null) RemoveButton.gameObject.SetActive(!isBlocked);
            if (BlockButton != null) BlockButton.gameObject.SetActive(!isBlocked);
            if (UnblockButton != null) UnblockButton.gameObject.SetActive(isBlocked);

            // Whispering someone you have blocked is pointless - the server
            // drops it at dispatch (see NetworkBroadcastSystem's block
            // filter), so the button is hidden rather than silently failing.
            if (ChatButton != null) ChatButton.gameObject.SetActive(!isBlocked);
        }

        private void HandleChatClicked()
        {
            _onChatClicked?.Invoke(_playerId, _username);
        }

        private void HandleRemoveClicked()
        {
            _onRemoveClicked?.Invoke(_playerId);
        }

        private void HandleBlockClicked()
        {
            _onBlockClicked?.Invoke(_playerId);
        }

        private void HandleUnblockClicked()
        {
            _onUnblockClicked?.Invoke(_playerId);
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

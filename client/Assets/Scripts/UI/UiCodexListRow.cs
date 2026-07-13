using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Single pooled row for UiCodexListBinder. Bind() is only called when the
    // owning binder rebuilds its visible rows, never from an Update() loop.
    public class UiCodexListRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button RowButton;

        private readonly char[] _rowUiBuffer = new char[64];
        private string _assetKey;
        private Action<string> _onSelected;

        private void Awake()
        {
            if (RowButton != null)
            {
                RowButton.onClick.AddListener(HandleClicked);
            }
        }

        public void Bind(int monsterId, string assetKey, int level, Action<string> onSelected)
        {
            _assetKey = assetKey;
            _onSelected = onSelected;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, "Monster ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, monsterId);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  Lv. ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, level);
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }
        }

        private void HandleClicked()
        {
            _onSelected?.Invoke(_assetKey);
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

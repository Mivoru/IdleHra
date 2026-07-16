using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.2. Single pooled
    // row for UiMailboxWindow, mirroring UiMarketListingRow's exact shape.
    // Bind() is only called when the owning window rebuilds its list, never
    // from an Update() loop.
    public class UiMailboxEntryRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button ClaimButton;

        private readonly char[] _rowUiBuffer = new char[96];
        private long _mailId;
        private Action<long> _onClaimClicked;

        private void Awake()
        {
            if (ClaimButton != null)
            {
                ClaimButton.onClick.AddListener(HandleClaimClicked);
            }
        }

        public void Bind(long mailId, string baseItemId, int quantity, long goldAttachment, bool hasEquipmentAttachment, Action<long> onClaimClicked)
        {
            _mailId = mailId;
            _onClaimClicked = onClaimClicked;

            if (RowLabelText != null)
            {
                int offset = 0;
                if (hasEquipmentAttachment || !string.IsNullOrEmpty(baseItemId))
                {
                    offset = WriteTextToBuffer(_rowUiBuffer, offset, baseItemId);
                    offset = WriteTextToBuffer(_rowUiBuffer, offset, " x");
                    offset = WriteIntToBuffer(_rowUiBuffer, offset, quantity);
                }
                if (goldAttachment > 0)
                {
                    if (offset > 0) offset = WriteTextToBuffer(_rowUiBuffer, offset, "  ");
                    offset = WriteTextToBuffer(_rowUiBuffer, offset, "+");
                    offset = WriteLongToBuffer(_rowUiBuffer, offset, goldAttachment);
                    offset = WriteTextToBuffer(_rowUiBuffer, offset, "g");
                }
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }
        }

        private void HandleClaimClicked()
        {
            _onClaimClicked?.Invoke(_mailId);
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
            return (int)WriteLongToBuffer(buffer, offset, value);
        }

        private static int WriteLongToBuffer(char[] buffer, int offset, long value)
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

            long temp = value;
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

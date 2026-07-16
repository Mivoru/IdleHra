using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.2. Single pooled
    // row for UiBankVaultWindow, mirroring UiMarketListingRow's exact shape.
    public class UiBankVaultEntryRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button WithdrawButton;

        private readonly char[] _rowUiBuffer = new char[96];
        private long _bankId;
        private Action<long> _onWithdrawClicked;

        private void Awake()
        {
            if (WithdrawButton != null)
            {
                WithdrawButton.onClick.AddListener(HandleWithdrawClicked);
            }
        }

        public void Bind(long bankId, string baseItemId, int qualityTier, bool isAffixLocked, Action<long> onWithdrawClicked)
        {
            _bankId = bankId;
            _onWithdrawClicked = onWithdrawClicked;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, baseItemId);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  T");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, qualityTier);
                if (isAffixLocked)
                {
                    offset = WriteTextToBuffer(_rowUiBuffer, offset, "  [Locked]");
                }
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }
        }

        private void HandleWithdrawClicked()
        {
            _onWithdrawClicked?.Invoke(_bankId);
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

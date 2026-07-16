using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1. Single
    // pooled row for UiStoreWindow's package grid, mirroring
    // UiMarketListingRow's exact shape.
    public class UiStoreEntryRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button PurchaseButton;

        private readonly char[] _rowUiBuffer = new char[96];
        private string _productId = string.Empty;
        private Action<string> _onPurchaseClicked;

        private void Awake()
        {
            if (PurchaseButton != null)
            {
                PurchaseButton.onClick.AddListener(HandlePurchaseClicked);
            }
        }

        public void Bind(string productId, int diamondAmount, Action<string> onPurchaseClicked)
        {
            _productId = productId;
            _onPurchaseClicked = onPurchaseClicked;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, productId);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, diamondAmount);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, " Diamonds");
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }
        }

        private void HandlePurchaseClicked()
        {
            _onPurchaseClicked?.Invoke(_productId);
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

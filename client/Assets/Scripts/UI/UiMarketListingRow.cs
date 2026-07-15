using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Single pooled row for UiMarketBrowserWindow. Bind() is only called when
    // the owning window rebuilds its visible page, never from an Update() loop.
    public class UiMarketListingRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button BuyButton;

        private readonly char[] _rowUiBuffer = new char[96];
        private long _orderId;
        private Action<long> _onBuyClicked;

        private void Awake()
        {
            if (BuyButton != null)
            {
                BuyButton.onClick.AddListener(HandleBuyClicked);
            }
        }

        public void Bind(long orderId, string baseItemId, int qualityTier, long price, Action<long> onBuyClicked)
        {
            _orderId = orderId;
            _onBuyClicked = onBuyClicked;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, baseItemId);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  T");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, qualityTier);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  ");
                offset = WriteLongToBuffer(_rowUiBuffer, offset, price);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "g");
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }
        }

        private void HandleBuyClicked()
        {
            _onBuyClicked?.Invoke(_orderId);
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

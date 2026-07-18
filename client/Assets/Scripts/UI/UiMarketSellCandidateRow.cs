using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Full-Game UI Architecture, Part 2. Single pooled row for
    // UiMarketSellPanel - mirrors UiBankDepositCandidateRow's shape plus a
    // free-text price field, since CommandType.MarketListItem's price
    // argument is a plain int with no narrower client-side validation
    // available - the server is the sole source of truth on whether a
    // given price is accepted.
    public class UiMarketSellCandidateRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public TMP_InputField PriceInputField;
        public Button SellButton;

        private readonly char[] _rowUiBuffer = new char[96];
        private long _instanceId;
        private Action<long, int> _onSellClicked;

        private void Awake()
        {
            if (SellButton != null)
            {
                SellButton.onClick.AddListener(HandleSellClicked);
            }
        }

        public void Bind(long instanceId, string baseItemId, int qualityTier, bool isAffixLocked, Action<long, int> onSellClicked)
        {
            _instanceId = instanceId;
            _onSellClicked = onSellClicked;

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

            if (PriceInputField != null)
            {
                PriceInputField.text = string.Empty;
            }
        }

        private void HandleSellClicked()
        {
            int price = 0;
            if (PriceInputField != null)
            {
                int.TryParse(PriceInputField.text, out price);
            }

            if (price <= 0) return;

            _onSellClicked?.Invoke(_instanceId, price);
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

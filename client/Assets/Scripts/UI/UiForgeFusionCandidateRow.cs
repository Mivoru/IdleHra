using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Pooled row for UiForgeFusionPanel's candidate list. Mirrors
    // UiBreedingRosterRow's exact click-to-select pattern (whole row is one
    // Select button, no per-row commitment - the owning panel decides which
    // of its 3 slots a click fills based on which "Select X" button was
    // pressed last).
    public class UiForgeFusionCandidateRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button SelectButton;

        private readonly char[] _rowUiBuffer = new char[64];
        private long _instanceId;
        private string _baseItemId = string.Empty;
        private int _qualityTier;
        private Action<long, string, int> _onSelected;

        private void Awake()
        {
            if (SelectButton != null)
            {
                SelectButton.onClick.AddListener(HandleClicked);
            }
        }

        public void Bind(long instanceId, string baseItemId, int qualityTier, Action<long, string, int> onSelected)
        {
            _instanceId = instanceId;
            _baseItemId = baseItemId;
            _qualityTier = qualityTier;
            _onSelected = onSelected;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, baseItemId);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  T");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, qualityTier);
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }
        }

        private void HandleClicked()
        {
            _onSelected?.Invoke(_instanceId, _baseItemId, _qualityTier);
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

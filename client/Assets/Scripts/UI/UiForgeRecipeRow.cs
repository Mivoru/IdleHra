using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Pooled row for UiForgeCraftingPanel. Bind() only runs when the panel
    // rebuilds its visible rows (snapshot refresh or selection change), never
    // from an Update() loop.
    public class UiForgeRecipeRow : MonoBehaviour
    {
        public TextMeshProUGUI RowLabelText;
        public Button RowButton;
        public GameObject SelectedHighlight;

        private readonly char[] _rowUiBuffer = new char[160];
        private int _recipeId;
        private Action<int> _onSelected;

        private void Awake()
        {
            if (RowButton != null)
            {
                RowButton.onClick.AddListener(HandleClicked);
            }
        }

        public void Bind(int recipeId, string resultBaseItemId, int tierIndex, bool isSelected, Action<int> onSelected)
        {
            _recipeId = recipeId;
            _onSelected = onSelected;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, "T");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, tierIndex);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, " - ");
                offset = WriteTextToBuffer(_rowUiBuffer, offset, resultBaseItemId);
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }

            if (SelectedHighlight != null)
            {
                SelectedHighlight.SetActive(isSelected);
            }
        }

        private void HandleClicked()
        {
            _onSelected?.Invoke(_recipeId);
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

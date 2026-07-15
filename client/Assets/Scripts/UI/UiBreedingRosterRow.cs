using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Single pooled row for UiBreedingLabWindow's parent roster list. Bind()
    // is only called when the owning window rebuilds its visible rows, never
    // from an Update() loop.
    public class UiBreedingRosterRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button SelectButton;
        public GameObject CooldownBadge;

        private readonly char[] _rowUiBuffer = new char[64];
        private string _characterId = string.Empty;
        private Action<string> _onSelected;

        private void Awake()
        {
            if (SelectButton != null)
            {
                SelectButton.onClick.AddListener(HandleClicked);
            }
        }

        public void Bind(BreedingRosterEntryData entry, Action<string> onSelected)
        {
            _characterId = entry.CharacterId;
            _onSelected = onSelected;

            bool onCooldown = entry.IsBreedingActive;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, "Lv. ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, entry.Level);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  Gen ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, entry.GenerationIndex);
                if (entry.IsEpicMutation)
                {
                    offset = WriteTextToBuffer(_rowUiBuffer, offset, "  Epic");
                }
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }

            if (CooldownBadge != null)
            {
                CooldownBadge.SetActive(onCooldown);
            }

            if (SelectButton != null)
            {
                SelectButton.interactable = !onCooldown;
            }
        }

        private void HandleClicked()
        {
            _onSelected?.Invoke(_characterId);
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

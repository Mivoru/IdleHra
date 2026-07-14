using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Pooled row for UiEquipmentRerollPanel. Bind() only runs when the panel
    // rebuilds its visible rows (snapshot refresh or selection change), never
    // from an Update() loop.
    public class UiForgeEquipmentRow : MonoBehaviour
    {
        public TextMeshProUGUI RowLabelText;
        public Button RowButton;
        public GameObject SelectedHighlight;
        public GameObject LockedIcon;

        // Modul 16/21: Equip dispatch. EquippedIcon shows instead of EquipButton
        // when this row's item is already in the active weapon/armor slot.
        public Button EquipButton;
        public GameObject EquippedIcon;

        private readonly char[] _rowUiBuffer = new char[160];
        private long _itemId;
        private Action<long> _onSelected;
        private Action<long> _onEquip;

        private void Awake()
        {
            if (RowButton != null)
            {
                RowButton.onClick.AddListener(HandleClicked);
            }

            if (EquipButton != null)
            {
                EquipButton.onClick.AddListener(HandleEquipClicked);
            }
        }

        public void Bind(long itemId, string baseItemId, int qualityTier, bool isAffixLocked, bool isSelected, Action<long> onSelected, bool isEquipped = false, Action<long> onEquip = null)
        {
            _itemId = itemId;
            _onSelected = onSelected;
            _onEquip = onEquip;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, "T");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, qualityTier);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, " - ");
                offset = WriteTextToBuffer(_rowUiBuffer, offset, baseItemId);
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }

            if (SelectedHighlight != null)
            {
                SelectedHighlight.SetActive(isSelected);
            }

            if (LockedIcon != null)
            {
                LockedIcon.SetActive(isAffixLocked);
            }

            if (EquipButton != null)
            {
                EquipButton.gameObject.SetActive(!isEquipped);
                EquipButton.interactable = !isEquipped;
            }

            if (EquippedIcon != null)
            {
                EquippedIcon.SetActive(isEquipped);
            }
        }

        private void HandleClicked()
        {
            _onSelected?.Invoke(_itemId);
        }

        // Disables the button immediately so a double-click cannot dispatch the
        // equip command twice before the next snapshot refresh calls Bind()
        // again (which restores interactable once the server state settles).
        private void HandleEquipClicked()
        {
            if (EquipButton != null)
            {
                EquipButton.interactable = false;
            }

            _onEquip?.Invoke(_itemId);
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

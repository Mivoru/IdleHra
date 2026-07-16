using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.2. Single
    // pooled row for UiBankVaultWindow's "owned equipment" (backpack) list -
    // the deposit-candidate counterpart to UiBankVaultEntryRow's withdraw
    // list. Selecting an item here dispatches DepositToBank directly by its
    // real EquipmentInstances.Id, sourced from EquipmentInventoryCache -
    // the player never types or pastes an id.
    public class UiBankDepositCandidateRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button DepositButton;

        private readonly char[] _rowUiBuffer = new char[96];
        private long _instanceId;
        private Action<long> _onDepositClicked;

        private void Awake()
        {
            if (DepositButton != null)
            {
                DepositButton.onClick.AddListener(HandleDepositClicked);
            }
        }

        public void Bind(long instanceId, string baseItemId, int qualityTier, bool isAffixLocked, Action<long> onDepositClicked)
        {
            _instanceId = instanceId;
            _onDepositClicked = onDepositClicked;

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

        private void HandleDepositClicked()
        {
            _onDepositClicked?.Invoke(_instanceId);
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

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1. Single
    // pooled row for UiSeasonPassWindow's milestone track.
    public class UiSeasonPassMilestoneRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;
        public Button ClaimButton;

        [Header("State Colors")]
        public Color LockedColor = Color.gray;
        public Color ClaimableColor = Color.yellow;
        public Color ClaimedColor = Color.green;

        private readonly char[] _rowUiBuffer = new char[64];
        private uint _milestoneIndex;
        private Action<uint> _onClaimClicked;

        private void Awake()
        {
            if (ClaimButton != null)
            {
                ClaimButton.onClick.AddListener(HandleClaimClicked);
            }
        }

        public void Bind(uint milestoneIndex, int requiredXp, bool isReached, bool isClaimed, Action<uint> onClaimClicked)
        {
            _milestoneIndex = milestoneIndex;
            _onClaimClicked = onClaimClicked;

            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, "Milestone ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, (int)(milestoneIndex + 1));
                offset = WriteTextToBuffer(_rowUiBuffer, offset, "  ");
                offset = WriteIntToBuffer(_rowUiBuffer, offset, requiredXp);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, " XP");
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
                RowLabelText.color = isClaimed ? ClaimedColor : (isReached ? ClaimableColor : LockedColor);
            }

            if (ClaimButton != null)
            {
                ClaimButton.interactable = isReached && !isClaimed;
            }
        }

        private void HandleClaimClicked()
        {
            _onClaimClicked?.Invoke(_milestoneIndex);
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

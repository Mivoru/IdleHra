using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1. Single
    // pooled row for UiGuildRosterPanel.
    public class UiGuildRosterEntryRow : MonoBehaviour
    {
        public const int RoleLeader = 1;

        public TMP_Text RowLabelText;
        public GameObject OnlineIndicator;
        public GameObject OfflineIndicator;

        private readonly char[] _rowUiBuffer = new char[64];

        public void Bind(long playerId, int role, long contributionPoints, bool isOnline)
        {
            if (RowLabelText != null)
            {
                int offset = WriteTextToBuffer(_rowUiBuffer, 0, "Player ");
                offset = WriteLongToBuffer(_rowUiBuffer, offset, playerId);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, role == RoleLeader ? "  [Leader]  " : "  [Member]  ");
                offset = WriteLongToBuffer(_rowUiBuffer, offset, contributionPoints);
                offset = WriteTextToBuffer(_rowUiBuffer, offset, " CP");
                RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
            }

            if (OnlineIndicator != null) OnlineIndicator.SetActive(isOnline);
            if (OfflineIndicator != null) OfflineIndicator.SetActive(!isOnline);
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
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

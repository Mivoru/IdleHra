using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.3. Single pooled
    // row for UiLeaderboardWindow, mirroring UiMarketListingRow's shape.
    public class UiLeaderboardEntryRow : MonoBehaviour
    {
        public TMP_Text RowLabelText;

        private readonly char[] _rowUiBuffer = new char[96];

        public void Bind(int rank, string displayName, int level, long xp)
        {
            if (RowLabelText == null) return;

            int offset = WriteTextToBuffer(_rowUiBuffer, 0, "#");
            offset = WriteIntToBuffer(_rowUiBuffer, offset, rank);
            offset = WriteTextToBuffer(_rowUiBuffer, offset, "  ");
            offset = WriteTextToBuffer(_rowUiBuffer, offset, displayName);
            offset = WriteTextToBuffer(_rowUiBuffer, offset, "  Lv");
            offset = WriteIntToBuffer(_rowUiBuffer, offset, level);
            offset = WriteTextToBuffer(_rowUiBuffer, offset, "  ");
            offset = WriteLongToBuffer(_rowUiBuffer, offset, xp);
            offset = WriteTextToBuffer(_rowUiBuffer, offset, " XP");
            RowLabelText.SetCharArray(_rowUiBuffer, 0, offset);
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
            return (int)WriteLongToBuffer(buffer, offset, value);
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

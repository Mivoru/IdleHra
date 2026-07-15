using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Single fixed chat row, owned and repeatedly rebound by UiChatWindow's
    // virtualization - never instantiated or destroyed outside that window's
    // one-time row setup. Bind() is called every time this row's slot is
    // remapped to a different history entry (new live message arriving, or
    // the user scrolling), so it uses the same zero-allocation char-buffer
    // text-write convention as other frequently-rebound pooled rows in this
    // codebase (see UiMarketListingRow.Bind) rather than string
    // concatenation.
    public class UiChatMessageRow : MonoBehaviour
    {
        public TMP_Text RowText;

        private readonly char[] _rowUiBuffer = new char[200];

        public void Bind(long senderPlayerId, long timestampEpochMs, string messageText)
        {
            if (RowText == null) return;

            int offset = WriteTextToBuffer(_rowUiBuffer, 0, "Player #");
            offset = WriteLongToBuffer(_rowUiBuffer, offset, senderPlayerId);
            offset = WriteTextToBuffer(_rowUiBuffer, offset, ": ");

            int remaining = _rowUiBuffer.Length - offset;
            int messageLength = messageText.Length;
            if (messageLength > remaining)
            {
                messageLength = remaining;
            }

            for (int i = 0; i < messageLength; i++)
            {
                _rowUiBuffer[offset++] = messageText[i];
            }

            RowText.SetCharArray(_rowUiBuffer, 0, offset);
        }

        public void Clear()
        {
            if (RowText == null) return;
            RowText.SetCharArray(_rowUiBuffer, 0, 0);
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

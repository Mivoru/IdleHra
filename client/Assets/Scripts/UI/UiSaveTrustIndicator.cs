using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.3. Save trust
    // indicator - displays "All progress saved" for the first few seconds
    // after a checkpoint commit, then "Saved Xm ago" as that age grows,
    // driven by VisualSyncProxy.VisualTicksSinceLastFlush (mirrors
    // TickStatePayload.TicksSinceLastFlush, which StateCheckpointManager.
    // FlushState resets to 0 on every successful persistence tick - see
    // that field's own doc comment on StateUpdatePacket). Refreshed once
    // per second from Update(), not from the raw per-packet event, so the
    // displayed age keeps counting up smoothly between broadcasts rather
    // than jumping only when a new packet happens to arrive. Never
    // allocates a string per refresh (plain char-buffer writes, matching
    // every other zero-alloc HUD panel in this codebase).
    public class UiSaveTrustIndicator : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;
        public TextMeshProUGUI SaveStatusText;

        // Below this age, the indicator reads as instantaneously current
        // ("All progress saved") rather than showing a specific elapsed
        // time that would otherwise read as "0m ago" for the first minute.
        public int RecentThresholdSeconds = 15;

        private readonly char[] _lineBuffer = new char[32];
        private int _lastDisplayedSeconds = -1;
        private float _lastRefreshRealtime = -1f;

        private void Update()
        {
            if (SyncProxy == null || SaveStatusText == null) return;

            if (Time.unscaledTime - _lastRefreshRealtime < 1.0f && _lastDisplayedSeconds >= 0)
            {
                return;
            }

            _lastRefreshRealtime = Time.unscaledTime;

            int ageSeconds = SyncProxy.VisualTicksSinceLastFlush / 10;
            if (ageSeconds == _lastDisplayedSeconds)
            {
                return;
            }

            _lastDisplayedSeconds = ageSeconds;

            if (ageSeconds <= RecentThresholdSeconds)
            {
                SaveStatusText.text = "All progress saved";
                return;
            }

            int offset = WriteTextToBuffer(_lineBuffer, 0, "Saved ");

            if (ageSeconds < 3600)
            {
                offset = WriteIntToBuffer(_lineBuffer, offset, ageSeconds / 60);
                offset = WriteTextToBuffer(_lineBuffer, offset, "m ago");
            }
            else
            {
                offset = WriteIntToBuffer(_lineBuffer, offset, ageSeconds / 3600);
                offset = WriteTextToBuffer(_lineBuffer, offset, "h ago");
            }

            SaveStatusText.SetCharArray(_lineBuffer, 0, offset);
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

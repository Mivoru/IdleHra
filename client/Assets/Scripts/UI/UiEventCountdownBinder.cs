using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Rendering layer for the LiveOps event countdown. Isolated onto its own Sub-Canvas
    // (EventCountdownSubCanvas) so its per-tick refresh does not force a mesh rebuild of the
    // high-frequency combat damage canvas, or vice versa.
    public class UiEventCountdownBinder : MonoBehaviour
    {
        private const byte EventStateActive = 1;
        private const long SecondsPerDay = 86400L;
        private const long SecondsPerHour = 3600L;
        private const long SecondsPerMinute = 60L;

        public VisualSyncProxy SyncProxy;

        [Header("Event Countdown HUD - Canvas Isolation")]
        public Canvas EventCountdownSubCanvas;
        public RectTransform EventCountdownPanelRect;

        [Header("Event Countdown HUD")]
        public TMP_Text EventCountdownText;

        private readonly char[] _countdownUiBuffer = new char[64];

        private void Awake()
        {
            if (EventCountdownPanelRect != null)
            {
                LayoutGroup layoutGroup = EventCountdownPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }

                EventCountdownPanelRect.anchorMin = new Vector2(0.5f, 1f);
                EventCountdownPanelRect.anchorMax = new Vector2(0.5f, 1f);
                EventCountdownPanelRect.pivot = new Vector2(0.5f, 1f);
                EventCountdownPanelRect.anchoredPosition = new Vector2(0f, -60f);
            }
        }

        private void Update()
        {
            if (SyncProxy == null || EventCountdownText == null) return;

            if (SyncProxy.VisualWorldBossEventState != EventStateActive)
            {
                int offset = WriteTextToBuffer(_countdownUiBuffer, 0, "No Active Event");
                EventCountdownText.SetCharArray(_countdownUiBuffer, 0, offset);
                return;
            }

            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long remaining = SyncProxy.VisualWorldBossEventEndEpoch - nowEpoch;
            if (remaining < 0) remaining = 0;

            long days = remaining / SecondsPerDay;
            remaining -= days * SecondsPerDay;
            long hours = remaining / SecondsPerHour;
            remaining -= hours * SecondsPerHour;
            long minutes = remaining / SecondsPerMinute;
            remaining -= minutes * SecondsPerMinute;
            long seconds = remaining;

            int index = WriteLongToBuffer(_countdownUiBuffer, 0, days);
            index = WriteTextToBuffer(_countdownUiBuffer, index, FastStringCache.GetTimeUnitLabel(FastStringCache.TimeUnitDays));
            _countdownUiBuffer[index++] = ' ';
            index = WriteTwoDigits(_countdownUiBuffer, index, (int)hours);
            index = WriteTextToBuffer(_countdownUiBuffer, index, FastStringCache.GetTimeUnitLabel(FastStringCache.TimeUnitHours));
            _countdownUiBuffer[index++] = ' ';
            index = WriteTwoDigits(_countdownUiBuffer, index, (int)minutes);
            index = WriteTextToBuffer(_countdownUiBuffer, index, FastStringCache.GetTimeUnitLabel(FastStringCache.TimeUnitMinutes));
            _countdownUiBuffer[index++] = ' ';
            index = WriteTwoDigits(_countdownUiBuffer, index, (int)seconds);
            index = WriteTextToBuffer(_countdownUiBuffer, index, FastStringCache.GetTimeUnitLabel(FastStringCache.TimeUnitSeconds));

            EventCountdownText.SetCharArray(_countdownUiBuffer, 0, index);
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteTwoDigits(char[] buffer, int offset, int value)
        {
            if (value < 0) value = 0;
            buffer[offset++] = (char)('0' + ((value / 10) % 10));
            buffer[offset++] = (char)('0' + (value % 10));
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

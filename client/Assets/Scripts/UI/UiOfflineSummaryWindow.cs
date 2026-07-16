using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 1.1. The Offline
    // "Welcome Back" modal - subscribes to VisualSyncProxy.
    // OnOfflineSummaryAvailable (fires exactly once per login that granted
    // a real offline catch-up, see that event's own doc comment) and shows
    // the exact elapsed time, gold earned, XP earned, and material drops
    // OfflineSimulationEngine granted, instead of the player experiencing a
    // silent stat jump. Event-driven only - never polls, never allocates a
    // string per refresh (plain char-buffer writes, matching every other
    // zero-alloc HUD panel in this codebase).
    public class UiOfflineSummaryWindow : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Modal Root")]
        public GameObject WindowRoot;
        public Button DismissButton;
        public TextMeshProUGUI HeaderText;

        [Header("Summary Fields")]
        public TextMeshProUGUI ElapsedTimeText;
        public TextMeshProUGUI GoldEarnedText;
        public TextMeshProUGUI XpEarnedText;
        public TextMeshProUGUI MaterialDropsText;

        private readonly char[] _lineBuffer = new char[64];
        private readonly char[] _titleBuffer = new char[32];

        private void Awake()
        {
            if (DismissButton != null)
            {
                DismissButton.onClick.AddListener(HandleDismissClicked);
            }

            if (WindowRoot != null)
            {
                WindowRoot.SetActive(false);
            }

            if (HeaderText != null)
            {
                byte activeLanguage = SyncProxy == null || SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;
                int offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.HeaderOfflineSummary, _titleBuffer, 0);
                HeaderText.SetCharArray(_titleBuffer, 0, offset);
            }
        }

        private void OnEnable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnOfflineSummaryAvailable += HandleOfflineSummaryAvailable;
        }

        private void OnDisable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnOfflineSummaryAvailable -= HandleOfflineSummaryAvailable;
        }

        private void HandleOfflineSummaryAvailable(long elapsedSeconds, long goldEarned, long xpEarned, int materialDrops)
        {
            if (ElapsedTimeText != null)
            {
                int offset = WriteElapsedTimeToBuffer(_lineBuffer, 0, elapsedSeconds);
                ElapsedTimeText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (GoldEarnedText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "+");
                offset = WriteLongToBuffer(_lineBuffer, offset, goldEarned);
                offset = WriteTextToBuffer(_lineBuffer, offset, " Gold");
                GoldEarnedText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (XpEarnedText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "+");
                offset = WriteLongToBuffer(_lineBuffer, offset, xpEarned);
                offset = WriteTextToBuffer(_lineBuffer, offset, " XP");
                XpEarnedText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (MaterialDropsText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "+");
                offset = WriteIntToBuffer(_lineBuffer, offset, materialDrops);
                offset = WriteTextToBuffer(_lineBuffer, offset, " Materials");
                MaterialDropsText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (WindowRoot != null)
            {
                WindowRoot.SetActive(true);
            }
        }

        private void HandleDismissClicked()
        {
            if (WindowRoot != null)
            {
                WindowRoot.SetActive(false);
            }
        }

        private int WriteElapsedTimeToBuffer(char[] buffer, int offset, long elapsedSeconds)
        {
            long hours = elapsedSeconds / 3600L;
            long minutes = (elapsedSeconds % 3600L) / 60L;

            byte activeLanguage = SyncProxy == null || SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;

            offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.OfflineAwayForPrefix, buffer, offset);
            offset = WriteLongToBuffer(buffer, offset, hours);
            offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.OfflineHoursSuffix, buffer, offset);
            offset = WriteLongToBuffer(buffer, offset, minutes);
            offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.OfflineMinutesSuffix, buffer, offset);
            return offset;
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

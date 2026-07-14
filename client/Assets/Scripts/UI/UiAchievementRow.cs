using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Pooled row for UiAchievementsPanel. Bind() only runs when the panel
    // rebuilds its visible rows (snapshot refresh), never from Update().
    public class UiAchievementRow : MonoBehaviour
    {
        public TextMeshProUGUI AchievementIdText;
        public TextMeshProUGUI TierText;
        public TextMeshProUGUI ProgressText;
        public RectTransform ProgressBarFill;

        private readonly char[] _idBuffer = new char[32];
        private readonly char[] _tierBuffer = new char[16];
        private readonly char[] _progressBuffer = new char[64];

        public void Bind(int achievementId, int completedTier, long currentProgress, long nextTierTarget)
        {
            if (AchievementIdText != null)
            {
                int offset = WriteTextToBuffer(_idBuffer, 0, "Achievement ");
                offset = WriteIntToBuffer(_idBuffer, offset, achievementId);
                AchievementIdText.SetCharArray(_idBuffer, 0, offset);
            }

            if (TierText != null)
            {
                int offset = WriteTextToBuffer(_tierBuffer, 0, "Tier ");
                offset = WriteTextToBuffer(_tierBuffer, offset, ResolveTierNumeral(completedTier));
                TierText.SetCharArray(_tierBuffer, 0, offset);
            }

            if (ProgressText != null)
            {
                int offset = WriteLongToBuffer(_progressBuffer, 0, currentProgress);
                offset = WriteTextToBuffer(_progressBuffer, offset, " / ");
                offset = nextTierTarget > 0L
                    ? WriteLongToBuffer(_progressBuffer, offset, nextTierTarget)
                    : WriteTextToBuffer(_progressBuffer, offset, "MAX");
                ProgressText.SetCharArray(_progressBuffer, 0, offset);
            }

            if (ProgressBarFill != null)
            {
                float ratio = nextTierTarget > 0L ? Mathf.Clamp01((float)currentProgress / nextTierTarget) : 1f;
                Vector2 anchorMax = ProgressBarFill.anchorMax;
                anchorMax.x = ratio;
                ProgressBarFill.anchorMax = anchorMax;
            }
        }

        private static string ResolveTierNumeral(int completedTier)
        {
            switch (completedTier)
            {
                case 1: return "I";
                case 2: return "II";
                case 3: return "III";
                case 4: return "IV";
                default: return "None";
            }
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

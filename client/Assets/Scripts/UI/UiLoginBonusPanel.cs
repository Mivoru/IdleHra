using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    [Serializable]
    public class LoginBonusDayBoxRefs
    {
        public int Day;
        public TextMeshProUGUI RewardText;
        public Image HighlightBackground;
    }

    // Modul: UI audit follow-up. Login Bonus panel - replaces the old static
    // "Login bonus is not implemented yet." placeholder. The reward itself
    // is already granted server-side at login time by
    // DailyLoginRewardEngine.TryGrantLoginRewardAsync; this only displays
    // the streak/schedule state that grant already persists (see
    // LoginBonusCache's own comment) - it never triggers a grant itself.
    // Fixed 7-day layout (not a pooled list), matching UiRaceMasteryPanel's
    // reasoning: the week length never changes at runtime.
    public class UiLoginBonusPanel : MonoBehaviour
    {
        public LoginBonusDayBoxRefs[] DayBoxes = new LoginBonusDayBoxRefs[7];
        public TextMeshProUGUI StreakText;
        public TextMeshProUGUI StatusText;

        private readonly char[] _streakBuffer = new char[24];
        private readonly char[] _rewardBuffer = new char[40];

        private void OnEnable()
        {
            LoginBonusCache.OnLoginBonusStateUpdated += RefreshUI;
            LoginBonusCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            LoginBonusCache.OnLoginBonusStateUpdated -= RefreshUI;
        }

        private void RefreshUI(LoginBonusStateData data)
        {
            if (StreakText != null)
            {
                int offset = WriteTextToBuffer(_streakBuffer, 0, "Day ");
                offset = WriteIntToBuffer(_streakBuffer, offset, data.CurrentStreakDay);
                offset = WriteTextToBuffer(_streakBuffer, offset, " of 7");
                StreakText.SetCharArray(_streakBuffer, 0, offset);
            }

            if (StatusText != null)
            {
                StatusText.text = data.CreditedToday
                    ? "Today's bonus has been credited."
                    : "Log in to claim today's bonus.";
            }

            if (DayBoxes == null) return;

            for (int i = 0; i < DayBoxes.Length; i++)
            {
                LoginBonusDayBoxRefs box = DayBoxes[i];
                if (box == null) continue;

                int scheduleIndex = box.Day - 1;
                long gold = scheduleIndex >= 0 && scheduleIndex < data.WeeklyGoldSchedule.Length
                    ? data.WeeklyGoldSchedule[scheduleIndex]
                    : 0L;

                if (box.RewardText != null)
                {
                    int offset = WriteLongToBuffer(_rewardBuffer, 0, gold);
                    offset = WriteTextToBuffer(_rewardBuffer, offset, "g");
                    if (box.Day == 7 && data.Day7DiamondBonus > 0)
                    {
                        offset = WriteTextToBuffer(_rewardBuffer, offset, " +");
                        offset = WriteIntToBuffer(_rewardBuffer, offset, data.Day7DiamondBonus);
                        offset = WriteTextToBuffer(_rewardBuffer, offset, "g");
                    }
                    box.RewardText.SetCharArray(_rewardBuffer, 0, offset);
                }

                if (box.HighlightBackground != null)
                {
                    bool isCurrentDay = box.Day == data.CurrentStreakDay;
                    box.HighlightBackground.color = isCurrentDay
                        ? new Color(1f, 0.85f, 0.2f, 0.35f)
                        : new Color(1f, 1f, 1f, 0.05f);
                }
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

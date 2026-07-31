using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: UI audit follow-up. Statistics panel - replaces the old static
    // "Statistics are not implemented yet." placeholder. Aggregates fields
    // already persisted for other systems (see PlayerStatisticsCache's own
    // comment) rather than introducing new tracking. Event-driven only -
    // RefreshUI runs from PlayerStatisticsCache.OnStatisticsUpdated, never
    // from an Update() loop.
    public class UiStatisticsPanel : MonoBehaviour
    {
        public TextMeshProUGUI LevelText;
        public TextMeshProUGUI XpText;
        public TextMeshProUGUI GoldText;
        public TextMeshProUGUI DiamondsText;
        public TextMeshProUGUI LoginStreakText;
        public TextMeshProUGUI AchievementsClaimedText;
        public TextMeshProUGUI RegionsCompletedText;
        public TextMeshProUGUI CharacterCountText;
        public TextMeshProUGUI SkillPointsText;
        public TextMeshProUGUI GuildText;

        // Modul: lifetime statistics.
        public TextMeshProUGUI TotalKillsText;
        public TextMeshProUGUI BossesSlainText;
        public TextMeshProUGUI ItemsCraftedText;
        public TextMeshProUGUI DeathsText;
        public TextMeshProUGUI TimePlayedText;

        // Sized for the longest label ("Achievements Claimed: ") plus a
        // full-length long value, and separately reused for the Guild row
        // (label plus up to GuildRecord.Name's 100-char max).
        private readonly char[] _statBuffer = new char[120];

        private void OnEnable()
        {
            PlayerStatisticsCache.OnStatisticsUpdated += RefreshUI;
            PlayerStatisticsCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            PlayerStatisticsCache.OnStatisticsUpdated -= RefreshUI;
        }

        private void RefreshUI(PlayerStatisticsData data)
        {
            WriteStat(LevelText, "Level: ", data.Level);
            WriteStat(XpText, "Experience: ", data.Xp);
            WriteStat(GoldText, "Gold: ", data.Gold);
            WriteStat(DiamondsText, "Diamonds: ", data.PremiumDiamonds);
            WriteStat(LoginStreakText, "Login Streak: ", data.LoginStreakDays);
            WriteStat(AchievementsClaimedText, "Achievements Claimed: ", data.AchievementsClaimedCount);
            WriteStat(RegionsCompletedText, "Regions Completed: ", data.RegionsCompletedCount);
            WriteStat(CharacterCountText, "Characters: ", data.CharacterCount);
            WriteStat(SkillPointsText, "Unspent Skill Points: ", data.AvailableSkillPoints);

            WriteStat(TotalKillsText, "Total Kills: ", data.TotalKills);
            WriteStat(BossesSlainText, "Bosses Slain: ", data.BossesSlain);
            WriteStat(ItemsCraftedText, "Items Crafted: ", data.TotalItemsCrafted);
            WriteStat(DeathsText, "Deaths: ", data.TotalDeaths);
            WriteTimePlayed(TimePlayedText, data.TotalPlayTimeSeconds);

            if (GuildText != null)
            {
                int offset = WriteTextToBuffer(_statBuffer, 0, "Guild: ");
                offset = WriteTextToBuffer(_statBuffer, offset, string.IsNullOrEmpty(data.GuildName) ? "None" : data.GuildName);
                GuildText.SetCharArray(_statBuffer, 0, offset);
            }
        }

        // Modul: lifetime statistics. Seconds are useless to a player past the
        // first hour, so this renders "12h 34m" (or "34m" under an hour).
        // Written through the same shared buffer as every other row - no
        // string.Format, no interpolation, nothing that allocates per refresh.
        private void WriteTimePlayed(TextMeshProUGUI target, long totalSeconds)
        {
            if (target == null) return;

            if (totalSeconds < 0L) totalSeconds = 0L;
            long hours = totalSeconds / 3600L;
            long minutes = (totalSeconds % 3600L) / 60L;

            int offset = WriteTextToBuffer(_statBuffer, 0, "Time Played: ");
            if (hours > 0L)
            {
                offset = WriteLongToBuffer(_statBuffer, offset, hours);
                offset = WriteTextToBuffer(_statBuffer, offset, "h ");
            }
            offset = WriteLongToBuffer(_statBuffer, offset, minutes);
            offset = WriteTextToBuffer(_statBuffer, offset, "m");

            target.SetCharArray(_statBuffer, 0, offset);
        }

        private void WriteStat(TextMeshProUGUI target, string label, long value)
        {
            if (target == null) return;

            int offset = WriteTextToBuffer(_statBuffer, 0, label);
            offset = WriteLongToBuffer(_statBuffer, offset, value);
            target.SetCharArray(_statBuffer, 0, offset);
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

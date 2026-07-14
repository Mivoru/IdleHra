using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    [Serializable]
    public class RaceMasteryRowRefs
    {
        public int RaceId;
        public TextMeshProUGUI LevelText;
        public TextMeshProUGUI ExperienceText;
        public RectTransform ProgressBarFill;
    }

    // Modul 13: Race Mastery panel. Event-driven only - RefreshUI runs from
    // RaceMasteryCache.OnMasteryCacheUpdated, never from an Update() loop.
    //
    // Fixed set of 6 races (Human/Vila/Draugr/Kobold/Vodnik/Moosleute - see
    // RaceIds) rather than a pooled dynamic list, since the race roster is not
    // variable-length data and does not need pooling infrastructure. Race
    // display names/icons are static per row and set directly on the prefab in
    // the Inspector rather than written here, since they never change at runtime.
    public class UiRaceMasteryPanel : MonoBehaviour
    {
        public RaceMasteryRowRefs[] RaceRows = new RaceMasteryRowRefs[6];

        private readonly char[] _levelBuffer = new char[32];
        private readonly char[] _xpBuffer = new char[64];

        private void OnEnable()
        {
            RaceMasteryCache.OnMasteryCacheUpdated += RefreshUI;
            RaceMasteryCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            RaceMasteryCache.OnMasteryCacheUpdated -= RefreshUI;
        }

        private void RefreshUI()
        {
            if (RaceRows == null) return;

            IReadOnlyList<RaceMasteryEntryData> entries = RaceMasteryCache.Entries;

            for (int i = 0; i < RaceRows.Length; i++)
            {
                RaceMasteryRowRefs row = RaceRows[i];
                if (row == null) continue;

                RaceMasteryEntryData entry = FindEntry(entries, row.RaceId);
                int level = entry != null ? entry.Level : 0;
                long experience = entry != null ? entry.Experience : 0L;
                long nextLevelExperience = entry != null ? entry.NextLevelExperience : 0L;

                if (row.LevelText != null)
                {
                    int offset = WriteTextToBuffer(_levelBuffer, 0, "Lv. ");
                    offset = WriteIntToBuffer(_levelBuffer, offset, level);
                    row.LevelText.SetCharArray(_levelBuffer, 0, offset);
                }

                if (row.ExperienceText != null)
                {
                    int offset = WriteLongToBuffer(_xpBuffer, 0, experience);
                    offset = WriteTextToBuffer(_xpBuffer, offset, " / ");
                    offset = WriteLongToBuffer(_xpBuffer, offset, nextLevelExperience);
                    row.ExperienceText.SetCharArray(_xpBuffer, 0, offset);
                }

                if (row.ProgressBarFill != null)
                {
                    float ratio = nextLevelExperience > 0L ? Mathf.Clamp01((float)experience / nextLevelExperience) : 0f;
                    Vector2 anchorMax = row.ProgressBarFill.anchorMax;
                    anchorMax.x = ratio;
                    row.ProgressBarFill.anchorMax = anchorMax;
                }
            }
        }

        private static RaceMasteryEntryData FindEntry(IReadOnlyList<RaceMasteryEntryData> entries, int raceId)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].RaceId == raceId) return entries[i];
            }
            return null;
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

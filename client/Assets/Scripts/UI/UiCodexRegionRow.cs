using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Single pooled row for UiCodexRegionsWindow. Bind() is only called when
    // the owning window rebuilds its rows, never from an Update() loop.
    public class UiCodexRegionRow : MonoBehaviour
    {
        public TMP_Text RegionLabelText;
        public TMP_Text ProgressLabelText;
        public Image ProgressBarFill;
        public GameObject CompletedBadge;
        public TMP_Text BonusFlagText;

        private readonly char[] _labelUiBuffer = new char[64];
        private readonly char[] _progressUiBuffer = new char[32];

        public void Bind(int regionId, int currentKills, int requiredKills, bool isCompleted, int lootLuckBonusPct)
        {
            if (RegionLabelText != null)
            {
                int offset = WriteTextToBuffer(_labelUiBuffer, 0, "Region ");
                offset = WriteIntToBuffer(_labelUiBuffer, offset, regionId);
                RegionLabelText.SetCharArray(_labelUiBuffer, 0, offset);
            }

            if (ProgressLabelText != null)
            {
                int offset = WriteIntToBuffer(_progressUiBuffer, 0, currentKills);
                offset = WriteTextToBuffer(_progressUiBuffer, offset, " / ");
                offset = WriteIntToBuffer(_progressUiBuffer, offset, requiredKills);
                ProgressLabelText.SetCharArray(_progressUiBuffer, 0, offset);
            }

            if (ProgressBarFill != null)
            {
                float fraction = requiredKills > 0 ? (float)currentKills / requiredKills : 0f;
                ProgressBarFill.fillAmount = Mathf.Clamp01(fraction);
            }

            if (CompletedBadge != null)
            {
                CompletedBadge.SetActive(isCompleted);
            }

            if (BonusFlagText != null)
            {
                if (isCompleted && lootLuckBonusPct > 0)
                {
                    BonusFlagText.gameObject.SetActive(true);
                    BonusFlagText.text = "+" + lootLuckBonusPct.ToString(System.Globalization.CultureInfo.InvariantCulture) + "% Loot Luck (permanent)";
                }
                else
                {
                    BonusFlagText.gameObject.SetActive(false);
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

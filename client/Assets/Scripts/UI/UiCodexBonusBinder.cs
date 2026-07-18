using UnityEngine;
using TMPro;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Rendering layer for passive Codex/Mastery efficiency bonuses. Isolated onto its own
    // Sub-Canvas (CodexSubCanvas) so its per-tick refresh does not force a mesh rebuild of the
    // high-frequency combat damage canvas, or vice versa.
    public class UiCodexBonusBinder : MonoBehaviour
    {
        private const int MaxTrackedMasteryLevel = 200;
        private const int PercentPerLevel = 1;

        // Precomputed once at load: bonusPercentByLevel[level] = passive efficiency bonus percent
        // granted at that mastery level. O(1) array index lookup at read time, no per-frame math.
        private static readonly int[] _bonusPercentByLevel = BuildBonusTable();

        public VisualSyncProxy SyncProxy;

        [Header("Codex HUD - Canvas Isolation")]
        public Canvas CodexSubCanvas;
        public RectTransform CodexPanelRect;

        [Header("Codex HUD")]
        public TMP_Text HumanBonusText;
        public TMP_Text VilaBonusText;
        public TMP_Text DraugrBonusText;

        private readonly char[] _codexUiBuffer = new char[32];

        private static int[] BuildBonusTable()
        {
            var table = new int[MaxTrackedMasteryLevel + 1];
            for (int level = 0; level <= MaxTrackedMasteryLevel; level++)
            {
                table[level] = level * PercentPerLevel;
            }
            return table;
        }

        private static int GetBonusPercent(int masteryLevel)
        {
            if (masteryLevel < 0) masteryLevel = 0;
            else if (masteryLevel > MaxTrackedMasteryLevel) masteryLevel = MaxTrackedMasteryLevel;
            return _bonusPercentByLevel[masteryLevel];
        }

        private void Awake()
        {
            if (CodexPanelRect != null)
            {
                CodexPanelRect.anchorMin = new Vector2(1f, 1f);
                CodexPanelRect.anchorMax = new Vector2(1f, 1f);
                CodexPanelRect.pivot = new Vector2(1f, 1f);
                CodexPanelRect.anchoredPosition = new Vector2(-20f, -20f);
            }
        }

        private void Update()
        {
            if (SyncProxy == null) return;

            WriteBonusText(HumanBonusText, SyncProxy.VisualHumanMasteryLevel);
            WriteBonusText(VilaBonusText, SyncProxy.VisualVilaMasteryLevel);
            WriteBonusText(DraugrBonusText, SyncProxy.VisualDraugrMasteryLevel);
        }

        private void WriteBonusText(TMP_Text target, int masteryLevel)
        {
            if (target == null) return;

            int bonusPercent = GetBonusPercent(masteryLevel);

            int offset = WriteTextToBuffer(_codexUiBuffer, 0, "+");
            offset = WriteIntToBuffer(_codexUiBuffer, offset, bonusPercent);
            offset = WriteTextToBuffer(_codexUiBuffer, offset, "%");

            target.SetCharArray(_codexUiBuffer, 0, offset);
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

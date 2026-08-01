using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: gathering mastery display.
    //
    // Woodcutting and mining mastery were earned by three separate server paths
    // and CONSUMED for gathering yield, but had no database column and no UI, so
    // the player could neither keep them across a logout nor see they existed.
    // The persistence half is fixed server-side; this is the visible half.
    //
    // Event-driven, no Update() loop: mastery changes only when a gather cycle
    // completes, which is far slower than a frame. The XP-to-next-level curve
    // mirrors SimulationEngine's own 50 * (level + 1)^2 for display purposes -
    // the same "client mirrors server formula for preview" pattern
    // UiCharacterStatsPanel documents. The server remains authoritative.
    public class UiGatheringMasteryPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Woodcutting")]
        public TextMeshProUGUI WoodcuttingLevelText;
        public UnityEngine.UI.Image WoodcuttingProgressFill;

        [Header("Mining")]
        public TextMeshProUGUI MiningLevelText;
        public UnityEngine.UI.Image MiningProgressFill;

        private readonly char[] _woodBuffer = new char[40];
        private readonly char[] _miningBuffer = new char[40];

        private int _lastWoodLevel = -1;
        private int _lastMiningLevel = -1;
        private int _lastWoodXp = -1;
        private int _lastMiningXp = -1;

        private void OnEnable()
        {
            RefreshDisplay();
        }

        private void Update()
        {
            if (SyncProxy == null) return;

            int woodLevel = SyncProxy.VisualWoodcuttingMasteryLevel;
            int miningLevel = SyncProxy.VisualMiningMasteryLevel;
            int woodXp = (int)SyncProxy.VisualWoodcuttingXp;
            int miningXp = (int)SyncProxy.VisualMiningXp;

            // Change-gated: the buffers are only rewritten when a value actually
            // moved, so a static panel costs one comparison per frame.
            if (woodLevel == _lastWoodLevel && miningLevel == _lastMiningLevel
                && woodXp == _lastWoodXp && miningXp == _lastMiningXp)
            {
                return;
            }

            _lastWoodLevel = woodLevel;
            _lastMiningLevel = miningLevel;
            _lastWoodXp = woodXp;
            _lastMiningXp = miningXp;

            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (SyncProxy == null) return;

            WriteProfession(
                WoodcuttingLevelText, WoodcuttingProgressFill, _woodBuffer,
                "Woodcutting Lv ",
                SyncProxy.VisualWoodcuttingMasteryLevel,
                (int)SyncProxy.VisualWoodcuttingXp);

            WriteProfession(
                MiningLevelText, MiningProgressFill, _miningBuffer,
                "Mining Lv ",
                SyncProxy.VisualMiningMasteryLevel,
                (int)SyncProxy.VisualMiningXp);
        }

        private static void WriteProfession(
            TextMeshProUGUI label,
            UnityEngine.UI.Image fill,
            char[] buffer,
            string prefix,
            int level,
            int currentXp)
        {
            int requiredXp = GetRequiredMasteryXp(level);

            if (label != null)
            {
                int offset = WriteTextToBuffer(buffer, 0, prefix);
                offset = WriteIntToBuffer(buffer, offset, level);
                offset = WriteTextToBuffer(buffer, offset, "  (");
                offset = WriteIntToBuffer(buffer, offset, currentXp);
                offset = WriteTextToBuffer(buffer, offset, "/");
                offset = WriteIntToBuffer(buffer, offset, requiredXp);
                offset = WriteTextToBuffer(buffer, offset, ")");
                label.SetCharArray(buffer, 0, offset);
            }

            if (fill != null)
            {
                fill.fillAmount = requiredXp > 0
                    ? Mathf.Clamp01(currentXp / (float)requiredXp)
                    : 0f;
            }
        }

        // Mirrors SimulationEngine: 50 * (level + 1) * (level + 1).
        private static int GetRequiredMasteryXp(int level)
        {
            if (level < 0) level = 0;
            long required = 50L * (level + 1L) * (level + 1L);
            return required > int.MaxValue ? int.MaxValue : (int)required;
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length && offset < buffer.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteIntToBuffer(char[] buffer, int offset, int value)
        {
            if (value == 0)
            {
                if (offset < buffer.Length) buffer[offset++] = '0';
                return offset;
            }

            if (value < 0)
            {
                if (offset < buffer.Length) buffer[offset++] = '-';
                value = -value;
            }

            int temp = value;
            int length = 0;
            while (temp > 0)
            {
                temp /= 10;
                length++;
            }

            if (offset + length > buffer.Length) return offset;

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

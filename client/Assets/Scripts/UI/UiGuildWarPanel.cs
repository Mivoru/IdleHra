using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish, Part 3.3. Guild War
    // status panel - matchmaking status (active war id), the active
    // target (the live GuildCombatSimulationEngine turn-based match, see
    // VisualCombatSimulationMatchId), and both sides' contribution
    // metrics. Every field this panel reads (VisualActiveGuildWarId,
    // VisualWarMultiplier, VisualGuildCombatPoints/LogisticsPoints/
    // SupplyPoints and their Enemy* counterparts,
    // VisualCombatSimulationMatchId/TurnCounter/DamageDelta) was already
    // flowing from the server through StateUpdatePacket into
    // VisualSyncProxy before this panel existed - GuildWarEngine/
    // GuildCombatSimulationEngine needed no server-side change, only a
    // client binding. Event-driven only, mirroring UiGuildRaidPanel - HUD
    // text redraws strictly from VisualSyncProxy.OnGuildStateUpdated,
    // never from Update(), and never allocates a string per refresh (plain
    // char-buffer writes, matching the same convention every other
    // zero-alloc HUD panel in this codebase uses).
    public class UiGuildWarPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Matchmaking Status")]
        public TextMeshProUGUI WarStatusText;
        public GameObject NoActiveWarRoot;
        public GameObject ActiveWarRoot;

        [Header("Active Target")]
        public TextMeshProUGUI ActiveMatchText;
        public TextMeshProUGUI TurnCounterText;
        public TextMeshProUGUI LastDamageDeltaText;

        [Header("Contribution Metrics - Own Guild")]
        public TextMeshProUGUI CombatVanguardPointsText;
        public TextMeshProUGUI ProductionLogisticsPointsText;
        public TextMeshProUGUI GatheringSupplyChainPointsText;

        [Header("Contribution Metrics - Enemy Guild")]
        public TextMeshProUGUI EnemyCombatVanguardPointsText;
        public TextMeshProUGUI EnemyProductionLogisticsPointsText;
        public TextMeshProUGUI EnemyGatheringSupplyChainPointsText;

        [Header("War Multiplier")]
        public TextMeshProUGUI WarMultiplierText;

        private readonly char[] _lineBuffer = new char[64];

        private void OnEnable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnGuildStateUpdated += RefreshUI;
            RefreshUI();
        }

        private void OnDisable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnGuildStateUpdated -= RefreshUI;
        }

        private void RefreshUI()
        {
            if (SyncProxy == null) return;

            bool warActive = SyncProxy.VisualActiveGuildWarId > 0L;

            if (NoActiveWarRoot != null) NoActiveWarRoot.SetActive(!warActive);
            if (ActiveWarRoot != null) ActiveWarRoot.SetActive(warActive);

            if (WarStatusText != null)
            {
                WarStatusText.text = warActive ? "War Active" : "No Active War";
            }

            if (!warActive)
            {
                return;
            }

            if (ActiveMatchText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "Match ");
                offset = WriteLongToBuffer(_lineBuffer, offset, SyncProxy.VisualCombatSimulationMatchId);
                ActiveMatchText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (TurnCounterText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "Turn ");
                offset = WriteIntToBuffer(_lineBuffer, offset, SyncProxy.VisualCombatSimulationTurnCounter);
                TurnCounterText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (LastDamageDeltaText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "Last Damage ");
                offset = WriteIntToBuffer(_lineBuffer, offset, SyncProxy.VisualCombatSimulationDamageDelta);
                LastDamageDeltaText.SetCharArray(_lineBuffer, 0, offset);
            }

            SetPointsText(CombatVanguardPointsText, SyncProxy.VisualGuildCombatPoints);
            SetPointsText(ProductionLogisticsPointsText, SyncProxy.VisualGuildLogisticsPoints);
            SetPointsText(GatheringSupplyChainPointsText, SyncProxy.VisualGuildSupplyPoints);

            SetPointsText(EnemyCombatVanguardPointsText, SyncProxy.VisualEnemyCombatPoints);
            SetPointsText(EnemyProductionLogisticsPointsText, SyncProxy.VisualEnemyLogisticsPoints);
            SetPointsText(EnemyGatheringSupplyChainPointsText, SyncProxy.VisualEnemySupplyPoints);

            if (WarMultiplierText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "x");
                offset = WriteIntToBuffer(_lineBuffer, offset, Mathf.RoundToInt(SyncProxy.VisualWarMultiplier * 100f));
                WarMultiplierText.SetCharArray(_lineBuffer, 0, offset);
            }
        }

        private void SetPointsText(TextMeshProUGUI target, int points)
        {
            if (target == null) return;

            int offset = WriteIntToBuffer(_lineBuffer, 0, points);
            target.SetCharArray(_lineBuffer, 0, offset);
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

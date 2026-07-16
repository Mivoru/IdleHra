using System;
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
    // client binding. HUD text redraws strictly from
    // VisualSyncProxy.OnGuildStateUpdated (Part 3 adds a second, narrowly
    // scoped Update() for the real-time Sunday matchmaking countdown,
    // which has no server push to hook), and never allocates a string per
    // refresh (plain char-buffer writes, matching the same convention
    // every other zero-alloc HUD panel in this codebase uses).
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

        [Header("Matchmaking Countdown")]
        public TextMeshProUGUI MatchmakingCountdownText;

        private readonly char[] _lineBuffer = new char[64];
        private readonly char[] _countdownBuffer = new char[32];
        private float _countdownRefreshAccumulatorSeconds;
        private const float CountdownRefreshIntervalSeconds = 1f;

        private void OnEnable()
        {
            _countdownRefreshAccumulatorSeconds = CountdownRefreshIntervalSeconds;

            if (SyncProxy == null) return;

            SyncProxy.OnGuildStateUpdated += RefreshUI;
            RefreshUI();
        }

        private void OnDisable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnGuildStateUpdated -= RefreshUI;
        }

        // Modul: Part 3, Guild War Sunday matchmaking countdown. This is
        // the one place on this panel that legitimately needs Update() -
        // unlike the rest of the panel (strictly event-driven off
        // OnGuildStateUpdated), the countdown to the next matchmaking
        // window ticks down in real time independent of any server push.
        // Throttled to once per second (sub-second precision is not
        // player-visible) purely to reduce UI-thread churn - the
        // calculation itself is already zero-allocation regardless of
        // frequency.
        private void Update()
        {
            if (MatchmakingCountdownText == null) return;

            _countdownRefreshAccumulatorSeconds += Time.unscaledDeltaTime;
            if (_countdownRefreshAccumulatorSeconds < CountdownRefreshIntervalSeconds) return;
            _countdownRefreshAccumulatorSeconds = 0f;

            RefreshMatchmakingCountdown();
        }

        // Modul: matchmaking runs every Sunday at 23:30 UTC. Pure
        // DateTime.UtcNow/TimeSpan struct arithmetic (both value types) -
        // zero managed heap allocation. Written directly into a
        // pre-allocated char buffer via the same WriteTextToBuffer/
        // WriteIntToBuffer helpers every other panel in this codebase
        // uses, never string concatenation/interpolation.
        private void RefreshMatchmakingCountdown()
        {
            TimeSpan remaining = ComputeTimeUntilNextGuildWarMatchmaking(DateTime.UtcNow);

            int offset = WriteTextToBuffer(_countdownBuffer, 0, "Next War In ");
            offset = WriteIntToBuffer(_countdownBuffer, offset, remaining.Days);
            offset = WriteTextToBuffer(_countdownBuffer, offset, "d ");
            offset = WriteIntToBuffer(_countdownBuffer, offset, remaining.Hours);
            offset = WriteTextToBuffer(_countdownBuffer, offset, "h ");
            offset = WriteIntToBuffer(_countdownBuffer, offset, remaining.Minutes);
            offset = WriteTextToBuffer(_countdownBuffer, offset, "m ");
            offset = WriteIntToBuffer(_countdownBuffer, offset, remaining.Seconds);
            offset = WriteTextToBuffer(_countdownBuffer, offset, "s");

            MatchmakingCountdownText.SetCharArray(_countdownBuffer, 0, offset);
        }

        private static TimeSpan ComputeTimeUntilNextGuildWarMatchmaking(DateTime utcNow)
        {
            int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)utcNow.DayOfWeek + 7) % 7;
            DateTime candidate = utcNow.Date.AddDays(daysUntilSunday).AddHours(23).AddMinutes(30);
            if (candidate <= utcNow)
            {
                candidate = candidate.AddDays(7);
            }

            return candidate - utcNow;
        }

        private void RefreshUI()
        {
            if (SyncProxy == null) return;

            bool warActive = SyncProxy.VisualActiveGuildWarId > 0L;

            if (NoActiveWarRoot != null) NoActiveWarRoot.SetActive(!warActive);
            if (ActiveWarRoot != null) ActiveWarRoot.SetActive(warActive);

            if (WarStatusText != null)
            {
                byte activeLanguage = SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;
                LocalizationKey statusKey = warActive ? LocalizationKey.GuildWarStatusActive : LocalizationKey.GuildWarStatusInactive;
                int offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, statusKey, _lineBuffer, 0);
                WarStatusText.SetCharArray(_lineBuffer, 0, offset);
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

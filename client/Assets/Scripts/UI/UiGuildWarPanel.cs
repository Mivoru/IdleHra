using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

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
        public WebSocketClient NetworkClient;

        [Header("Matchmaking Status")]
        public TextMeshProUGUI WarStatusText;
        public GameObject NoActiveWarRoot;
        public GameObject ActiveWarRoot;

        // Modul: Play Mode audit fix. RegisterGuildDefense had a working
        // zero-alloc sender (WebSocketClient.SendRegisterGuildDefenseCommand
        // ZeroAlloc, no parameters needed) but no button anywhere ever
        // called it - Guild War combat participation was entirely
        // unreachable despite the scoreboard above showing it live.
        [Header("Actions")]
        public Button DefendButton;

        // Modul: Play Mode audit follow-up. ExecuteCombatTurn (the
        // turn-based GuildWarActiveMatches attack, distinct from
        // SubmitShardAttack's cross-shard tournament mesh system, which
        // depends on an external _tournamentMeshService not present in
        // this dev environment and is out of scope here) already had a
        // working zero-alloc sender (SendCombatTurnCommandZeroAlloc, only
        // ever called from the dead UiCommandDispatcher grab-bag) and its
        // own damage/turn resolution is entirely server-computed from each
        // guild's aggregate combat stats - the client only needs to echo
        // back the live MatchId/TurnCounter this panel already displays.
        public Button AttackButton;

        // Modul: Play Mode audit follow-up. ContributeToWarSupply already
        // had a working zero-alloc sender (SendWarSupplyCommandZeroAlloc)
        // burning a commodity toward the Supply Chain front - see
        // GuildWarEngine.RunSupplyChainLoopAsync - but no UI anywhere ever
        // called it, so the entire Gathering/Supply front of guild war was
        // unreachable despite its own points column above already
        // rendering. This also surfaced a second real server bug while
        // testing: RunSupplyChainLoopAsync used to stringify CommodityId
        // directly instead of resolving it through the small 1-6
        // ContentRegistry.GetMaterialString mapping (copper_ore/raw_log/
        // iron_ore/oak_log/gold_ore/magic_log - a separate id space from
        // GetItemBaseId's 183-entry equipment catalog), so it could never
        // match a real CommodityRecords row. CommodityId here is that
        // small 1-6 material id, not an equipment catalog id.
        [Header("Supply Contribution")]
        public TMP_InputField ContributeCommodityIdField;
        public TMP_InputField ContributeQuantityField;
        public Button ContributeSupplyButton;

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

        private void Awake()
        {
            if (DefendButton != null)
            {
                DefendButton.onClick.AddListener(HandleDefendClicked);
            }

            if (AttackButton != null)
            {
                AttackButton.onClick.AddListener(HandleAttackClicked);
            }

            if (ContributeSupplyButton != null)
            {
                ContributeSupplyButton.onClick.AddListener(HandleContributeSupplyClicked);
            }
        }

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

        private void HandleDefendClicked()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendRegisterGuildDefenseCommandZeroAlloc();
            }
        }

        private void HandleAttackClicked()
        {
            if (NetworkClient == null || SyncProxy == null) return;
            if (SyncProxy.VisualCombatSimulationMatchId <= 0L) return;

            NetworkClient.SendCombatTurnCommandZeroAlloc((uint)SyncProxy.VisualCombatSimulationMatchId, (uint)SyncProxy.VisualCombatSimulationTurnCounter);
        }

        private void HandleContributeSupplyClicked()
        {
            if (NetworkClient == null) return;

            if (!long.TryParse(ContributeCommodityIdField != null ? ContributeCommodityIdField.text : string.Empty, out long commodityId) || commodityId <= 0)
            {
                return;
            }

            if (!long.TryParse(ContributeQuantityField != null ? ContributeQuantityField.text : string.Empty, out long quantity) || quantity <= 0)
            {
                return;
            }

            NetworkClient.SendWarSupplyCommandZeroAlloc(0, commodityId, quantity);
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
            if (DefendButton != null) DefendButton.gameObject.SetActive(warActive);
            if (ContributeCommodityIdField != null) ContributeCommodityIdField.gameObject.SetActive(warActive);
            if (ContributeQuantityField != null) ContributeQuantityField.gameObject.SetActive(warActive);
            if (ContributeSupplyButton != null) ContributeSupplyButton.gameObject.SetActive(warActive);

            if (WarStatusText != null)
            {
                byte activeLanguage = SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;
                LocalizationKey statusKey = warActive ? LocalizationKey.GuildWarStatusActive : LocalizationKey.GuildWarStatusInactive;
                int offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, statusKey, _lineBuffer, 0);
                WarStatusText.SetCharArray(_lineBuffer, 0, offset);
            }

            if (!warActive)
            {
                if (AttackButton != null) AttackButton.gameObject.SetActive(false);
                return;
            }

            if (AttackButton != null) AttackButton.gameObject.SetActive(SyncProxy.VisualCombatSimulationMatchId > 0L);

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

            SetPointsText(CombatVanguardPointsText, "Vanguard: ", SyncProxy.VisualGuildCombatPoints);
            SetPointsText(ProductionLogisticsPointsText, "Logistics: ", SyncProxy.VisualGuildLogisticsPoints);
            SetPointsText(GatheringSupplyChainPointsText, "Supply: ", SyncProxy.VisualGuildSupplyPoints);

            SetPointsText(EnemyCombatVanguardPointsText, "Enemy Vanguard: ", SyncProxy.VisualEnemyCombatPoints);
            SetPointsText(EnemyProductionLogisticsPointsText, "Enemy Logistics: ", SyncProxy.VisualEnemyLogisticsPoints);
            SetPointsText(EnemyGatheringSupplyChainPointsText, "Enemy Supply: ", SyncProxy.VisualEnemySupplyPoints);

            if (WarMultiplierText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "War multiplier: x");
                offset = WriteIntToBuffer(_lineBuffer, offset, Mathf.RoundToInt(SyncProxy.VisualWarMultiplier * 100f));
                WarMultiplierText.SetCharArray(_lineBuffer, 0, offset);
            }
        }

        // Modul: Guild sub-tab polish. The scene builder seeds each of these
        // with a descriptive placeholder ("Vanguard: 0"), which this method
        // then overwrote with the bare number on the very first refresh -
        // leaving a column of six unlabelled zeros that told the player
        // nothing about which track was which. The label is part of the
        // written value now, so it survives every refresh.
        private void SetPointsText(TextMeshProUGUI target, string label, int points)
        {
            if (target == null) return;

            int offset = WriteTextToBuffer(_lineBuffer, 0, label);
            offset = WriteIntToBuffer(_lineBuffer, offset, points);
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

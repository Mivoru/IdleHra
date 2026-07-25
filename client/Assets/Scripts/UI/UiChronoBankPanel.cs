using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit fix. Banked Chrono Seconds (earned from
    // offline time via ChronoBufferEngine on the server, see
    // VisualBankedChronoSeconds's own doc comment) had every sync field
    // already flowing to the client, and both spend commands
    // (ActivateChronoBoost/ConsumeTimeWarpCore) already had working
    // zero-alloc senders on WebSocketClient - but no panel anywhere ever
    // called them, so the game's own core idle-catchup mechanic was
    // entirely unreachable. Two ways to spend the bank: boost runs the
    // live simulation at 2x/4x for a while (draining the bank per second
    // elapsed), instant warp burns a fixed chunk immediately for an
    // instant catch-up burst. Same char-buffer/no-string-alloc convention
    // and throttled-Update() countdown pattern as UiGuildWarPanel.
    public class UiChronoBankPanel : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 1f;
        private const uint InstantWarpSecondsToConsume = 86400u; // 1 day per press, capped to whatever is banked

        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        [Header("Display")]
        public TextMeshProUGUI BankedSecondsText;
        public TextMeshProUGUI StatusText;

        [Header("Actions")]
        public Button Boost2xButton;
        public Button Boost4xButton;
        public Button InstantWarpButton;

        private readonly char[] _bankedBuffer = new char[48];
        private readonly char[] _statusBuffer = new char[48];
        private float _refreshAccumulatorSeconds;

        private void Awake()
        {
            if (Boost2xButton != null) Boost2xButton.onClick.AddListener(() => HandleBoostClicked(2.0));
            if (Boost4xButton != null) Boost4xButton.onClick.AddListener(() => HandleBoostClicked(4.0));
            if (InstantWarpButton != null) InstantWarpButton.onClick.AddListener(HandleInstantWarpClicked);
        }

        private void OnEnable()
        {
            _refreshAccumulatorSeconds = RefreshIntervalSeconds;
        }

        private void Update()
        {
            _refreshAccumulatorSeconds += Time.unscaledDeltaTime;
            if (_refreshAccumulatorSeconds < RefreshIntervalSeconds) return;
            _refreshAccumulatorSeconds = 0f;

            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (SyncProxy == null) return;

            long bankedSeconds = (long)SyncProxy.VisualBankedChronoSeconds;
            bool isAccelerating = SyncProxy.VisualIsChronoAccelerating;
            byte activeMultiplier = SyncProxy.VisualCurrentSimulationSpeedMultiplier;

            if (BankedSecondsText != null)
            {
                int offset = WriteTextToBuffer(_bankedBuffer, 0, "Banked: ");
                offset = WriteDurationToBuffer(_bankedBuffer, offset, bankedSeconds);
                BankedSecondsText.SetCharArray(_bankedBuffer, 0, offset);
            }

            if (StatusText != null)
            {
                int offset;
                if (isAccelerating && activeMultiplier > 1)
                {
                    offset = WriteTextToBuffer(_statusBuffer, 0, "Accelerating x");
                    offset = WriteIntToBuffer(_statusBuffer, offset, activeMultiplier);
                }
                else
                {
                    offset = WriteTextToBuffer(_statusBuffer, 0, "Idle");
                }
                StatusText.SetCharArray(_statusBuffer, 0, offset);
            }

            bool hasBankedTime = bankedSeconds > 0;
            if (Boost2xButton != null) Boost2xButton.interactable = hasBankedTime && !isAccelerating;
            if (Boost4xButton != null) Boost4xButton.interactable = hasBankedTime && !isAccelerating;
            if (InstantWarpButton != null) InstantWarpButton.interactable = hasBankedTime;
        }

        private void HandleBoostClicked(double multiplier)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendActivateChronoBoostCommandZeroAlloc(multiplier);
            }
        }

        private void HandleInstantWarpClicked()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendConsumeTimeWarpCoreCommandZeroAlloc(InstantWarpSecondsToConsume);
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
            return WriteLongToBuffer(buffer, offset, value);
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

        private static int WriteDurationToBuffer(char[] buffer, int offset, long totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;

            long days = totalSeconds / 86400L;
            long hours = (totalSeconds % 86400L) / 3600L;
            long minutes = (totalSeconds % 3600L) / 60L;

            if (days > 0)
            {
                offset = WriteLongToBuffer(buffer, offset, days);
                offset = WriteTextToBuffer(buffer, offset, "d ");
            }

            offset = WriteLongToBuffer(buffer, offset, hours);
            offset = WriteTextToBuffer(buffer, offset, "h ");
            offset = WriteLongToBuffer(buffer, offset, minutes);
            offset = WriteTextToBuffer(buffer, offset, "m");

            return offset;
        }
    }
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit follow-up. UpdateAutoEatThreshold had a real
    // server-side effect (SimulationEngine only auto-consumes food once
    // PlayerHp drops below AutoEatThreshold% of max HP) and the threshold
    // itself was already on the wire (StateUpdatePacket.AutoEatThreshold),
    // but no client sender or UI existed at all. A stepper (not a Slider -
    // no Slider exists anywhere else in this codebase, and one full
    // background/fill/handle setup isn't worth it for a single 0-100 value)
    // matches the Button-only convention used by every other panel here.
    public class UiAutoEatThresholdPanel : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 1f;
        private const int StepPercent = 10;

        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        public TextMeshProUGUI ThresholdText;
        public Button DecreaseButton;
        public Button IncreaseButton;

        private readonly char[] _lineBuffer = new char[32];
        private float _refreshAccumulatorSeconds;

        private void Awake()
        {
            if (DecreaseButton != null) DecreaseButton.onClick.AddListener(() => HandleStepClicked(-StepPercent));
            if (IncreaseButton != null) IncreaseButton.onClick.AddListener(() => HandleStepClicked(StepPercent));
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
            if (SyncProxy == null || ThresholdText == null) return;

            int offset = WriteTextToBuffer(_lineBuffer, 0, "Auto-Eat: ");
            offset = WriteIntToBuffer(_lineBuffer, offset, SyncProxy.VisualAutoEatThreshold);
            offset = WriteTextToBuffer(_lineBuffer, offset, "%");
            ThresholdText.SetCharArray(_lineBuffer, 0, offset);
        }

        private void HandleStepClicked(int delta)
        {
            if (NetworkClient == null || SyncProxy == null) return;

            int newThreshold = SyncProxy.VisualAutoEatThreshold + delta;
            newThreshold = Mathf.Clamp(newThreshold, 0, 100);
            NetworkClient.SendAutoEatThresholdCommandZeroAlloc(newThreshold);
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

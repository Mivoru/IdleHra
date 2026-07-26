using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Rendering layer for the World Boss combat HUD. Isolated onto its own Sub-Canvas
    // (WorldBossSubCanvas) so its per-tick refresh does not force a mesh rebuild of the
    // high-frequency combat damage canvas, or vice versa.
    public class UiWorldBossDataBinder : MonoBehaviour
    {
        private const int MaxAttempts = FastStringCache.WorldBossMaxAttempts;

        private const byte EventStateActive = 1;

        // No event-state value has been observed yet: forces a state-change detection
        // pass on the first Update instead of comparing against a real state.
        private const byte EventStateUnknown = byte.MaxValue;

        public VisualSyncProxy SyncProxy;

        [Header("World Boss HUD - Canvas Isolation")]
        public Canvas WorldBossSubCanvas;
        public RectTransform WorldBossPanelRect;

        [Header("World Boss HUD")]
        public RectTransform BossHpFillRect;
        public TMP_Text BossHpText;
        public TMP_Text WorldBossRunsText;
        public Button WorldBossAttackButton;
        public TMP_Text EventStateText;

        [Header("World Boss Audio")]
        public SfxPoolEngine SoundEngine;
        public AudioClip CombatTrackClip;
        public float CombatTrackVolume = 1.0f;

        private readonly char[] _worldBossUiBuffer = new char[128];
        private byte _lastEventState = EventStateUnknown;

        private void Update()
        {
            if (SyncProxy == null) return;

            byte eventState = SyncProxy.VisualWorldBossEventState;
            if (eventState != _lastEventState)
            {
                HandleEventStateChanged(_lastEventState, eventState);
                _lastEventState = eventState;
            }

            float currentHp = SyncProxy.VisualWorldBossHp;
            float maxHp = SyncProxy.VisualWorldBossMaxHp;
            float hpFraction = maxHp > 0f ? Mathf.Clamp01(currentHp / maxHp) : 0f;

            if (BossHpFillRect != null)
            {
                // Zero-allocation coordinate translation: drive the fill bar by moving the
                // right-edge anchor instead of touching a Layout/Slider component.
                Vector2 anchorMax = BossHpFillRect.anchorMax;
                anchorMax.x = hpFraction;
                BossHpFillRect.anchorMax = anchorMax;
            }

            if (BossHpText != null)
            {
                int offset = WriteLongToBuffer(_worldBossUiBuffer, 0, (long)currentHp);
                offset = WriteTextToBuffer(_worldBossUiBuffer, offset, " / ");
                offset = WriteLongToBuffer(_worldBossUiBuffer, offset, (long)maxHp);
                BossHpText.SetCharArray(_worldBossUiBuffer, 0, offset);
            }

            byte attemptCount = SyncProxy.VisualWorldBossAttemptCount;
            int remainingRuns = MaxAttempts - attemptCount;
            if (remainingRuns < 0) remainingRuns = 0;

            if (WorldBossRunsText != null)
            {
                string runsLabel = FastStringCache.GetWorldBossRemainingRunsLabel(remainingRuns);
                int offset = WriteTextToBuffer(_worldBossUiBuffer, 0, "Runs: ");
                offset = WriteTextToBuffer(_worldBossUiBuffer, offset, runsLabel);
                WorldBossRunsText.SetCharArray(_worldBossUiBuffer, 0, offset);
            }

            if (WorldBossAttackButton != null)
            {
                // Modul: Play Mode audit fix. This only ever checked
                // attemptCount, never eventState - the World Boss event is
                // only actually active ~14 days a month (LiveOpsTickEngine's
                // 1st-7th/15th-22nd windows), so for most of the month a
                // fresh player (attemptCount 0) saw an interactable Attack
                // button that, when clicked, hit
                // ValidateWorldBossAttackRequest's `!eventActive` check
                // server-side and got TerminateSessionForSecurity'd -
                // confirmed live: the click force-disconnected the session,
                // silently masked by the client's own remembered-device
                // auto-reconnect a moment later, so it looked like nothing
                // happened instead of the real kick it was.
                WorldBossAttackButton.interactable = attemptCount < MaxAttempts && eventState == EventStateActive;
            }

            RefreshEventStateText(eventState, attemptCount);
        }

        // Modul: UI rework. Gating the Attack button on the event window
        // (above) stopped the click from force-disconnecting the player, but
        // left them staring at a permanently greyed-out button with no
        // explanation for ~half the month. This says which of the three
        // real states the boss is in, and when the active one ends.
        private void RefreshEventStateText(byte eventState, int attemptCount)
        {
            if (EventStateText == null) return;

            if (eventState == EventStateActive)
            {
                long remainingSeconds = SyncProxy.VisualWorldBossEventEndEpoch - System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                EventStateText.color = new Color(0.55f, 0.9f, 0.55f, 1f);
                EventStateText.text = remainingSeconds > 0
                    ? "Event active - ends in " + FormatRemaining(remainingSeconds)
                    : "Event active";
                return;
            }

            EventStateText.color = new Color(1f, 1f, 1f, 0.55f);
            EventStateText.text = attemptCount >= MaxAttempts
                ? "No attacks left this event window."
                : "No boss event running right now. Attacks unlock when the next window opens.";
        }

        private static string FormatRemaining(long totalSeconds)
        {
            long days = totalSeconds / 86400L;
            long hours = (totalSeconds % 86400L) / 3600L;
            long minutes = (totalSeconds % 3600L) / 60L;

            if (days > 0) return days + "d " + hours + "h";
            if (hours > 0) return hours + "h " + minutes + "m";
            return minutes + "m";
        }

        private void HandleEventStateChanged(byte previousState, byte newState)
        {
            if (SoundEngine == null || CombatTrackClip == null)
            {
                return;
            }

            if (newState == EventStateActive && previousState != EventStateActive)
            {
                SoundEngine.PlayWorldBossCombatTrack(CombatTrackClip, CombatTrackVolume);
            }
            else if (previousState == EventStateActive && newState != EventStateActive)
            {
                // Covers both conclusion outcomes carried by EventState == 2 (defeated or
                // window-expired failure): either way the combat track fades out cleanly.
                SoundEngine.StopWorldBossCombatTrack();
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

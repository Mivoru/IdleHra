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

        public VisualSyncProxy SyncProxy;

        [Header("World Boss HUD - Canvas Isolation")]
        public Canvas WorldBossSubCanvas;
        public RectTransform WorldBossPanelRect;

        [Header("World Boss HUD")]
        public RectTransform BossHpFillRect;
        public TMP_Text BossHpText;
        public TMP_Text WorldBossRunsText;
        public Button WorldBossAttackButton;

        private readonly char[] _worldBossUiBuffer = new char[128];

        private void Awake()
        {
            // No Layout Group components may remain on the World Boss HUD panel: they trigger
            // CPU layout traversal on every refresh. Explicit anchor offsets replace them.
            if (WorldBossPanelRect != null)
            {
                LayoutGroup layoutGroup = WorldBossPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }

                WorldBossPanelRect.anchorMin = new Vector2(0.5f, 1f);
                WorldBossPanelRect.anchorMax = new Vector2(0.5f, 1f);
                WorldBossPanelRect.pivot = new Vector2(0.5f, 1f);
                WorldBossPanelRect.anchoredPosition = new Vector2(0f, -20f);
            }
        }

        private void Update()
        {
            if (SyncProxy == null) return;

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
                // Core Input Safety: block outbound attack requests once attempts are exhausted.
                WorldBossAttackButton.interactable = attemptCount < MaxAttempts;
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

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Rendering layer for the co-op PvE Guild Raid Boss HUD. Isolated onto its own
    // Sub-Canvas (GuildRaidSubCanvas) so its refresh does not force a mesh rebuild
    // of the high-frequency combat damage canvas, or vice versa.
    public class UiGuildRaidDataBinder : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Guild Raid HUD - Canvas Isolation")]
        public Canvas GuildRaidSubCanvas;
        public RectTransform GuildRaidPanelRect;

        [Header("Guild Raid HUD")]
        public TextMeshProUGUI RaidTierText;
        public TextMeshProUGUI BossHpText;
        public RectTransform HpBarFill;

        private readonly char[] _raidTierUiBuffer = new char[32];
        private readonly char[] _bossHpUiBuffer = new char[64];

        private int _lastRenderedTier = int.MinValue;
        private long _lastRenderedCurrentHp = long.MinValue;
        private long _lastRenderedMaxHp = long.MinValue;

        private void Awake()
        {
            // No Layout Group components may remain on the Guild Raid HUD panel: they
            // trigger CPU layout traversal on every refresh. Explicit anchor offsets
            // replace them.
            if (GuildRaidPanelRect != null)
            {
                LayoutGroup layoutGroup = GuildRaidPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }

                GuildRaidPanelRect.anchorMin = new Vector2(0.5f, 1f);
                GuildRaidPanelRect.anchorMax = new Vector2(0.5f, 1f);
                GuildRaidPanelRect.pivot = new Vector2(0.5f, 1f);
                GuildRaidPanelRect.anchoredPosition = new Vector2(0f, -60f);
            }
        }

        private void Update()
        {
            if (SyncProxy == null) return;

            int tier = SyncProxy.VisualGuildRaidTier;
            long currentHp = SyncProxy.VisualGuildRaidBossCurrentHp;
            long maxHp = SyncProxy.VisualGuildRaidBossMaxHp;

            if (tier == _lastRenderedTier && currentHp == _lastRenderedCurrentHp && maxHp == _lastRenderedMaxHp)
            {
                return;
            }

            _lastRenderedTier = tier;
            _lastRenderedCurrentHp = currentHp;
            _lastRenderedMaxHp = maxHp;

            if (HpBarFill != null)
            {
                float ratio = maxHp > 0L ? Mathf.Clamp01((float)currentHp / maxHp) : 0f;
                Vector2 anchorMax = HpBarFill.anchorMax;
                anchorMax.x = ratio;
                HpBarFill.anchorMax = anchorMax;
            }

            if (RaidTierText != null)
            {
                int offset = WriteTextToBuffer(_raidTierUiBuffer, 0, "Tier ");
                offset = WriteIntToBuffer(_raidTierUiBuffer, offset, tier);
                RaidTierText.SetCharArray(_raidTierUiBuffer, 0, offset);
            }

            if (BossHpText != null)
            {
                int offset = WriteLongToBuffer(_bossHpUiBuffer, 0, currentHp);
                offset = WriteTextToBuffer(_bossHpUiBuffer, offset, " / ");
                offset = WriteLongToBuffer(_bossHpUiBuffer, offset, maxHp);
                BossHpText.SetCharArray(_bossHpUiBuffer, 0, offset);
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

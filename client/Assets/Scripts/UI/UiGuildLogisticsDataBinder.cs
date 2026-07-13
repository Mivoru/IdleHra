using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Rendering layer for the Guild Logistics Depot level HUD. Isolated onto its own
    // Sub-Canvas (GuildLogisticsSubCanvas) so its refresh does not force a mesh
    // rebuild of the high-frequency combat damage canvas, or vice versa.
    public class UiGuildLogisticsDataBinder : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Guild Logistics HUD - Canvas Isolation")]
        public Canvas GuildLogisticsSubCanvas;
        public RectTransform GuildLogisticsPanelRect;

        [Header("Guild Logistics HUD")]
        public TMP_Text LogisticsLevelText;

        private readonly char[] _logisticsUiBuffer = new char[32];
        private int _lastRenderedLevel = int.MinValue;

        private void Awake()
        {
            // No Layout Group components may remain on the Guild Logistics HUD panel:
            // they trigger CPU layout traversal on every refresh. Explicit anchor
            // offsets replace them.
            if (GuildLogisticsPanelRect != null)
            {
                LayoutGroup layoutGroup = GuildLogisticsPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }

                GuildLogisticsPanelRect.anchorMin = new Vector2(0f, 1f);
                GuildLogisticsPanelRect.anchorMax = new Vector2(0f, 1f);
                GuildLogisticsPanelRect.pivot = new Vector2(0f, 1f);
                GuildLogisticsPanelRect.anchoredPosition = new Vector2(20f, -20f);
            }
        }

        private void Update()
        {
            if (SyncProxy == null || LogisticsLevelText == null) return;

            int level = SyncProxy.VisualGuildLogisticsLevel;
            if (level == _lastRenderedLevel) return;

            _lastRenderedLevel = level;

            int offset = WriteTextToBuffer(_logisticsUiBuffer, 0, "Lv. ");
            offset = WriteIntToBuffer(_logisticsUiBuffer, offset, level);
            LogisticsLevelText.SetCharArray(_logisticsUiBuffer, 0, offset);
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

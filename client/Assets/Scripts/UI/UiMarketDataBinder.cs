using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Rendering layer for the Market HUD. Isolated onto its own Sub-Canvas (MarketSubCanvas)
    // so its per-tick text refresh does not force a mesh rebuild of the high-frequency
    // combat damage canvas, or vice versa.
    public class UiMarketDataBinder : MonoBehaviour
    {
        private const long TaxBracketMidThreshold = 500000L;
        private const long TaxBracketHighThreshold = 5000000L;

        public VisualSyncProxy SyncProxy;

        [Header("Market HUD - Canvas Isolation")]
        public Canvas MarketSubCanvas;
        public RectTransform MarketPanelRect;

        [Header("Market HUD")]
        public TMP_Text MarketTaxSummaryText;
        public TMP_Text MarketNetPayoutText;
        public long CurrentListingPrice;

        private readonly char[] _marketUiBuffer = new char[128];

        private void Awake()
        {
            // No Layout Group components may remain on the Market HUD panel: they trigger
            // CPU layout traversal on every refresh. Explicit anchor offsets replace them.
            if (MarketPanelRect != null)
            {
                LayoutGroup layoutGroup = MarketPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }

                MarketPanelRect.anchorMin = new Vector2(0f, 0f);
                MarketPanelRect.anchorMax = new Vector2(0f, 0f);
                MarketPanelRect.pivot = new Vector2(0f, 0f);
                MarketPanelRect.anchoredPosition = new Vector2(20f, 20f);
            }
        }

        private void Update()
        {
            if (SyncProxy == null) return;

            long gold = SyncProxy.GetGoldBalance();
            int tierIndex = ResolveTaxTierIndex(gold);

            if (MarketTaxSummaryText != null)
            {
                string taxLabel = FastStringCache.GetTaxBracketLabel(tierIndex);

                int offset = WriteTextToBuffer(_marketUiBuffer, 0, "Gold: ");
                offset = WriteLongToBuffer(_marketUiBuffer, offset, gold);
                offset = WriteTextToBuffer(_marketUiBuffer, offset, "  Tax: ");
                offset = WriteTextToBuffer(_marketUiBuffer, offset, taxLabel);

                MarketTaxSummaryText.SetCharArray(_marketUiBuffer, 0, offset);
            }

            if (MarketNetPayoutText != null)
            {
                long netPayout = ComputeNetPayout(CurrentListingPrice, tierIndex);

                int offset = WriteTextToBuffer(_marketUiBuffer, 0, "Net Payout: ");
                offset = WriteLongToBuffer(_marketUiBuffer, offset, netPayout);
                offset = WriteTextToBuffer(_marketUiBuffer, offset, "g");

                MarketNetPayoutText.SetCharArray(_marketUiBuffer, 0, offset);
            }
        }

        private static int ResolveTaxTierIndex(long gold)
        {
            if (gold > TaxBracketHighThreshold) return FastStringCache.TaxBracketHigh;
            if (gold >= TaxBracketMidThreshold) return FastStringCache.TaxBracketMid;
            return FastStringCache.TaxBracketLow;
        }

        private static long ComputeNetPayout(long listingPrice, int tierIndex)
        {
            double taxRate = tierIndex == FastStringCache.TaxBracketHigh ? 0.18
                : tierIndex == FastStringCache.TaxBracketMid ? 0.10
                : 0.06;
            return (long)(listingPrice * (1.0 - taxRate));
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

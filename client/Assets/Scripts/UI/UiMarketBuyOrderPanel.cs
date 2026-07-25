using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit follow-up. PlaceLimitOrder (the standing-BUY-
    // order side of MarketOrderBookEngine, distinct from the already-wired
    // instant MarketListItem/MarketBuyItem escrow flow - see
    // UiMarketBrowserWindow's own header comment) had no client sender and
    // no UI at all. Also fixed a real server-side bug found while wiring
    // this: SimulationEngine's dispatcher used to synthesize a bogus
    // "ItemType_{TargetId}" BaseItemId for BUY orders that could never
    // match any real listing - see SimulationEngine's PlaceLimitOrder
    // dispatch comment. TargetId here is the numeric ContentRegistry item
    // id (the same catalog id ConsumableEngine/CombatLootEngine use), not
    // the string BaseItemId the existing browser searches by - no
    // client-side catalog browse exists yet, so this is a raw numeric
    // input like several other newly-wired panels this session.
    public class UiMarketBuyOrderPanel : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        public TMP_InputField ItemIdField;
        public TMP_InputField QualityTierField;
        public TMP_InputField PriceField;
        public Button PlaceBuyOrderButton;
        public TextMeshProUGUI StatusText;

        private void Awake()
        {
            if (PlaceBuyOrderButton != null) PlaceBuyOrderButton.onClick.AddListener(HandlePlaceBuyOrderClicked);
        }

        private void HandlePlaceBuyOrderClicked()
        {
            if (NetworkClient == null) return;

            if (!long.TryParse(ItemIdField != null ? ItemIdField.text : string.Empty, out long itemId) || itemId <= 0)
            {
                if (StatusText != null) StatusText.text = "Enter a valid item id.";
                return;
            }

            if (!int.TryParse(PriceField != null ? PriceField.text : string.Empty, out int price) || price <= 0)
            {
                if (StatusText != null) StatusText.text = "Enter a valid price.";
                return;
            }

            int.TryParse(QualityTierField != null ? QualityTierField.text : "0", out int qualityTier);
            if (qualityTier < 0) qualityTier = 0;

            NetworkClient.SendPlaceLimitOrderCommandZeroAlloc(true, itemId, price, qualityTier);
            if (StatusText != null) StatusText.text = "Buy order placed.";
        }
    }
}

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 40: marketplace browser window. Rows are pooled via
    // UIComponentPool<UiMarketListingRow> and only rebuilt when
    // MarketBrowserCache actually changes (dirty-flag pattern), matching
    // UiCodexListBinder. Buying dispatches the existing CommandType.MarketBuyItem
    // (10) WebSocket command straight into MarketEscrowEngine.BuyItemAsync -
    // no new packet type is needed for the purchase itself, only for browsing
    // the paginated result set (which HandleMarketBrowserListings serves over
    // HTTP, the same pattern already used for Forge/Guild/Codex/Leaderboard
    // snapshots, since a paginated list has no natural fixed size to match
    // StateUpdatePacket's binary layout).
    public class UiMarketBrowserWindow : MonoBehaviour
    {
        private const int PageSize = 20;

        [Header("Market Browser - Canvas Isolation")]
        public Canvas MarketBrowserSubCanvas;
        public RectTransform MarketBrowserPanelRect;

        [Header("Market Browser HUD")]
        public ScrollRect ListScrollRect;
        public Transform RowContainer;
        public UiMarketListingRow RowPrefab;
        public int InitialRowPoolCapacity = 20;

        [Header("Filter Controls")]
        public TMP_InputField BaseItemIdInput;
        public TMP_InputField QualityTierInput;
        public Button SearchButton;
        public Button NextPageButton;
        public Button PrevPageButton;
        public TMP_Text PageIndexText;

        [Header("Tax Bracket Legend")]
        public TMP_Text TaxLegendText;

        public WebSocketClient NetworkClient;

        private UIComponentPool<UiMarketListingRow> _rowPool;
        private readonly List<UiMarketListingRow> _activeRows = new List<UiMarketListingRow>();
        private readonly char[] _pageUiBuffer = new char[32];
        private string _activeBaseItemId = string.Empty;
        private int _activeQualityTier;
        private int _pageIndex;
        private bool _isDirty;

        private void Awake()
        {
            if (MarketBrowserPanelRect != null)
            {
                LayoutGroup layoutGroup = MarketBrowserPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }
            }

            Transform poolParent = RowContainer != null ? RowContainer : (ListScrollRect != null ? ListScrollRect.content : null);
            if (RowPrefab != null && poolParent != null)
            {
                _rowPool = new UIComponentPool<UiMarketListingRow>(RowPrefab, poolParent, InitialRowPoolCapacity);
            }

            if (SearchButton != null) SearchButton.onClick.AddListener(HandleSearchClicked);
            if (NextPageButton != null) NextPageButton.onClick.AddListener(HandleNextPageClicked);
            if (PrevPageButton != null) PrevPageButton.onClick.AddListener(HandlePrevPageClicked);

            if (TaxLegendText != null)
            {
                TaxLegendText.text = "Seller Tax: 5% under 500k gold, 8% 500k-5M, 15% above 5M";
            }
        }

        private void OnEnable()
        {
            MarketBrowserCache.OnMarketCacheUpdated += HandleCacheUpdated;
        }

        private void OnDisable()
        {
            MarketBrowserCache.OnMarketCacheUpdated -= HandleCacheUpdated;
        }

        private void Update()
        {
            if (!_isDirty)
            {
                return;
            }

            RefreshRows();
            _isDirty = false;
        }

        public void OpenForItem(string baseItemId, int qualityTier)
        {
            _activeBaseItemId = baseItemId;
            _activeQualityTier = qualityTier;
            _pageIndex = 0;
            RequestCurrentPage();
        }

        private void HandleSearchClicked()
        {
            _activeBaseItemId = BaseItemIdInput != null ? BaseItemIdInput.text : string.Empty;
            int.TryParse(QualityTierInput != null ? QualityTierInput.text : "0", out _activeQualityTier);
            _pageIndex = 0;
            RequestCurrentPage();
        }

        private void HandleNextPageClicked()
        {
            _pageIndex++;
            RequestCurrentPage();
        }

        private void HandlePrevPageClicked()
        {
            if (_pageIndex <= 0) return;
            _pageIndex--;
            RequestCurrentPage();
        }

        private void RequestCurrentPage()
        {
            if (string.IsNullOrEmpty(_activeBaseItemId))
            {
                return;
            }

            MarketBrowserCache.RequestPage(_activeBaseItemId, _activeQualityTier, _pageIndex, PageSize);
        }

        private void HandleCacheUpdated()
        {
            _isDirty = true;
        }

        private void RefreshRows()
        {
            if (_rowPool == null)
            {
                return;
            }

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<MarketListingData> entries = MarketBrowserCache.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                MarketListingData entry = entries[i];
                UiMarketListingRow row = _rowPool.Spawn();
                row.Bind(entry.OrderId, entry.BaseItemId, entry.QualityTier, entry.Price, HandleBuyClicked);
                _activeRows.Add(row);
            }

            if (PageIndexText != null)
            {
                int offset = WriteTextToBuffer(_pageUiBuffer, 0, "Page ");
                offset = WriteIntToBuffer(_pageUiBuffer, offset, MarketBrowserCache.CurrentPageIndex + 1);
                PageIndexText.SetCharArray(_pageUiBuffer, 0, offset);
            }
        }

        private void HandleBuyClicked(long orderId)
        {
            if (NetworkClient != null)
            {
                // 10 = MarketBuyItem, dispatches into MarketEscrowEngine.BuyItemAsync
                // on the server (see SimulationEngine's MarketBuyItem/MarketListItem
                // command handler). The price argument is unused for a buy.
                NetworkClient.SendMarketCommandZeroAlloc(10, orderId, 0);
            }

            MarketBrowserCache.RemoveListingLocally(orderId);
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

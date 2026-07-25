using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit fix. LegacyStoreEngine's 3 prestige perks
    // (XP Multiplier/Gold Drop Rate/Combat Speed) had a working zero-alloc
    // sender (SendLegacyUnlockCommandZeroAlloc) and real server-side
    // purchase logic, but the perk bitmask itself was never on the wire -
    // the client had no way to show a current rank or compute a next-rank
    // cost, so this half of the Legacy Shop had no purchasable UI at all
    // (citizen-slot unlocks were reachable in principle via
    // CitizenMultiSlotsUnlocked, but are out of scope here - see
    // StateUpdatePacket.LegacyPerksBitmask's own comment for the wire
    // addition this panel depends on).
    //
    // Rank-extraction/cost formulas are duplicated from the server's
    // LegacyPerkResolver rather than shared (same reasoning as
    // UiAchievementRow's MonsterKillAchievementId duplication - the client
    // has no existing link to that server-only static class).
    public class UiLegacyShopPanel : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 1f;
        private const int XpMultiplierBitOffset = 0;
        private const int GoldDropRateBitOffset = 8;
        private const int CombatSpeedBitOffset = 16;
        private const long PerkRankMask = 0xFFL;
        private const int MaxPerkRank = 50;

        private const uint XpMultiplierPerkUnlockId = 2U;
        private const uint GoldDropRatePerkUnlockId = 3U;
        private const uint CombatSpeedPerkUnlockId = 4U;

        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        [Header("Balance")]
        public TextMeshProUGUI ShardBalanceText;

        [Header("XP Multiplier")]
        public TextMeshProUGUI XpMultiplierRankText;
        public Button PurchaseXpMultiplierButton;

        [Header("Gold Drop Rate")]
        public TextMeshProUGUI GoldDropRateRankText;
        public Button PurchaseGoldDropRateButton;

        [Header("Combat Speed")]
        public TextMeshProUGUI CombatSpeedRankText;
        public Button PurchaseCombatSpeedButton;

        private readonly char[] _lineBuffer = new char[64];
        private float _refreshAccumulatorSeconds;

        private void Awake()
        {
            if (PurchaseXpMultiplierButton != null) PurchaseXpMultiplierButton.onClick.AddListener(() => HandlePurchaseClicked(XpMultiplierPerkUnlockId));
            if (PurchaseGoldDropRateButton != null) PurchaseGoldDropRateButton.onClick.AddListener(() => HandlePurchaseClicked(GoldDropRatePerkUnlockId));
            if (PurchaseCombatSpeedButton != null) PurchaseCombatSpeedButton.onClick.AddListener(() => HandlePurchaseClicked(CombatSpeedPerkUnlockId));
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

            int shardBalance = SyncProxy.VisualLegacyShardBalance;
            long perks = SyncProxy.VisualLegacyPerksBitmask;

            if (ShardBalanceText != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "Legacy Shards: ");
                offset = WriteIntToBuffer(_lineBuffer, offset, shardBalance);
                ShardBalanceText.SetCharArray(_lineBuffer, 0, offset);
            }

            RefreshPerkRow(XpMultiplierRankText, PurchaseXpMultiplierButton, GetPerkRank(perks, XpMultiplierBitOffset), shardBalance);
            RefreshPerkRow(GoldDropRateRankText, PurchaseGoldDropRateButton, GetPerkRank(perks, GoldDropRateBitOffset), shardBalance);
            RefreshPerkRow(CombatSpeedRankText, PurchaseCombatSpeedButton, GetPerkRank(perks, CombatSpeedBitOffset), shardBalance);
        }

        private void RefreshPerkRow(TextMeshProUGUI text, Button purchaseButton, int rank, int shardBalance)
        {
            int cost = CalculatePerkRankCost(rank);
            bool isMaxed = rank >= MaxPerkRank;

            if (text != null)
            {
                int offset = WriteTextToBuffer(_lineBuffer, 0, "Rank ");
                offset = WriteIntToBuffer(_lineBuffer, offset, rank);
                offset = WriteTextToBuffer(_lineBuffer, offset, " (+");
                offset = WriteIntToBuffer(_lineBuffer, offset, rank);
                offset = WriteTextToBuffer(_lineBuffer, offset, "%)  ");
                offset = isMaxed
                    ? WriteTextToBuffer(_lineBuffer, offset, "MAX")
                    : WriteTextToBuffer(_lineBuffer, offset, "Next: " + cost + "sh");
                text.SetCharArray(_lineBuffer, 0, offset);
            }

            if (purchaseButton != null)
            {
                purchaseButton.interactable = !isMaxed && shardBalance >= cost;
            }
        }

        private void HandlePurchaseClicked(uint targetUnlockId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendLegacyUnlockCommandZeroAlloc(targetUnlockId, 0);
            }
        }

        private static int GetPerkRank(long legacyPerks, int bitOffset)
        {
            return (int)((legacyPerks >> bitOffset) & PerkRankMask);
        }

        private static int CalculatePerkRankCost(int currentRank)
        {
            if (currentRank >= MaxPerkRank)
            {
                return int.MaxValue;
            }

            return 20 + (currentRank * 8);
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

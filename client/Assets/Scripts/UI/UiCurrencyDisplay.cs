using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Top-right persistent currency readout. Gold and premium currency
    // (Gems) both come from real, already-synced VisualSyncProxy fields
    // (VisualGoldCell via GetGoldBalance()/VisualPremiumCurrencyBalance).
    // Neither field has a dedicated "changed" event on VisualSyncProxy, so
    // this polls once per frame and only rewrites text on an actual
    // change, matching UiSkillTreeWindow's established "skip redundant
    // redraw" convention.
    public class UiCurrencyDisplay : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;
        public TMP_Text GoldText;
        public TMP_Text GemsText;

        private long _lastGold = long.MinValue;
        private uint _lastGems = uint.MaxValue;

        private void Update()
        {
            if (SyncProxy == null) return;

            long gold = SyncProxy.GetGoldBalance();
            if (gold != _lastGold)
            {
                _lastGold = gold;
                if (GoldText != null) GoldText.text = "Gold: " + gold;
            }

            uint gems = SyncProxy.VisualPremiumCurrencyBalance;
            if (gems != _lastGems)
            {
                _lastGems = gems;
                if (GemsText != null) GemsText.text = "Gems: " + gems;
            }
        }
    }
}

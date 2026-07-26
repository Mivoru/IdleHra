using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Single building row for UiVillageOverviewWindow - one per building
    // slot, assigned explicitly in the Inspector (matching
    // UiVillageOverviewPanel's existing per-building field style) rather
    // than a pooled/data-driven list, since the building roster is a small
    // fixed set of 8 known ids (see VillageManagementEngine.ForgeBuildingId..
    // WarehouseBuildingId).
    public class UiVillageBuildingRow : MonoBehaviour
    {
        public int BuildingId;
        public TMP_Text BuildingNameText;
        public TMP_Text LevelText;
        public Button UpgradeButton;
        public GameObject ProgressBarRoot;
        public Image ProgressBarFill;
        public TMP_Text ProgressRemainingText;

        // Modul: UI rework. Each row used to be a bare name, "Lv. 0" and an
        // Upgrade button - nothing said what any building actually does,
        // what upgrading it costs, or why the button was refusing to work
        // (every building is capped at 2 + TownHallLevel * 2, so a player
        // with no Town Hall hits a wall at level 2 with no explanation
        // anywhere on screen).
        public TMP_Text DescriptionText;
        public TMP_Text CostText;

        private readonly char[] _levelUiBuffer = new char[16];
        private readonly char[] _remainingUiBuffer = new char[16];
        private Action<int> _onUpgradeClicked;

        private void Awake()
        {
            if (UpgradeButton != null)
            {
                UpgradeButton.onClick.AddListener(HandleUpgradeClicked);
            }
        }

        public void Bind(Action<int> onUpgradeClicked)
        {
            _onUpgradeClicked = onUpgradeClicked;
        }

        public void SetLevel(int level)
        {
            if (LevelText == null) return;

            int offset = WriteTextToBuffer(_levelUiBuffer, 0, "Lv. ");
            offset = WriteIntToBuffer(_levelUiBuffer, offset, level);
            LevelText.SetCharArray(_levelUiBuffer, 0, offset);
        }

        // Modul: UI rework. Mirrors VillageManagementEngine's real cost
        // rules so a player can see the price before spending: service
        // buildings (Forge/Inn/Breeding/Academy) cost 1000 * 1.5^level in
        // gold, production buildings (Lumberjack/Quarry/Mine/Warehouse)
        // cost 100 * 1.5^level in Wood AND Stone, and the two structural
        // buildings (Town Hall/Crafting Workshop) cost the same 100 *
        // 1.5^level in raw_log AND copper_ore, with the Workshop taking a
        // further cost/10 golden_birch_log on top.
        //
        // This is a deliberate duplication of a server formula, which the
        // server stays authoritative over - the display can be wrong, the
        // charge cannot. Kept honest by naming the same two base constants
        // the engine names; if that engine's curve changes, this needs the
        // same edit.
        private const long ServiceBaseUpgradeCost = 1000L;
        private const long ProductionBaseUpgradeCost = 100L;

        public void SetUpgradeCost(int currentLevel, int maxLevel)
        {
            if (CostText == null) return;

            if (currentLevel >= maxLevel)
            {
                CostText.text = "Max level for this Town Hall (raise Town Hall to go further)";
                return;
            }

            bool isProduction = BuildingId >= 5 && BuildingId <= 8;
            bool isStructural = BuildingId == 9 || BuildingId == 10;

            if (isProduction)
            {
                long cost = ScaledCost(ProductionBaseUpgradeCost, currentLevel);
                CostText.text = "Next: " + cost + " Wood + " + cost + " Stone";
            }
            else if (isStructural)
            {
                long cost = ScaledCost(ProductionBaseUpgradeCost, currentLevel);
                CostText.text = BuildingId == 10
                    ? "Next: " + cost + " Logs + " + cost + " Ore + " + System.Math.Max(1L, cost / 10L) + " Golden Birch Log"
                    : "Next: " + cost + " Logs + " + cost + " Ore";
            }
            else
            {
                CostText.text = "Next: " + ScaledCost(ServiceBaseUpgradeCost, currentLevel) + " gold";
            }
        }

        private static long ScaledCost(long baseCost, int currentLevel)
        {
            if (currentLevel < 0) currentLevel = 0;
            double scaled = baseCost * System.Math.Pow(1.5d, currentLevel);
            return scaled > long.MaxValue ? long.MaxValue : (long)System.Math.Ceiling(scaled);
        }

        // Called once whenever the pending-upgrade slot changes (starts on
        // this building, completes, or moves to/away from this building) -
        // not from Update(), matching this codebase's event-driven UI
        // convention (see UiVillageOverviewPanel). Update() (owned by the
        // parent window) only ticks the already-visible countdown text via
        // TickRemaining/SetFillAmount below.
        public void SetPending(bool isPending)
        {
            if (ProgressBarRoot != null)
            {
                ProgressBarRoot.SetActive(isPending);
            }

            if (UpgradeButton != null)
            {
                UpgradeButton.interactable = !isPending;
            }
        }

        // Modul: clicking Upgrade optimistically disables the button before
        // the server has confirmed the transaction. The next real
        // StateUpdatePacket's SetPending call is authoritative - if the
        // request was accepted, SetPending(true) keeps it locked and shows
        // the progress bar; if it was rejected (insufficient resources, slot
        // already occupied), SetPending(false) re-enables it since
        // PendingUpgradeBuildingId on the server never actually changed.
        public void LockOptimistically()
        {
            if (UpgradeButton != null)
            {
                UpgradeButton.interactable = false;
            }
        }

        public void TickRemaining(long remainingSeconds)
        {
            if (ProgressRemainingText == null) return;

            if (remainingSeconds < 0) remainingSeconds = 0;
            int offset = WriteLongToBuffer(_remainingUiBuffer, 0, remainingSeconds);
            offset = WriteTextToBuffer(_remainingUiBuffer, offset, "s");
            ProgressRemainingText.SetCharArray(_remainingUiBuffer, 0, offset);
        }

        public void SetFillAmount(float fraction)
        {
            if (ProgressBarFill != null)
            {
                ProgressBarFill.fillAmount = Mathf.Clamp01(fraction);
            }
        }

        private void HandleUpgradeClicked()
        {
            _onUpgradeClicked?.Invoke(BuildingId);
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

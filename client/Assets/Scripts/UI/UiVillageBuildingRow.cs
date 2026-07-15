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

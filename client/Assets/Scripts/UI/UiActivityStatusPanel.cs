using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: activity status HUD. The two things a player doing the core loop
    // could not see.
    //
    // Both values have been on the wire and mirrored into VisualSyncProxy for
    // a long time with nothing reading them:
    //
    // - Backpack occupancy. InventoryCapacity was added to the wire
    //   specifically so the client could show "13/20" instead of a bare
    //   countdown, and that display was never built. The first sign a player
    //   got that their backpack was full was loot silently vanishing - and
    //   ActivityHaltReason.InventoryFull exists precisely because that failure
    //   is otherwise invisible.
    // - Activity progress. There was no progress indicator of any kind for
    //   gathering. In an idle game whose gathering loop is roughly a tenth of
    //   total playtime, the player watched nothing happen.
    //
    // Refreshed from Update rather than from an event, unlike
    // UiCharacterStatsPanel: progress is interpolated per frame by
    // VisualSyncProxy (it is a Mathf.Lerp between two snapshots), so an
    // event-driven refresh tied to discrete state changes would make the bar
    // move in visible steps. UiRosterPanel takes the same approach for the
    // same reason. The work per frame is a handful of value-type field reads
    // and, only when a displayed number actually changes, one char-buffer
    // write - nothing allocates in the steady state.
    public class UiActivityStatusPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        public TextMeshProUGUI BackpackText;
        public TextMeshProUGUI ProgressLabelText;
        public Image ProgressBarFill;

        // Tint the backpack readout as it fills, so "nearly full" is legible
        // at a glance rather than requiring the player to read two numbers.
        public Color BackpackNormalColor = new Color(1f, 1f, 1f, 0.85f);
        public Color BackpackFullColor = new Color(1f, 0.45f, 0.4f, 1f);

        // At or above this fraction the readout switches to the warning tint.
        private const float BackpackWarningFraction = 0.85f;

        // "Backpack: 20/20" at its longest.
        private readonly char[] _backpackBuffer = new char[24];

        // Only rewrite the text when the displayed integers actually change -
        // this runs every frame and SetCharArray is not free.
        private int _lastShownUsed = -1;
        private int _lastShownCapacity = -1;

        private void Update()
        {
            if (SyncProxy == null) return;

            RefreshBackpack();
            RefreshProgress();
        }

        private void RefreshBackpack()
        {
            if (BackpackText == null) return;

            int capacity = SyncProxy.VisualInventoryCapacity;
            if (capacity <= 0)
            {
                // No capacity reported yet (no packet, or a pre-census
                // session). Showing "0/0" would be a lie, so show nothing.
                if (_lastShownCapacity != 0)
                {
                    BackpackText.SetCharArray(_backpackBuffer, 0, 0);
                    _lastShownCapacity = 0;
                    _lastShownUsed = -1;
                }
                return;
            }

            // The wire carries space REMAINING; players think in slots used.
            int remaining = Mathf.Clamp(SyncProxy.VisualInventorySpaceRemaining, 0, capacity);
            int used = capacity - remaining;

            if (used == _lastShownUsed && capacity == _lastShownCapacity) return;

            _lastShownUsed = used;
            _lastShownCapacity = capacity;

            int offset = WriteTextToBuffer(_backpackBuffer, 0, "Backpack: ");
            offset = WriteIntToBuffer(_backpackBuffer, offset, used);
            _backpackBuffer[offset++] = '/';
            offset = WriteIntToBuffer(_backpackBuffer, offset, capacity);
            BackpackText.SetCharArray(_backpackBuffer, 0, offset);

            BackpackText.color = used >= capacity * BackpackWarningFraction
                ? BackpackFullColor
                : BackpackNormalColor;
        }

        private void RefreshProgress()
        {
            bool isWorking = SyncProxy.VisualActiveActivityId > 0;

            if (ProgressLabelText != null)
            {
                ProgressLabelText.gameObject.SetActive(isWorking);
            }
            if (ProgressBarFill != null)
            {
                ProgressBarFill.transform.parent.gameObject.SetActive(isWorking);
            }

            if (!isWorking || ProgressBarFill == null) return;

            int required = SyncProxy.VisualRequiredProgressTicks;
            if (required <= 0)
            {
                // Combat reports no required-tick total (its pacing is the
                // monster's HP, shown by the combat arena's own HP bar), so
                // there is nothing meaningful to fill here.
                ProgressBarFill.fillAmount = 0f;
                return;
            }

            float fraction = Mathf.Clamp01(SyncProxy.VisualProgressTicks / required);
            ProgressBarFill.fillAmount = fraction;
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
            if (value <= 0)
            {
                buffer[offset++] = '0';
                return offset;
            }

            int length = 0;
            for (int temp = value; temp > 0; temp /= 10) length++;

            int endOffset = offset + length;
            for (int i = endOffset - 1, temp = value; i >= offset; i--, temp /= 10)
            {
                buffer[i] = (char)('0' + (temp % 10));
            }
            return endOffset;
        }
    }
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: halt reasons. A persistent banner naming why the character is not
    // earning right now.
    //
    // Every one of these states used to be invisible. The activity id silently
    // dropped to 0 (auto-eat depletion, death, no eligible character) or the
    // backpack filled and every drop was discarded while the activity kept
    // running - and in all four cases the screen showed a character standing
    // still, indistinguishable from one that had simply never been deployed.
    // Players had no way to tell "I need to cook food" from "I forgot to press
    // Fight".
    //
    // Lives on the persistent bar layer so it is visible from any screen, not
    // only from Combat - the whole point is that the player finds out without
    // having to go looking.
    //
    // This component MUST sit on a holder that is never deactivated, with
    // BannerRoot as a child. Update does not run on an inactive GameObject, so
    // if this were attached to BannerRoot itself the first hide would be
    // permanent - it could never observe a later halt.
    public class UiActivityHaltBanner : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        // The object shown and hidden. Never this component's own GameObject.
        public GameObject BannerRoot;
        public TMP_Text MessageText;
        public Image BackgroundImage;

        // Full-stop states get the alarm colour; a full backpack is an ongoing
        // loss rather than a stop, so it gets the softer warning colour.
        private static readonly Color StopColor = new Color(0.62f, 0.18f, 0.16f, 0.94f);
        private static readonly Color WarnColor = new Color(0.58f, 0.44f, 0.12f, 0.94f);

        private byte _lastReason = 255;

        private void Update()
        {
            if (SyncProxy == null || BannerRoot == null) return;

            byte reason = SyncProxy.VisualActivityHaltReason;
            if (reason == _lastReason) return;
            _lastReason = reason;

            if (reason == ActivityHaltReason.None)
            {
                BannerRoot.SetActive(false);
                return;
            }

            BannerRoot.SetActive(true);

            if (MessageText != null)
            {
                MessageText.text = DescribeReason(reason);
            }

            if (BackgroundImage != null)
            {
                BackgroundImage.color = reason == ActivityHaltReason.InventoryFull ? WarnColor : StopColor;
            }
        }

        // Each message says what happened AND what to do about it - a banner
        // that only names the fault leaves the player exactly as stuck.
        private static string DescribeReason(byte reason)
        {
            switch (reason)
            {
                case ActivityHaltReason.OutOfFood:
                    return "Your character stopped: the larder is empty. Load cooked food in the Larder screen, then send them out again.";
                case ActivityHaltReason.Died:
                    return "Your character was killed and has respawned. Send them out again - stock more food first if the larder is low.";
                case ActivityHaltReason.InventoryFull:
                    return "Backpack full - everything you find is being thrown away. Sell or bank something to start collecting again.";
                case ActivityHaltReason.NoEligibleCharacter:
                    return "No character is free to send out. One may be lent to the Academy as a mentor - recall them, or breed another.";
                default:
                    return "Your character is idle.";
            }
        }
    }
}

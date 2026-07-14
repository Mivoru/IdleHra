using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 18: Guild Logistics Depot panel. Event-driven only - text/progress bar
    // redraw strictly from VisualSyncProxy.OnGuildStateUpdated, never from Update().
    //
    // Material scoping note: StateUpdatePacket's Guild Logistics fields are single
    // scalars (CurrentStock/TargetRequirement/Level), not keyed per material - the
    // wire protocol only ever reports whichever material the depot most recently
    // updated, with no MaterialId alongside it. There is also no client-visible
    // generic "stock of material X" query anywhere (VisualSyncProxy only exposes
    // the three Modul 16 village commodities - wood/stone/iron_ore - a different,
    // string-keyed commodity namespace from the numeric MaterialId
    // GuildLogisticsDepotEngine actually spends). So TargetMaterialId/DonateQuantity
    // are designer-configured constants for this panel instance rather than
    // resolved from any inventory data, and the Donate button does not pre-flight
    // an affordability check client-side - it sends the deposit command
    // optimistically and lets DepositMaterialAsync's server-side check (the
    // authoritative one regardless) silently no-op if the player is short.
    public class UiGuildLogisticsPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        [Header("Guild Logistics Command")]
        public uint TargetMaterialId = 1;
        public uint DonateQuantity = 10;

        [Header("Guild Logistics HUD")]
        public TextMeshProUGUI LogisticsLevelText;
        public TextMeshProUGUI ContributionText;
        public RectTransform ProgressBarFill;
        public Button DonateButton;

        private readonly char[] _levelBuffer = new char[32];
        private readonly char[] _contributionBuffer = new char[64];

        private void Awake()
        {
            if (DonateButton != null)
            {
                DonateButton.onClick.AddListener(HandleDonateClicked);
            }
        }

        private void OnEnable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnGuildStateUpdated += RefreshUI;
            RefreshUI();
        }

        private void OnDisable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnGuildStateUpdated -= RefreshUI;
        }

        private void RefreshUI()
        {
            if (SyncProxy == null) return;

            int level = SyncProxy.VisualGuildLogisticsLevel;
            long currentStock = SyncProxy.VisualGuildLogisticsCurrentStock;
            long targetRequirement = SyncProxy.VisualGuildLogisticsTargetRequirement;

            if (LogisticsLevelText != null)
            {
                int offset = WriteTextToBuffer(_levelBuffer, 0, "Lv. ");
                offset = WriteIntToBuffer(_levelBuffer, offset, level);
                LogisticsLevelText.SetCharArray(_levelBuffer, 0, offset);
            }

            if (ContributionText != null)
            {
                int offset = WriteLongToBuffer(_contributionBuffer, 0, currentStock);
                offset = WriteTextToBuffer(_contributionBuffer, offset, " / ");
                offset = WriteLongToBuffer(_contributionBuffer, offset, targetRequirement);
                ContributionText.SetCharArray(_contributionBuffer, 0, offset);
            }

            if (ProgressBarFill != null)
            {
                float ratio = targetRequirement > 0L ? Mathf.Clamp01((float)currentStock / targetRequirement) : 0f;
                Vector2 anchorMax = ProgressBarFill.anchorMax;
                anchorMax.x = ratio;
                ProgressBarFill.anchorMax = anchorMax;
            }

            if (DonateButton != null)
            {
                DonateButton.interactable = true;
            }
        }

        private void HandleDonateClicked()
        {
            if (NetworkClient == null) return;

            if (DonateButton != null)
            {
                DonateButton.interactable = false;
            }

            NetworkClient.SendGuildMaterialDepositCommandZeroAlloc(TargetMaterialId, DonateQuantity);

            // Safety-net re-enable: OnGuildStateUpdated only fires on an actual
            // stock change, so a server-side rejection (no guild, insufficient
            // materials, etc.) would otherwise leave the button disabled forever.
            Invoke(nameof(ReEnableDonateButton), 1.0f);
        }

        private void ReEnableDonateButton()
        {
            if (DonateButton != null)
            {
                DonateButton.interactable = true;
            }
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
            return (int)WriteLongToBuffer(buffer, offset, value);
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

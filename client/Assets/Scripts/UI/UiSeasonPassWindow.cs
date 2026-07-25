using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 3.1.
    // Seasonal progress track UI - binds to VisualSyncProxy.
    // VisualActiveChroniclePassLevel/VisualAccumulatedSeasonalXp/
    // VisualClaimedMilestonesBitmask, which mirror PlayerChroniclePass.
    // PassLevel/AccumulatedXp/ClaimedMilestonesBitmask over the wire.
    //
    // Engine note: the task names "SeasonEraEngine" as the metrics source,
    // but that engine governs the 90-day prestige/Legacy-Shard era
    // rotation (server-wide, resets everyone's village/inventory on
    // rollover) - a completely different system from a player's individual
    // battle-pass progress. The real per-player XP-milestone data
    // (PassLevel, AccumulatedXp, ClaimedMilestonesBitmask,
    // ClaimBattlePassReward) lives in PlayerChroniclePass/
    // SimulationEngine's ClaimBattlePassReward handler, which is what this
    // window actually binds to.
    //
    // Fixed 50 rows (MaxMilestones), matching the server's own
    // milestoneIndex >= 50 rejection and the ulong bitmask's 64-bit
    // capacity - pooled via UIComponentPool exactly like every other
    // list-style panel in this codebase, never Instantiate per refresh.
    public class UiSeasonPassWindow : MonoBehaviour
    {
        public const uint MaxMilestones = 50;
        public const int XpPerMilestone = 1000;

        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        [Header("Season Pass HUD")]
        public Transform RowContainer;
        public UiSeasonPassMilestoneRow RowPrefab;
        public TextMeshProUGUI PassLevelText;
        public TextMeshProUGUI AccumulatedXpText;
        public TextMeshProUGUI HeaderText;

        // Modul: Play Mode audit follow-up. PurchaseBattlePass had a real
        // server-side effect (unlocks the premium milestone track for
        // 950 PremiumDiamonds - ChroniclePassEconomy.PremiumPassPriceDiamonds,
        // the price is a static label in MainSceneBuilder rather than a
        // duplicated constant here) but no button anywhere ever called it.
        // Whether the pass is already purchased isn't on the wire
        // (PlayerChroniclePass.PremiumUnlocked has no StateUpdatePacket
        // field, and this project's 700-byte packet ceiling has ~1 byte of
        // headroom left - not worth spending on a single already-purchased
        // flag) - a repeat purchase attempt just silently no-ops
        // server-side (ExecutePassPurchaseAsync's own early-return),
        // matching this button's lack of client-side gating.
        [Header("Premium Purchase")]
        public Button PurchasePremiumButton;

        private UIComponentPool<UiSeasonPassMilestoneRow> _rowPool;
        private readonly UiSeasonPassMilestoneRow[] _activeRows = new UiSeasonPassMilestoneRow[MaxMilestones];
        private readonly char[] _headerBuffer = new char[32];
        private readonly char[] _titleBuffer = new char[32];

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiSeasonPassMilestoneRow>(RowPrefab, RowContainer, (int)MaxMilestones);
            }

            if (HeaderText != null)
            {
                byte activeLanguage = SyncProxy == null || SyncProxy.VisualActiveLanguageState == 0 ? (byte)1 : SyncProxy.VisualActiveLanguageState;
                int offset = LocalizationMatrix.WriteToCharBuffer(activeLanguage, LocalizationKey.HeaderSeasonPass, _titleBuffer, 0);
                HeaderText.SetCharArray(_titleBuffer, 0, offset);
            }

            if (PurchasePremiumButton != null)
            {
                PurchasePremiumButton.onClick.AddListener(HandlePurchasePremiumClicked);
            }
        }

        private void HandlePurchasePremiumClicked()
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendBattlePassPurchaseCommandZeroAlloc();
            }
        }

        private void OnEnable()
        {
            RefreshUI();
        }

        // Modul: no dedicated "season pass updated" event exists on
        // VisualSyncProxy (ActiveChroniclePassLevel/AccumulatedSeasonalXp/
        // ClaimedMilestonesBitmask are plain per-packet scalar copies, not
        // edge-detected fields) - refreshed every frame this window is
        // active instead, matching UiCharacterStatsPanel's own approach for
        // similarly always-current scalar HUD data. Cheap: a fixed 50-row
        // rebind with zero allocation, not a rebuild.
        private void Update()
        {
            RefreshUI();
        }

        private void RefreshUI()
        {
            if (SyncProxy == null || _rowPool == null) return;

            uint passLevel = SyncProxy.VisualActiveChroniclePassLevel;
            uint accumulatedXp = SyncProxy.VisualAccumulatedSeasonalXp;
            ulong claimedBitmask = SyncProxy.VisualClaimedMilestonesBitmask;

            if (PassLevelText != null)
            {
                int offset = WriteTextToBuffer(_headerBuffer, 0, "Pass Level ");
                offset = WriteUIntToBuffer(_headerBuffer, offset, passLevel);
                PassLevelText.SetCharArray(_headerBuffer, 0, offset);
            }

            if (AccumulatedXpText != null)
            {
                int offset = WriteTextToBuffer(_headerBuffer, 0, "");
                offset = WriteUIntToBuffer(_headerBuffer, offset, accumulatedXp);
                offset = WriteTextToBuffer(_headerBuffer, offset, " XP");
                AccumulatedXpText.SetCharArray(_headerBuffer, 0, offset);
            }

            for (uint i = 0; i < MaxMilestones; i++)
            {
                if (_activeRows[i] == null)
                {
                    _activeRows[i] = _rowPool.Spawn();
                }

                int requiredXp = (int)((i + 1U) * XpPerMilestone);
                bool isReached = accumulatedXp >= (uint)requiredXp;
                bool isClaimed = (claimedBitmask & (1UL << (int)i)) != 0UL;

                _activeRows[i].Bind(i, requiredXp, isReached, isClaimed, HandleClaimClicked);
            }
        }

        private void HandleClaimClicked(uint milestoneIndex)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendBattlePassClaimCommandZeroAlloc(milestoneIndex);
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

        private static int WriteUIntToBuffer(char[] buffer, int offset, uint value)
        {
            if (value == 0)
            {
                buffer[offset++] = '0';
                return offset;
            }

            uint temp = value;
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

using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Active Skill Tree: node-based skill unlock UI. Mirrors the four fixed
    // skills in the server's ActiveSkillEngine registry (SkillId 1-4, no
    // server-side name - display names/costs below are presentation-only
    // client data, matching the registry's ManaCost/CooldownMs/
    // DamageMultiplierPct/RequiredSkillPointCost values exactly so the UI
    // never shows a cost the server would actually reject).
    //
    // No dedicated "skills changed" event exists on VisualSyncProxy for
    // VisualUnlockedSkillsBitmask/VisualAvailableSkillPoints (unlike
    // VisualPlayerHp etc, unlocking is a rare discrete action, not a per-tick
    // value worth its own edge-detected event) - this window instead polls
    // both fields once per frame in Update() and only rewrites node visuals
    // when something actually changed, the same "skip redundant redraw"
    // guard ApplyVillagePacketState uses, just performed here instead since
    // no such event exists to hang the check on.
    public class UiSkillTreeWindow : MonoBehaviour
    {
        private static readonly string[] SkillNames = { "Power Strike", "Heavy Blow", "Rupture", "Execute" };
        private static readonly int[] SkillManaCosts = { 10, 20, 30, 50 };
        private static readonly int[] SkillCooldownMs = { 3000, 6000, 10000, 20000 };
        private static readonly int[] SkillDamageMultiplierPct = { 150, 200, 300, 500 };
        private static readonly int[] SkillPointCosts = { 1, 1, 2, 3 };

        public WebSocketClient NetworkClient;
        public VisualSyncProxy SyncProxy;

        [Header("Skill Points")]
        public TMP_Text AvailableSkillPointsText;

        [Header("Skill Node 1")]
        public Button UnlockButton1;
        public TMP_Text NodeText1;
        public GameObject UnlockedOverlay1;

        [Header("Skill Node 2")]
        public Button UnlockButton2;
        public TMP_Text NodeText2;
        public GameObject UnlockedOverlay2;

        [Header("Skill Node 3")]
        public Button UnlockButton3;
        public TMP_Text NodeText3;
        public GameObject UnlockedOverlay3;

        [Header("Skill Node 4")]
        public Button UnlockButton4;
        public TMP_Text NodeText4;
        public GameObject UnlockedOverlay4;

        private Button[] _unlockButtons;
        private TMP_Text[] _nodeTexts;
        private GameObject[] _unlockedOverlays;

        private uint _lastRenderedBitmask = uint.MaxValue;
        private int _lastRenderedSkillPoints = int.MinValue;

        private void Awake()
        {
            _unlockButtons = new[] { UnlockButton1, UnlockButton2, UnlockButton3, UnlockButton4 };
            _nodeTexts = new[] { NodeText1, NodeText2, NodeText3, NodeText4 };
            _unlockedOverlays = new[] { UnlockedOverlay1, UnlockedOverlay2, UnlockedOverlay3, UnlockedOverlay4 };

            for (int i = 0; i < _unlockButtons.Length; i++)
            {
                int skillId = i + 1;
                if (_unlockButtons[i] != null)
                {
                    _unlockButtons[i].onClick.AddListener(() => HandleUnlockClicked(skillId));
                }
            }
        }

        private void Update()
        {
            if (SyncProxy == null) return;

            uint bitmask = SyncProxy.VisualUnlockedSkillsBitmask;
            int skillPoints = SyncProxy.VisualAvailableSkillPoints;
            if (bitmask == _lastRenderedBitmask && skillPoints == _lastRenderedSkillPoints) return;

            _lastRenderedBitmask = bitmask;
            _lastRenderedSkillPoints = skillPoints;

            if (AvailableSkillPointsText != null)
            {
                AvailableSkillPointsText.text = "Skill Points: " + skillPoints;
            }

            for (int i = 0; i < 4; i++)
            {
                int skillId = i + 1;
                bool isUnlocked = (bitmask & (1u << i)) != 0;
                bool canAfford = skillPoints >= SkillPointCosts[i];

                if (_nodeTexts[i] != null)
                {
                    _nodeTexts[i].text = SkillNames[i] + "\nCost: " + SkillPointCosts[i] + " pt\n" +
                        "Mana: " + SkillManaCosts[i] + "  CD: " + (SkillCooldownMs[i] / 1000) + "s\n" +
                        "Damage: " + SkillDamageMultiplierPct[i] + "%";
                }

                if (_unlockedOverlays[i] != null)
                {
                    _unlockedOverlays[i].SetActive(isUnlocked);
                }

                if (_unlockButtons[i] != null)
                {
                    _unlockButtons[i].interactable = !isUnlocked && canAfford;
                }
            }
        }

        private void HandleUnlockClicked(int skillId)
        {
            if (NetworkClient == null) return;
            // 57 = RequestUnlockSkill (see FolkIdle.Server.Network.CommandType).
            NetworkClient.SendCommandZeroAlloc(57, skillId);
        }
    }
}

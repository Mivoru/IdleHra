using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Active Skill Tree: node-based skill unlock UI. Mirrors the four fixed
    // skills in the server's ActiveSkillEngine registry (SkillId 1-4).
    // Numeric costs/cooldowns/damage read live from ClientContentRegistry
    // (parsed from the same server/GameData/skills.json the server itself
    // loads - see ClientContentRegistry.Initialize), so the UI can never
    // show a cost the server would actually reject. SkillNames stays a
    // client-only array: skills.json carries no display-name field (the
    // server has no source of truth for presentation text, only the
    // balance numbers a designer would tune), so there is nothing to
    // synchronize against for names specifically.
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

        public WebSocketClient NetworkClient;
        public VisualSyncProxy SyncProxy;

        [Header("Skill Points")]
        public TMP_Text AvailableSkillPointsText;

        [Header("Skill Node 1")]
        public Button UnlockButton1;
        public TMP_Text NodeText1;
        public GameObject UnlockedOverlay1;
        public Image NodeIcon1;

        [Header("Skill Node 2")]
        public Button UnlockButton2;
        public TMP_Text NodeText2;
        public GameObject UnlockedOverlay2;
        public Image NodeIcon2;

        [Header("Skill Node 3")]
        public Button UnlockButton3;
        public TMP_Text NodeText3;
        public GameObject UnlockedOverlay3;
        public Image NodeIcon3;

        [Header("Skill Node 4")]
        public Button UnlockButton4;
        public TMP_Text NodeText4;
        public GameObject UnlockedOverlay4;
        public Image NodeIcon4;

        private Button[] _unlockButtons;
        private TMP_Text[] _nodeTexts;
        private GameObject[] _unlockedOverlays;
        private Image[] _nodeIcons;

        // Modul: skill icon Addressable keys, loaded once per node through
        // AssetManager (not Resources.Load) so icon art can ship and update
        // over-the-air independently of the client build. Loaded once in
        // Awake, never re-requested per frame - Update() below only ever
        // touches text/overlay/interactable state, matching this window's
        // existing "heavy work once, cheap redraw every frame" convention.
        private static readonly string[] SkillIconAddressableKeys =
        {
            "skill_icon_1", "skill_icon_2", "skill_icon_3", "skill_icon_4"
        };

        private uint _lastRenderedBitmask = uint.MaxValue;
        private int _lastRenderedSkillPoints = int.MinValue;

        private void Awake()
        {
            _unlockButtons = new[] { UnlockButton1, UnlockButton2, UnlockButton3, UnlockButton4 };
            _nodeTexts = new[] { NodeText1, NodeText2, NodeText3, NodeText4 };
            _unlockedOverlays = new[] { UnlockedOverlay1, UnlockedOverlay2, UnlockedOverlay3, UnlockedOverlay4 };
            _nodeIcons = new[] { NodeIcon1, NodeIcon2, NodeIcon3, NodeIcon4 };

            for (int i = 0; i < _unlockButtons.Length; i++)
            {
                int skillId = i + 1;
                if (_unlockButtons[i] != null)
                {
                    _unlockButtons[i].onClick.AddListener(() => HandleUnlockClicked(skillId));
                }

                if (_nodeIcons[i] != null && AssetManager.Instance != null)
                {
                    Image targetIcon = _nodeIcons[i];
                    AssetManager.Instance.LoadAsync<Sprite>(SkillIconAddressableKeys[i], sprite =>
                    {
                        if (targetIcon != null && sprite != null)
                        {
                            targetIcon.sprite = sprite;
                        }
                    });
                }
            }
        }

        private void OnDestroy()
        {
            if (AssetManager.Instance == null) return;

            for (int i = 0; i < SkillIconAddressableKeys.Length; i++)
            {
                if (_nodeIcons != null && _nodeIcons[i] != null)
                {
                    AssetManager.Instance.Release(SkillIconAddressableKeys[i]);
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
                SkillEntry skill = ClientContentRegistry.GetSkill(skillId);
                bool isUnlocked = (bitmask & (1u << i)) != 0;
                bool canAfford = skillPoints >= skill.RequiredSkillPointCost;

                if (_nodeTexts[i] != null)
                {
                    _nodeTexts[i].text = SkillNames[i] + "\nCost: " + skill.RequiredSkillPointCost + " pt\n" +
                        "Mana: " + skill.ManaCost + "  CD: " + (skill.CooldownMs / 1000) + "s\n" +
                        "Damage: " + skill.DamageMultiplierPct + "%";
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

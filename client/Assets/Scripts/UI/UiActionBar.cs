using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Active Skill Tree: real-time hotbar for the four ActiveSkillEngine
    // skills, keys 1-4 (matches SkillHotkeys/SkillId 1-4 directly - index i
    // maps to hotkey i and SkillId i+1 throughout this file). Client-side
    // gating (mana/cooldown/unlocked checks before sending) is prediction
    // only for responsiveness - SimulationEngine's RequestCastSkill handler
    // is the sole source of truth and silently no-ops an invalid cast rather
    // than trusting anything this file computes.
    public class UiActionBar : MonoBehaviour
    {
        private static readonly int[] SkillManaCosts = { 10, 20, 30, 50 };
        private static readonly int[] SkillCooldownMs = { 3000, 6000, 10000, 20000 };
        private static readonly KeyCode[] SkillHotkeys = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4 };

        private const float CastFlashDurationSeconds = 0.25f;

        public WebSocketClient NetworkClient;
        public VisualSyncProxy SyncProxy;

        [Header("Slot 1")]
        public Button CastButton1;
        public Image CooldownOverlay1;
        public Image FlashOverlay1;
        public TMP_Text ManaCostText1;

        [Header("Slot 2")]
        public Button CastButton2;
        public Image CooldownOverlay2;
        public Image FlashOverlay2;
        public TMP_Text ManaCostText2;

        [Header("Slot 3")]
        public Button CastButton3;
        public Image CooldownOverlay3;
        public Image FlashOverlay3;
        public TMP_Text ManaCostText3;

        [Header("Slot 4")]
        public Button CastButton4;
        public Image CooldownOverlay4;
        public Image FlashOverlay4;
        public TMP_Text ManaCostText4;

        [Header("Cast Flash Colors")]
        public Color SuccessFlashColor = new Color(0.2f, 1f, 0.2f, 0.6f);
        public Color FailureFlashColor = new Color(1f, 0.2f, 0.2f, 0.6f);

        private Button[] _castButtons;
        private Image[] _cooldownOverlays;
        private Image[] _flashOverlays;
        private float[] _flashRemainingSeconds;

        private void Awake()
        {
            _castButtons = new[] { CastButton1, CastButton2, CastButton3, CastButton4 };
            _cooldownOverlays = new[] { CooldownOverlay1, CooldownOverlay2, CooldownOverlay3, CooldownOverlay4 };
            _flashOverlays = new[] { FlashOverlay1, FlashOverlay2, FlashOverlay3, FlashOverlay4 };
            var manaCostTexts = new[] { ManaCostText1, ManaCostText2, ManaCostText3, ManaCostText4 };
            _flashRemainingSeconds = new float[4];

            for (int i = 0; i < _castButtons.Length; i++)
            {
                int skillId = i + 1;
                if (_castButtons[i] != null)
                {
                    _castButtons[i].onClick.AddListener(() => TryCastSkill(skillId));
                }

                if (manaCostTexts[i] != null)
                {
                    manaCostTexts[i].text = SkillManaCosts[i].ToString();
                }

                if (_flashOverlays[i] != null)
                {
                    Color c = _flashOverlays[i].color;
                    c.a = 0f;
                    _flashOverlays[i].color = c;
                }
            }
        }

        private void OnEnable()
        {
            if (SyncProxy != null) SyncProxy.OnSkillCastResult += HandleSkillCastResult;
        }

        private void OnDisable()
        {
            if (SyncProxy != null) SyncProxy.OnSkillCastResult -= HandleSkillCastResult;
        }

        private void Update()
        {
            for (int i = 0; i < 4; i++)
            {
                if (Input.GetKeyDown(SkillHotkeys[i]))
                {
                    TryCastSkill(i + 1);
                }
            }

            if (SyncProxy == null) return;

            // Zero-allocation radial cooldown fill - pure per-frame
            // arithmetic on values VisualSyncProxy already interpolated this
            // frame (see VisualSyncProxy.Update), no per-frame allocation or
            // GetComponent lookups (references cached above in Awake).
            SetCooldownFill(_cooldownOverlays[0], SyncProxy.VisualSkill1CooldownRemainingMs, SkillCooldownMs[0]);
            SetCooldownFill(_cooldownOverlays[1], SyncProxy.VisualSkill2CooldownRemainingMs, SkillCooldownMs[1]);
            SetCooldownFill(_cooldownOverlays[2], SyncProxy.VisualSkill3CooldownRemainingMs, SkillCooldownMs[2]);
            SetCooldownFill(_cooldownOverlays[3], SyncProxy.VisualSkill4CooldownRemainingMs, SkillCooldownMs[3]);

            float currentMana = SyncProxy.VisualCurrentMana;
            uint unlockedBitmask = SyncProxy.VisualUnlockedSkillsBitmask;
            for (int i = 0; i < 4; i++)
            {
                if (_castButtons[i] != null)
                {
                    bool isUnlocked = (unlockedBitmask & (1u << i)) != 0;
                    bool hasMana = currentMana >= SkillManaCosts[i];
                    _castButtons[i].interactable = isUnlocked && hasMana;
                }

                if (_flashRemainingSeconds[i] > 0f)
                {
                    _flashRemainingSeconds[i] -= Time.deltaTime;
                    float alpha = Mathf.Clamp01(_flashRemainingSeconds[i] / CastFlashDurationSeconds) * 0.6f;
                    if (_flashOverlays[i] != null)
                    {
                        Color c = _flashOverlays[i].color;
                        c.a = alpha;
                        _flashOverlays[i].color = c;
                    }
                }
            }
        }

        // fillAmount rises from 0 (just cast, fully covered) to 1 (ready) as
        // the cooldown drains - a standard "swipe clears" ability-icon
        // cooldown read, matching Image.FillMethod.Radial360's default
        // clockwise sweep on an Image.Type.Filled overlay.
        private static void SetCooldownFill(Image overlay, float remainingMs, int totalMs)
        {
            if (overlay == null || totalMs <= 0) return;
            float ready = 1f - Mathf.Clamp01(remainingMs / totalMs);
            overlay.fillAmount = ready;
            overlay.enabled = remainingMs > 0f;
        }

        private float GetCooldownRemainingMs(int index)
        {
            if (index == 0) return SyncProxy.VisualSkill1CooldownRemainingMs;
            if (index == 1) return SyncProxy.VisualSkill2CooldownRemainingMs;
            if (index == 2) return SyncProxy.VisualSkill3CooldownRemainingMs;
            return SyncProxy.VisualSkill4CooldownRemainingMs;
        }

        private void TryCastSkill(int skillId)
        {
            if (NetworkClient == null || SyncProxy == null) return;

            int index = skillId - 1;
            if (index < 0 || index >= 4) return;

            bool isUnlocked = (SyncProxy.VisualUnlockedSkillsBitmask & (1u << index)) != 0;
            bool hasMana = SyncProxy.VisualCurrentMana >= SkillManaCosts[index];
            bool offCooldown = GetCooldownRemainingMs(index) <= 0f;

            if (!isUnlocked || !hasMana || !offCooldown) return;

            // 58 = RequestCastSkill (see FolkIdle.Server.Network.CommandType).
            NetworkClient.SendCommandZeroAlloc(58, skillId);
        }

        private void HandleSkillCastResult(int skillId, bool success)
        {
            int index = skillId - 1;
            if (index < 0 || index >= 4) return;

            _flashRemainingSeconds[index] = CastFlashDurationSeconds;
            if (_flashOverlays[index] != null)
            {
                _flashOverlays[index].color = success ? SuccessFlashColor : FailureFlashColor;
            }
        }
    }
}

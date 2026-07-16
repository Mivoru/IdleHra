using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 18: co-op Guild Raid boss panel. Event-driven only - HP bar/text
    // redraw strictly from VisualSyncProxy.OnGuildStateUpdated, never from
    // Update().
    //
    // Boss-death detection note: GuildRaidEngine.ProcessGuildRaidTickAsync
    // clamps RaidBossCurrentHp to exactly 0 on the killing tick and does NOT
    // advance RaidTier or reset HP itself anymore (Phase: Full-Stack
    // Production Polish, Part 3.2 removed that auto-advance) - a literal
    // "CurrentHp reached 0" value is now genuinely observable on the wire,
    // so that transition (not a RaidTier increase) is the real death
    // signal. RaidTier only increases when a Guild Leader successfully
    // spends gold to manually (re)start the next tier via
    // SendLaunchGuildRaidCommandZeroAlloc below - a distinct "new raid
    // begins" moment, reusing the same SFX for simplicity.
    //
    // Launch Raid lifecycle note: a raid no longer auto-restarts after a
    // kill - the Launch Raid button re-enables once the current boss is
    // defeated (currentHp <= 0) or no raid has ever been started for this
    // guild (maxHp <= 0), letting the Guild Leader manually begin the next
    // tier at any time thereafter. Non-leader clients can still press the
    // button (SendLaunchGuildRaidCommandZeroAlloc carries no role claim of
    // its own), but the server-side leader check inside
    // GuildRaidEngine.TryStartRaidAsync silently no-ops the request.
    public class UiGuildRaidPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;
        public SfxPoolEngine SfxEngine;

        [Header("Guild Raid HUD")]
        public TextMeshProUGUI RaidTierText;
        public TextMeshProUGUI BossHpText;
        public RectTransform HpBarFill;
        public Button LaunchRaidButton;

        [Header("Damage Indicator Color")]
        public Color IdleHpColor = Color.white;
        public Color ActiveDamageColor = Color.yellow;

        [Header("Boss Death SFX")]
        public AudioClip BossDeathClip;
        public float BossDeathVolume = 1.0f;

        private readonly char[] _raidTierBuffer = new char[32];
        private readonly char[] _bossHpBuffer = new char[64];

        private int _lastObservedTier = -1;
        private long _lastObservedCurrentHp = -1L;

        private void Awake()
        {
            if (LaunchRaidButton != null)
            {
                LaunchRaidButton.onClick.AddListener(HandleLaunchRaidClicked);
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

            int tier = SyncProxy.VisualGuildRaidTier;
            long currentHp = SyncProxy.VisualGuildRaidBossCurrentHp;
            long maxHp = SyncProxy.VisualGuildRaidBossMaxHp;

            bool isDamageActive = _lastObservedCurrentHp >= 0L && currentHp < _lastObservedCurrentHp;
            bool bossJustDied = _lastObservedCurrentHp > 0L && currentHp <= 0L;
            bool newTierStarted = _lastObservedTier >= 0 && tier > _lastObservedTier;

            if (bossJustDied || newTierStarted)
            {
                PlayBossDeathSfx();
            }

            _lastObservedTier = tier;
            _lastObservedCurrentHp = currentHp;

            if (RaidTierText != null)
            {
                int offset = WriteTextToBuffer(_raidTierBuffer, 0, "Tier ");
                offset = WriteIntToBuffer(_raidTierBuffer, offset, tier);
                RaidTierText.SetCharArray(_raidTierBuffer, 0, offset);
            }

            if (BossHpText != null)
            {
                int offset = WriteLongToBuffer(_bossHpBuffer, 0, currentHp);
                offset = WriteTextToBuffer(_bossHpBuffer, offset, " / ");
                offset = WriteLongToBuffer(_bossHpBuffer, offset, maxHp);
                BossHpText.SetCharArray(_bossHpBuffer, 0, offset);
                BossHpText.color = isDamageActive ? ActiveDamageColor : IdleHpColor;
            }

            if (HpBarFill != null)
            {
                float ratio = maxHp > 0L ? Mathf.Clamp01((float)currentHp / maxHp) : 0f;
                Vector2 anchorMax = HpBarFill.anchorMax;
                anchorMax.x = ratio;
                HpBarFill.anchorMax = anchorMax;
            }

            if (LaunchRaidButton != null)
            {
                LaunchRaidButton.interactable = currentHp <= 0L || maxHp <= 0L;
            }
        }

        private void PlayBossDeathSfx()
        {
            if (SfxEngine != null && BossDeathClip != null)
            {
                SfxEngine.PlaySoundClip(BossDeathClip, BossDeathVolume, false);
            }
        }

        private void HandleLaunchRaidClicked()
        {
            if (NetworkClient == null) return;

            if (LaunchRaidButton != null)
            {
                LaunchRaidButton.interactable = false;
            }

            NetworkClient.SendLaunchGuildRaidCommandZeroAlloc();

            // Safety-net: OnGuildStateUpdated only fires on an actual HP/tier
            // change, so re-evaluate against real state shortly after in case the
            // launch was a no-op (a raid was already active).
            Invoke(nameof(RefreshUI), 1.0f);
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

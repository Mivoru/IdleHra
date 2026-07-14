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
    // Boss-death detection note: GuildRaidEngine.ProcessGuildRaidTickAsync applies
    // damage and, when it would drop HP to or below zero, tier-advances and resets
    // to the new tier's full HP within the *same* transaction before ever pushing
    // a notification - so a literal "CurrentHp reached 0" packet value is never
    // actually observable on the wire. The only real, observable signal that a
    // kill happened is RaidTier increasing between two OnGuildStateUpdated events,
    // so that is what triggers the death SFX below.
    //
    // Launch Raid lifecycle note: once launched, a raid never stops - the cron
    // auto-advances every subsequent tier forever with no explicit "next raid"
    // action. So after a guild's first successful launch this button is
    // permanently non-interactable for that guild; this matches the passive,
    // always-on raid design and is not a bug.
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
            bool bossWasDefeated = _lastObservedTier >= 0 && tier > _lastObservedTier;

            if (bossWasDefeated)
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

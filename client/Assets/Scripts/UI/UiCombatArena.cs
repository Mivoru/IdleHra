using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: Combat Arena. Wires VisualSyncProxy's combat signals to visual
    // entity targets and pooled VFX. No Instantiate/Destroy happens here at
    // all - enemy/player visuals are pre-placed scene objects toggled via
    // SetActive, and all hit-splat/projectile visuals go through
    // CombatVfxPool. Event-driven for instance changes (matching this
    // codebase's established UI convention - see UiVillageOverviewWindow/
    // UiCodexRegionsWindow); Update() only ticks the two health bar fill
    // amounts every frame from VisualSyncProxy's already-interpolated HP
    // (VisualMonsterHp/VisualPlayerHp use VisualSyncProxy's adaptive EMA
    // playback delay, this arena does not re-implement interpolation).
    public class UiCombatArena : MonoBehaviour
    {
        // ActiveAudioTrackId classification set server-side (SimulationEngine):
        // 1 = idle, 2 = gathering, 3 = combat, 4 = world boss. Both 3 and 4
        // mean an enemy target is present and should be shown.
        private const byte AudioTrackCombat = 3;
        private const byte AudioTrackWorldBoss = 4;

        public VisualSyncProxy SyncProxy;
        public CombatVfxPool VfxPool;

        [Header("Arena Root")]
        public GameObject ArenaRoot;

        [Header("Player Target")]
        public RectTransform PlayerAnchor;
        public Image PlayerHealthBarFill;
        public TMP_Text PlayerHealthText;

        [Header("Enemy Target")]
        public GameObject EnemyVisualRoot;
        public RectTransform EnemyAnchor;
        public Image EnemyHealthBarFill;
        public TMP_Text EnemyHealthText;

        private readonly char[] _healthUiBuffer = new char[32];

        // High-water-mark max HP: monsters always broadcast their full HP
        // the instant they spawn (SimulationEngine sets CurrentMonsterHp to
        // MaxHp*1000 on switch, before any damage tick can run), so the
        // first value observed per monster instance is its true max. Player
        // max HP is not transmitted at all (it is a dynamic, server-computed
        // value); tracking the highest HP ever observed is an honest
        // client-side approximation, not an authoritative max - it only
        // converges to the real value once the player has been at full HP
        // at least once since this arena became active.
        private float _monsterMaxHpObserved;
        private float _playerMaxHpObserved;

        private void Awake()
        {
            if (ArenaRoot != null) ArenaRoot.SetActive(false);
            if (EnemyVisualRoot != null) EnemyVisualRoot.SetActive(false);
        }

        private void OnEnable()
        {
            if (SyncProxy != null)
            {
                SyncProxy.OnCombatInstanceChanged += HandleCombatInstanceChanged;
                SyncProxy.OnMonsterHit += HandleMonsterHit;
                SyncProxy.OnPlayerHit += HandlePlayerHit;
            }

            HandleCombatInstanceChanged();
        }

        private void OnDisable()
        {
            if (SyncProxy != null)
            {
                SyncProxy.OnCombatInstanceChanged -= HandleCombatInstanceChanged;
                SyncProxy.OnMonsterHit -= HandleMonsterHit;
                SyncProxy.OnPlayerHit -= HandlePlayerHit;
            }
        }

        // Health bars only - this is the one thing that legitimately needs
        // per-frame updates, since VisualSyncProxy's interpolated HP moves
        // every frame between packets. Everything else in this arena is
        // event-driven.
        private void Update()
        {
            if (SyncProxy == null) return;

            if (SyncProxy.VisualPlayerHp > _playerMaxHpObserved)
            {
                _playerMaxHpObserved = SyncProxy.VisualPlayerHp;
            }

            SetHealthBar(PlayerHealthBarFill, PlayerHealthText, SyncProxy.VisualPlayerHp, _playerMaxHpObserved);

            bool inCombat = IsCombatTrack(SyncProxy.VisualActiveAudioTrackId);
            if (inCombat)
            {
                SetHealthBar(EnemyHealthBarFill, EnemyHealthText, SyncProxy.VisualMonsterHp, _monsterMaxHpObserved);
            }
        }

        private void HandleCombatInstanceChanged()
        {
            if (SyncProxy == null) return;

            bool inCombat = IsCombatTrack(SyncProxy.VisualActiveAudioTrackId);

            if (ArenaRoot != null) ArenaRoot.SetActive(inCombat);
            if (EnemyVisualRoot != null) EnemyVisualRoot.SetActive(inCombat);

            if (inCombat)
            {
                // A new monster instance always broadcasts its full starting
                // HP before any hit can land against it - see the class
                // comment on _monsterMaxHpObserved.
                _monsterMaxHpObserved = SyncProxy.VisualMonsterHp;
            }
        }

        private void HandleMonsterHit(int damageAmount, bool isCritical)
        {
            if (VfxPool == null || EnemyAnchor == null) return;

            VfxPool.SpawnDamageText(EnemyAnchor.anchoredPosition, damageAmount, isCritical);

            if (PlayerAnchor != null)
            {
                VfxPool.SpawnProjectile(PlayerAnchor.anchoredPosition, EnemyAnchor.anchoredPosition);
            }
        }

        private void HandlePlayerHit(int damageAmount, bool isCritical)
        {
            if (VfxPool == null || PlayerAnchor == null) return;

            VfxPool.SpawnDamageText(PlayerAnchor.anchoredPosition, damageAmount, isCritical);

            if (EnemyAnchor != null)
            {
                VfxPool.SpawnProjectile(EnemyAnchor.anchoredPosition, PlayerAnchor.anchoredPosition);
            }
        }

        private static bool IsCombatTrack(byte audioTrackId)
        {
            return audioTrackId == AudioTrackCombat || audioTrackId == AudioTrackWorldBoss;
        }

        private void SetHealthBar(Image fillImage, TMP_Text label, float currentHp, float maxHp)
        {
            if (fillImage != null)
            {
                fillImage.fillAmount = maxHp > 0f ? Mathf.Clamp01(currentHp / maxHp) : 0f;
            }

            if (label != null)
            {
                int offset = WriteIntToBuffer(_healthUiBuffer, 0, Mathf.RoundToInt(currentHp));
                offset = WriteTextToBuffer(_healthUiBuffer, offset, " / ");
                offset = WriteIntToBuffer(_healthUiBuffer, offset, Mathf.RoundToInt(maxHp));
                label.SetCharArray(_healthUiBuffer, 0, offset);
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
    }
}

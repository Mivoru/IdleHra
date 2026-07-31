using UnityEngine;

namespace FolkIdle.Client.Engine
{
    // Modul: audio pipeline. Turns authoritative state changes into sound
    // effects, in one place rather than scattered across the UI components that
    // happen to display the same numbers.
    //
    // Combat hits come from VisualSyncProxy.OnMonsterHit/OnPlayerHit, which
    // already fire exactly once per real server tick where raw HP dropped for
    // the same combat instance. Deriving them here from VisualMonsterHp instead
    // would be wrong twice over: that value is a per-frame Mathf.Lerp of two
    // packets, so one real swing would spread across every frame of the
    // interpolation window and play dozens of times at 60fps.
    //
    // Level-ups have no such event, so they are edge-detected off
    // VisualPlayerLevel - a discrete field taken straight from the latest
    // packet, not interpolated, so a per-frame comparison is sound.
    public class GameAudioEventRelay : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        private int _lastObservedLevel = -1;
        private bool _hasLevelBaseline;

        private void OnEnable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnMonsterHit += HandleMonsterHit;
            SyncProxy.OnCombatInstanceChanged += HandleCombatInstanceChanged;
        }

        private void OnDisable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnMonsterHit -= HandleMonsterHit;
            SyncProxy.OnCombatInstanceChanged -= HandleCombatInstanceChanged;
        }

        private void HandleMonsterHit(int damage, bool isCritical)
        {
            GameAudioDirector.Play(GameSfx.CombatPlayerHit);
        }

        // The combat instance changes when the current monster is replaced,
        // which on this server only happens after the previous one died.
        private void HandleCombatInstanceChanged()
        {
            GameAudioDirector.Play(GameSfx.CombatMonsterDefeated);
        }

        private void Update()
        {
            if (SyncProxy == null) return;

            int level = SyncProxy.VisualPlayerLevel;

            // The first frame establishes a baseline rather than firing.
            // Without it, logging in at level 40 would announce a level-up, and
            // so would every reconnect.
            if (!_hasLevelBaseline)
            {
                _lastObservedLevel = level;
                _hasLevelBaseline = true;
                return;
            }

            if (level > _lastObservedLevel)
            {
                GameAudioDirector.Play(GameSfx.LevelUp);
            }

            _lastObservedLevel = level;
        }
    }
}

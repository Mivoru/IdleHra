using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: zero-allocation VFX pool for the Combat Arena. Both pools are
    // pre-warmed once in Awake (the one place Instantiate is allowed to run)
    // via the same UIComponentPool<T> already used throughout this codebase
    // (UiMarketBrowserWindow, UiVillageOverviewWindow, UiCodexRegionsWindow)
    // - reusing it here rather than duplicating the pooling logic. Spawn/
    // Despawn during active combat rendering only ever toggles
    // GameObject.SetActive on an existing instance; Instantiate only runs
    // again if a pool's pre-warmed capacity is ever exceeded (matching that
    // same existing pool's documented fallback behavior), which sane
    // capacity values should make effectively never happen in practice.
    public class CombatVfxPool : MonoBehaviour
    {
        [Header("Floating Damage Text")]
        public UiFloatingDamageText DamageTextPrefab;
        public Transform DamageTextContainer;
        public int InitialDamageTextPoolCapacity = 24;

        [Header("Attack Projectiles")]
        public UiAttackProjectile ProjectilePrefab;
        public Transform ProjectileContainer;
        public int InitialProjectilePoolCapacity = 8;

        private UIComponentPool<UiFloatingDamageText> _damageTextPool;
        private UIComponentPool<UiAttackProjectile> _projectilePool;

        private void Awake()
        {
            if (DamageTextPrefab != null && DamageTextContainer != null)
            {
                _damageTextPool = new UIComponentPool<UiFloatingDamageText>(DamageTextPrefab, DamageTextContainer, InitialDamageTextPoolCapacity);
            }

            if (ProjectilePrefab != null && ProjectileContainer != null)
            {
                _projectilePool = new UIComponentPool<UiAttackProjectile>(ProjectilePrefab, ProjectileContainer, InitialProjectilePoolCapacity);
            }
        }

        public void SpawnDamageText(Vector2 anchoredPosition, int damageAmount, bool isCritical)
        {
            if (_damageTextPool == null) return;

            UiFloatingDamageText instance = _damageTextPool.Spawn();
            instance.Activate(this, anchoredPosition, damageAmount, isCritical);
        }

        public void DespawnDamageText(UiFloatingDamageText instance)
        {
            _damageTextPool?.Despawn(instance);
        }

        public void SpawnProjectile(Vector2 fromAnchoredPosition, Vector2 toAnchoredPosition)
        {
            if (_projectilePool == null) return;

            UiAttackProjectile instance = _projectilePool.Spawn();
            instance.Activate(this, fromAnchoredPosition, toAnchoredPosition);
        }

        public void DespawnProjectile(UiAttackProjectile instance)
        {
            _projectilePool?.Despawn(instance);
        }
    }
}

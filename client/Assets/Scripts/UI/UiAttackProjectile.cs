using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: pooled basic-attack projectile, a short-lived visual travelling
    // from attacker to target in UI space. Fully self-managed lifetime,
    // matching UiFloatingDamageText - Activate() is called by
    // CombatVfxPool.Spawn, and this instance ticks its own travel progress
    // in Update and calls back into the pool to despawn itself on arrival.
    // No Instantiate/Destroy ever happens here.
    public class UiAttackProjectile : MonoBehaviour
    {
        private const float TravelSeconds = 0.25f;

        public RectTransform SelfRectTransform;

        private float _elapsedSeconds;
        private Vector2 _fromAnchoredPosition;
        private Vector2 _toAnchoredPosition;
        private CombatVfxPool _owningPool;

        public void Activate(CombatVfxPool owningPool, Vector2 fromAnchoredPosition, Vector2 toAnchoredPosition)
        {
            _owningPool = owningPool;
            _elapsedSeconds = 0f;
            _fromAnchoredPosition = fromAnchoredPosition;
            _toAnchoredPosition = toAnchoredPosition;

            if (SelfRectTransform != null)
            {
                SelfRectTransform.anchoredPosition = fromAnchoredPosition;
            }
        }

        private void Update()
        {
            _elapsedSeconds += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsedSeconds / TravelSeconds);

            if (SelfRectTransform != null)
            {
                SelfRectTransform.anchoredPosition = Vector2.Lerp(_fromAnchoredPosition, _toAnchoredPosition, t);
            }

            if (_elapsedSeconds >= TravelSeconds)
            {
                _owningPool?.DespawnProjectile(this);
            }
        }
    }
}

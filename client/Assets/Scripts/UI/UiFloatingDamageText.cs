using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: pooled floating combat damage number. Fully self-managed
    // lifetime - Activate() is called by CombatVfxPool.Spawn, and this
    // instance ticks its own countdown in Update and calls back into the
    // pool to despawn itself once its lifetime elapses. No Instantiate/
    // Destroy ever happens here; the GameObject is only ever
    // enabled/disabled by the owning UIComponentPool.
    public class UiFloatingDamageText : MonoBehaviour
    {
        private const float LifetimeSeconds = 1.0f;
        private const float RiseDistance = 60f;

        public TMP_Text DamageText;
        public RectTransform SelfRectTransform;
        public Color NormalColor = Color.white;
        public Color CriticalColor = Color.red;
        public float CriticalFontSizeMultiplier = 1.4f;

        private readonly char[] _uiBuffer = new char[16];
        private float _elapsedSeconds;
        private float _baseFontSize;
        private bool _baseFontSizeCaptured;
        private Vector2 _startAnchoredPosition;
        private CombatVfxPool _owningPool;

        public void Activate(CombatVfxPool owningPool, Vector2 anchoredPosition, int damageAmount, bool isCritical)
        {
            _owningPool = owningPool;
            _elapsedSeconds = 0f;
            _startAnchoredPosition = anchoredPosition;

            if (SelfRectTransform != null)
            {
                SelfRectTransform.anchoredPosition = anchoredPosition;
            }

            if (DamageText != null)
            {
                if (!_baseFontSizeCaptured)
                {
                    _baseFontSize = DamageText.fontSize;
                    _baseFontSizeCaptured = true;
                }

                DamageText.color = isCritical ? CriticalColor : NormalColor;
                DamageText.fontSize = isCritical ? _baseFontSize * CriticalFontSizeMultiplier : _baseFontSize;

                int offset = WriteIntToBuffer(_uiBuffer, 0, damageAmount);
                DamageText.SetCharArray(_uiBuffer, 0, offset);
            }
        }

        private void Update()
        {
            _elapsedSeconds += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsedSeconds / LifetimeSeconds);

            if (SelfRectTransform != null)
            {
                SelfRectTransform.anchoredPosition = _startAnchoredPosition + new Vector2(0f, RiseDistance * t);
            }

            if (DamageText != null)
            {
                Color color = DamageText.color;
                color.a = 1f - t;
                DamageText.color = color;
            }

            if (_elapsedSeconds >= LifetimeSeconds)
            {
                _owningPool?.DespawnDamageText(this);
            }
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

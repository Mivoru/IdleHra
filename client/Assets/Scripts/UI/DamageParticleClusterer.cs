using System;
using System.Threading;
using UnityEngine;
using TMPro;

namespace FolkIdle.Client.UI
{
    public class DamageParticleClusterer : MonoBehaviour
    {
        private const int MaxParticles = 6;
        private TMP_Text[] _particleTexts = new TMP_Text[MaxParticles];
        private RectTransform[] _particleRects = new RectTransform[MaxParticles];
        
        private int _unmanagedDamageTotal = 0;
        private long _windowEndMs = 0;
        
        private int _activeParticleIndex = 0;
        private Engine.UiTransitionState[] _particleTransitions = new Engine.UiTransitionState[MaxParticles];

        private void Awake()
        {
            for (int i = 0; i < MaxParticles; i++)
            {
                if (i < transform.childCount)
                {
                    var child = transform.GetChild(i);
                    _particleTexts[i] = child.GetComponent<TMP_Text>();
                    _particleRects[i] = child.GetComponent<RectTransform>();
                    if (_particleTexts[i] != null)
                    {
                        _particleTexts[i].gameObject.SetActive(false);
                    }
                }
            }
        }

        public void ThreadSafeAddDamage(int damage)
        {
            Interlocked.Add(ref _unmanagedDamageTotal, damage);
        }

        private void Update()
        {
            long currentMs = Environment.TickCount64;

            if (currentMs >= _windowEndMs)
            {
                int damageToRender = Interlocked.Exchange(ref _unmanagedDamageTotal, 0);
                if (damageToRender > 0)
                {
                    SpawnParticle(damageToRender);
                    _windowEndMs = currentMs + 100;
                }
            }

            // Animate existing particles using unmanaged easing engine
            for (int i = 0; i < MaxParticles; i++)
            {
                if (_particleTexts[i] != null && _particleTexts[i].gameObject.activeSelf)
                {
                    float alpha = Engine.MotionUiEasingEngine.Evaluate(ref _particleTransitions[i]);
                    if (alpha <= 0.01f)
                    {
                        _particleTexts[i].gameObject.SetActive(false);
                    }
                    else
                    {
                        var color = _particleTexts[i].color;
                        color.a = alpha;
                        _particleTexts[i].color = color;
                        
                        var pos = _particleRects[i].anchoredPosition;
                        pos.y += Time.deltaTime * 50f; 
                        _particleRects[i].anchoredPosition = pos;
                    }
                }
            }
        }

        private void SpawnParticle(int damage)
        {
            if (_particleTexts[_activeParticleIndex] != null)
            {
                _particleTexts[_activeParticleIndex].text = $"-{damage}";
                _particleTexts[_activeParticleIndex].gameObject.SetActive(true);
                
                var color = _particleTexts[_activeParticleIndex].color;
                color.a = 1.0f;
                _particleTexts[_activeParticleIndex].color = color;
                
                _particleRects[_activeParticleIndex].anchoredPosition = Vector2.zero;

                // Start transition from 1f to 0f over 1000ms
                Engine.MotionUiEasingEngine.StartTransition(ref _particleTransitions[_activeParticleIndex], 1f, 0f, 1000.0);
            }

            _activeParticleIndex = (_activeParticleIndex + 1) % MaxParticles;
        }
    }
}

using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Modul: classic CTA pulse - sine-cycles Target's alpha between 0.35
    // and 1.0 on a 1-second period while this component's GameObject is
    // active (UiTutorialController toggles that active state per step, not
    // this component's enabled flag directly). Restores the original alpha
    // on disable so a highlighted button does not stay dimmed after the
    // tutorial moves past it. Zero allocations - pure float math against an
    // already-existing Graphic each frame.
    public class UiTutorialHighlight : MonoBehaviour
    {
        public Graphic Target;

        private const float PulseMinAlpha = 0.35f;
        private const float PulseMaxAlpha = 1.0f;
        private const float PulsePeriodSeconds = 1.0f;

        private float _originalAlpha = 1f;
        private bool _hasCapturedOriginalAlpha;

        private void OnEnable()
        {
            if (Target == null) return;
            if (!_hasCapturedOriginalAlpha)
            {
                _originalAlpha = Target.color.a;
                _hasCapturedOriginalAlpha = true;
            }
        }

        private void OnDisable()
        {
            if (Target == null) return;
            Color restored = Target.color;
            restored.a = _originalAlpha;
            Target.color = restored;
        }

        private void Update()
        {
            if (Target == null) return;

            float phase = (Time.time % PulsePeriodSeconds) / PulsePeriodSeconds;
            float sineValue = Mathf.Sin(phase * Mathf.PI * 2f) * 0.5f + 0.5f;
            float alpha = Mathf.Lerp(PulseMinAlpha, PulseMaxAlpha, sineValue);

            Color current = Target.color;
            current.a = alpha;
            Target.color = current;
        }
    }
}

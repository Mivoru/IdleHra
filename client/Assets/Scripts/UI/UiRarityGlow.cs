using TMPro;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    // Modul: rarity glow, 2026-08-01.
    //
    // A pulsing outline on Epic and Legendary text. Driven per-frame because it
    // is an animation - but it writes through a MaterialPropertyBlock-style
    // per-instance material rather than mutating the shared TMP material asset,
    // which would tint every text in the game that shares it.
    //
    // Attached and configured by UiRarityPalette.ApplyAffixRarity; nothing
    // should add this component directly, so the "which rarities glow" rule
    // stays in one place.
    [DisallowMultipleComponent]
    public class UiRarityGlow : MonoBehaviour
    {
        public TMP_Text Target;
        public Color GlowColor = Color.white;

        [Tooltip("Full pulse cycles per second.")]
        public float PulseSpeed = 1.4f;

        public float MinGlowPower = 0.15f;
        public float MaxGlowPower = 0.55f;

        private Material _instanceMaterial;
        private static readonly int GlowColorId = Shader.PropertyToID("_GlowColor");
        private static readonly int GlowPowerId = Shader.PropertyToID("_GlowPower");
        private static readonly int GlowOuterId = Shader.PropertyToID("_GlowOuter");

        private void OnEnable()
        {
            if (Target == null) Target = GetComponent<TMP_Text>();
            EnsureInstanceMaterial();
        }

        private void OnDisable()
        {
            ResetToPlain();
        }

        private void OnDestroy()
        {
            // fontMaterial hands back a clone that this component owns, so it
            // has to be destroyed explicitly or every pooled row that ever
            // glowed leaks one material for the session.
            if (_instanceMaterial != null)
            {
                Destroy(_instanceMaterial);
                _instanceMaterial = null;
            }
        }

        private void EnsureInstanceMaterial()
        {
            if (Target == null) return;

            // TextMeshProUGUI.fontMaterial returns a per-instance clone the
            // first time it is read; fontSharedMaterial would be the global
            // asset, and writing to that tints every text using that font.
            _instanceMaterial = Target.fontMaterial;

            if (_instanceMaterial != null)
            {
                _instanceMaterial.EnableKeyword("GLOW_ON");
                _instanceMaterial.SetColor(GlowColorId, GlowColor);
                _instanceMaterial.SetFloat(GlowOuterId, 0.2f);
            }
        }

        private void Update()
        {
            if (_instanceMaterial == null) return;

            // 0..1 triangle wave, so the pulse eases at both ends rather than
            // snapping at the peak the way a raw sine's derivative does here.
            float phase = Mathf.PingPong(Time.unscaledTime * PulseSpeed, 1f);
            float power = Mathf.Lerp(MinGlowPower, MaxGlowPower, Mathf.SmoothStep(0f, 1f, phase));

            _instanceMaterial.SetColor(GlowColorId, GlowColor);
            _instanceMaterial.SetFloat(GlowPowerId, power);
        }

        // Returns the text to a non-glowing state without destroying the
        // component, so a pooled row can be rebound to a lower rarity.
        public void ResetToPlain()
        {
            if (_instanceMaterial == null) return;

            _instanceMaterial.SetFloat(GlowPowerId, 0f);
            _instanceMaterial.DisableKeyword("GLOW_ON");
        }
    }
}

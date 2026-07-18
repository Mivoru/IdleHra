using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Slides the hamburger menu panel in/out along X by lerping
    // anchoredPosition each frame - cheap per-frame float math against an
    // already-existing RectTransform, no allocations, matching this
    // codebase's established CTA-pulse convention (see UiTutorialHighlight).
    public class UiHamburgerMenuPanel : MonoBehaviour
    {
        private const float SlideDurationSeconds = 0.2f;

        public RectTransform PanelRect;
        public Button ToggleButton;
        public GameObject Blocker;
        public float HiddenPositionX = -420f;
        public float ShownPositionX = 0f;

        private bool _isOpen;
        private float _slideElapsed;
        private float _slideStartX;
        private float _slideTargetX;

        private void Awake()
        {
            if (ToggleButton != null)
            {
                ToggleButton.onClick.AddListener(Toggle);
            }

            if (PanelRect != null)
            {
                Vector2 position = PanelRect.anchoredPosition;
                position.x = HiddenPositionX;
                PanelRect.anchoredPosition = position;
            }

            if (Blocker != null)
            {
                Blocker.SetActive(false);
            }

            _slideElapsed = SlideDurationSeconds;
            _slideTargetX = HiddenPositionX;
        }

        public void Toggle()
        {
            SetOpen(!_isOpen);
        }

        public void Close()
        {
            SetOpen(false);
        }

        private void SetOpen(bool open)
        {
            if (_isOpen == open) return;
            _isOpen = open;

            if (Blocker != null)
            {
                Blocker.SetActive(open);
            }

            _slideStartX = PanelRect != null ? PanelRect.anchoredPosition.x : 0f;
            _slideTargetX = open ? ShownPositionX : HiddenPositionX;
            _slideElapsed = 0f;
        }

        private void Update()
        {
            if (PanelRect == null || _slideElapsed >= SlideDurationSeconds) return;

            _slideElapsed += Time.deltaTime;
            float fraction = Mathf.Clamp01(_slideElapsed / SlideDurationSeconds);
            float x = Mathf.Lerp(_slideStartX, _slideTargetX, fraction);

            Vector2 position = PanelRect.anchoredPosition;
            position.x = x;
            PanelRect.anchoredPosition = position;
        }
    }
}

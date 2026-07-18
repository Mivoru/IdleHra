using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.UI
{
    // Small runtime toggle for the persistent chat corner overlay - collapses
    // the message list/input row down to just the header bar so chat never
    // permanently blocks the map hub or sub-panels beneath it.
    public class UiChatMinimizePanel : MonoBehaviour
    {
        public GameObject ExpandedContent;
        public Button MinimizeToggleButton;
        public TMPro.TMP_Text ToggleButtonLabel;

        private bool _isMinimized;

        private void Awake()
        {
            if (MinimizeToggleButton != null)
            {
                MinimizeToggleButton.onClick.AddListener(Toggle);
            }
        }

        public void Toggle()
        {
            _isMinimized = !_isMinimized;

            if (ExpandedContent != null)
            {
                ExpandedContent.SetActive(!_isMinimized);
            }

            if (ToggleButtonLabel != null)
            {
                ToggleButtonLabel.text = _isMinimized ? "+" : "-";
            }
        }
    }
}

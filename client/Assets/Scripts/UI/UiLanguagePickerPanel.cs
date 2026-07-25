using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Play Mode audit fix. SwitchLanguage had a working zero-alloc
    // sender and a fully real server-side effect (ActiveLanguageState drives
    // every LocalizationMatrix.WriteToCharBuffer call across the UI, see
    // UiGuildWarPanel's own status-text lookup for an existing consumer),
    // but no button anywhere ever called it. Language ids are the 4
    // LocalizationMatrix recognizes (1=en, 2=cs, 3=de, 4=pl - see
    // LocalizationMatrixTests's own doc comment).
    public class UiLanguagePickerPanel : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 1f;

        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        [Header("Buttons")]
        public Button EnglishButton;
        public Button CzechButton;
        public Button GermanButton;
        public Button PolishButton;
        public GameObject EnglishActiveHighlight;
        public GameObject CzechActiveHighlight;
        public GameObject GermanActiveHighlight;
        public GameObject PolishActiveHighlight;

        private float _refreshAccumulatorSeconds;

        private void Awake()
        {
            if (EnglishButton != null) EnglishButton.onClick.AddListener(() => HandleLanguageClicked(1));
            if (CzechButton != null) CzechButton.onClick.AddListener(() => HandleLanguageClicked(2));
            if (GermanButton != null) GermanButton.onClick.AddListener(() => HandleLanguageClicked(3));
            if (PolishButton != null) PolishButton.onClick.AddListener(() => HandleLanguageClicked(4));
        }

        private void OnEnable()
        {
            _refreshAccumulatorSeconds = RefreshIntervalSeconds;
        }

        private void Update()
        {
            _refreshAccumulatorSeconds += Time.unscaledDeltaTime;
            if (_refreshAccumulatorSeconds < RefreshIntervalSeconds) return;
            _refreshAccumulatorSeconds = 0f;

            RefreshHighlights();
        }

        private void RefreshHighlights()
        {
            if (SyncProxy == null) return;

            byte active = SyncProxy.VisualActiveLanguageState;
            if (EnglishActiveHighlight != null) EnglishActiveHighlight.SetActive(active == 1);
            if (CzechActiveHighlight != null) CzechActiveHighlight.SetActive(active == 2);
            if (GermanActiveHighlight != null) GermanActiveHighlight.SetActive(active == 3);
            if (PolishActiveHighlight != null) PolishActiveHighlight.SetActive(active == 4);
        }

        private void HandleLanguageClicked(byte languageId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendLanguageSwitchCommandZeroAlloc(languageId);
            }
        }
    }
}

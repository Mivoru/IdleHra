using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: audio pipeline / settings. The audio and graphics half of the
    // Settings screen.
    //
    // Deliberately NOT the whole screen. Settings already hosts three working
    // components that own their own concerns and are wired elsewhere in
    // MainSceneBuilder.BuildSettingsWindow: UiLanguagePickerPanel (English /
    // Czech / German / Polish), UiAutoEatThresholdPanel, and the Log Off button
    // that UiLoginWindow claims as a post-pass persistent listener. Folding
    // those into this class would have meant rewriting three working things to
    // add two sliders and a quality picker.
    //
    // Everything here is client-local: none of it is server state, none of it
    // is validated, and none of it sends a command. That is why it persists to
    // PlayerPrefs rather than riding the wire - unlike the auto-eat threshold
    // directly above it on the same screen, which is authoritative server state
    // and must round-trip.
    public class UiSettingsPanel : MonoBehaviour
    {
        private const string QualityLevelKey = "FolkIdle.Graphics.QualityLevel";

        // Index order matches the three buttons below.
        public const int QualityLow = 0;
        public const int QualityMedium = 1;
        public const int QualityHigh = 2;
        public const int QualityOptionCount = 3;

        public Slider SfxVolumeSlider;
        public TextMeshProUGUI SfxVolumeLabel;
        public Slider MusicVolumeSlider;
        public TextMeshProUGUI MusicVolumeLabel;

        public Button[] QualityButtons = new Button[QualityOptionCount];
        public GameObject[] QualityActiveHighlights = new GameObject[QualityOptionCount];

        // "SFX: 100%" at its longest.
        private readonly char[] _sfxBuffer = new char[16];
        private readonly char[] _musicBuffer = new char[16];

        private int _selectedQuality = QualityMedium;

        private void Awake()
        {
            if (SfxVolumeSlider != null)
            {
                SfxVolumeSlider.minValue = 0f;
                SfxVolumeSlider.maxValue = 1f;
                SfxVolumeSlider.SetValueWithoutNotify(GameAudioDirector.SfxVolume);
                SfxVolumeSlider.onValueChanged.AddListener(HandleSfxVolumeChanged);
            }

            if (MusicVolumeSlider != null)
            {
                MusicVolumeSlider.minValue = 0f;
                MusicVolumeSlider.maxValue = 1f;
                MusicVolumeSlider.SetValueWithoutNotify(GameAudioDirector.MusicVolume);
                MusicVolumeSlider.onValueChanged.AddListener(HandleMusicVolumeChanged);
            }

            for (int i = 0; i < QualityOptionCount; i++)
            {
                if (QualityButtons == null || i >= QualityButtons.Length || QualityButtons[i] == null) continue;

                int captured = i;
                QualityButtons[i].onClick.AddListener(() => HandleQualitySelected(captured));
            }

            _selectedQuality = PlayerPrefs.GetInt(QualityLevelKey, QualityMedium);
            if (_selectedQuality < 0 || _selectedQuality >= QualityOptionCount)
            {
                _selectedQuality = QualityMedium;
            }

            ApplyQualityLevel(_selectedQuality);
            RefreshLabels();
            RefreshQualityHighlights();
        }

        private void OnEnable()
        {
            // The sliders are the only view of a value that another screen
            // cannot change, so re-reading on show is enough - no subscription.
            if (SfxVolumeSlider != null) SfxVolumeSlider.SetValueWithoutNotify(GameAudioDirector.SfxVolume);
            if (MusicVolumeSlider != null) MusicVolumeSlider.SetValueWithoutNotify(GameAudioDirector.MusicVolume);
            RefreshLabels();
            RefreshQualityHighlights();
        }

        private void HandleSfxVolumeChanged(float value)
        {
            GameAudioDirector.SetSfxVolume(value);
            RefreshLabels();

            // Immediate audible feedback for the slider being dragged - without
            // it the SFX slider is the one control in the game whose effect is
            // invisible until something unrelated happens to make a noise.
            GameAudioDirector.Play(GameSfx.UiButtonClick);
        }

        private void HandleMusicVolumeChanged(float value)
        {
            GameAudioDirector.SetMusicVolume(value);
            RefreshLabels();
        }

        private void HandleQualitySelected(int qualityIndex)
        {
            if (qualityIndex < 0 || qualityIndex >= QualityOptionCount) return;

            _selectedQuality = qualityIndex;
            PlayerPrefs.SetInt(QualityLevelKey, qualityIndex);
            ApplyQualityLevel(qualityIndex);
            RefreshQualityHighlights();
        }

        // Maps the three player-facing options onto whatever quality levels the
        // project actually defines, rather than assuming Unity's default six.
        // A project trimmed to two levels still gets a sensible Low/Medium/High,
        // and one with eight spreads across the full range.
        private static void ApplyQualityLevel(int qualityIndex)
        {
            int availableLevels = QualitySettings.names != null ? QualitySettings.names.Length : 0;
            if (availableLevels <= 0) return;

            int target;
            switch (qualityIndex)
            {
                case QualityLow: target = 0; break;
                case QualityHigh: target = availableLevels - 1; break;
                default: target = (availableLevels - 1) / 2; break;
            }

            // applyExpensiveChanges: false - this runs on a button press in a
            // live session, and the expensive path forces a synchronous
            // pipeline rebuild that stalls the frame.
            QualitySettings.SetQualityLevel(target, false);
        }

        private void RefreshLabels()
        {
            WritePercentLabel(SfxVolumeLabel, _sfxBuffer, "SFX: ", GameAudioDirector.SfxVolume);
            WritePercentLabel(MusicVolumeLabel, _musicBuffer, "Music: ", GameAudioDirector.MusicVolume);
        }

        private void RefreshQualityHighlights()
        {
            if (QualityActiveHighlights == null) return;

            for (int i = 0; i < QualityActiveHighlights.Length; i++)
            {
                if (QualityActiveHighlights[i] == null) continue;
                QualityActiveHighlights[i].SetActive(i == _selectedQuality);
            }
        }

        private static void WritePercentLabel(TextMeshProUGUI target, char[] buffer, string label, float normalizedValue)
        {
            if (target == null) return;

            int offset = 0;
            for (int i = 0; i < label.Length; i++)
            {
                buffer[offset++] = label[i];
            }

            int percent = Mathf.RoundToInt(Mathf.Clamp01(normalizedValue) * 100f);
            if (percent >= 100)
            {
                buffer[offset++] = '1';
                buffer[offset++] = '0';
                buffer[offset++] = '0';
            }
            else if (percent >= 10)
            {
                buffer[offset++] = (char)('0' + (percent / 10));
                buffer[offset++] = (char)('0' + (percent % 10));
            }
            else
            {
                buffer[offset++] = (char)('0' + percent);
            }

            buffer[offset++] = '%';
            target.SetCharArray(buffer, 0, offset);
        }
    }
}

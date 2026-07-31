using UnityEngine;

namespace FolkIdle.Client.Engine
{
    // Modul: audio pipeline. The named sound effects the game can raise.
    //
    // An enum rather than strings so every call site is compile-checked and the
    // lookup is an array index - no dictionary, no string hashing, nothing that
    // allocates on the paths this is called from (combat resolves at 10Hz).
    public enum GameSfx
    {
        UiButtonClick = 0,
        UiWindowOpen = 1,
        CombatPlayerHit = 2,
        CombatMonsterDefeated = 3,
        LootDropped = 4,
        LootRareDropped = 5,
        CraftingCompleted = 6,
        LevelUp = 7,
        RaceUnlocked = 8,
        Error = 9,
        Count = 10
    }

    // Modul: audio pipeline. The missing middle of this game's audio stack.
    //
    // Before this, the stack was two halves that never met: SfxPoolEngine owned
    // 16 pooled AudioSources but took the AudioClip as a PARAMETER, so it had no
    // idea what sounds exist and only two call sites in the entire client ever
    // passed it one (the guild raid and world boss panels). AmbientAudioEngine
    // could crossfade four music tracks, but nothing ever called RegisterTrack
    // to give it a track and nothing ever called Tick to advance a fade. There
    // was no clip registry, no volume control, and no Assets/Audio directory at
    // all - so combat, crafting, loot, level-ups and every UI button in the game
    // were silent with no code path that could have made them otherwise.
    //
    // This component is the registry and the trigger API. It deliberately
    // tolerates having no audio files whatsoever: clips resolve to null, Play
    // returns immediately, and nothing logs. That is the normal state of this
    // project today, and a missing clip must never be an error - the alternative
    // is a NullReferenceException per swing at 10Hz, or console spam that buries
    // real warnings. Drop a clip into Resources/Audio/ and it starts playing
    // with no code change.
    public class GameAudioDirector : MonoBehaviour
    {
        // Where a clip for each GameSfx is looked up, relative to any Resources
        // folder and without a file extension. Index order matches GameSfx.
        private static readonly string[] ClipResourcePaths =
        {
            "Audio/ui_button_click",
            "Audio/ui_window_open",
            "Audio/combat_player_hit",
            "Audio/combat_monster_defeated",
            "Audio/loot_dropped",
            "Audio/loot_rare_dropped",
            "Audio/crafting_completed",
            "Audio/level_up",
            "Audio/race_unlocked",
            "Audio/error"
        };

        private const string SfxVolumeKey = "FolkIdle.Audio.SfxVolume";
        private const string MusicVolumeKey = "FolkIdle.Audio.MusicVolume";
        private const float DefaultVolume = 0.7f;

        // The scene builder assigns this; the singleton below is what call sites
        // that have no inspector reference (combat, loot, crafting) go through.
        public SfxPoolEngine SfxEngine;

        public static GameAudioDirector Instance { get; private set; }

        private AudioClip[] _clips;
        private bool _clipsResolved;

        private static float _sfxVolume = DefaultVolume;
        private static float _musicVolume = DefaultVolume;

        public static float SfxVolume => _sfxVolume;
        public static float MusicVolume => _musicVolume;

        private void Awake()
        {
            // Last one built wins rather than destroying the newcomer: the scene
            // builder is idempotent and may rebuild Managers.
            Instance = this;

            _sfxVolume = PlayerPrefs.GetFloat(SfxVolumeKey, DefaultVolume);
            _musicVolume = PlayerPrefs.GetFloat(MusicVolumeKey, DefaultVolume);

            ResolveClips();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // Resolve every clip exactly once. Resources.Load returns null for a
        // path that does not exist, which is the graceful-degradation path
        // rather than an error case - see this class's own comment.
        private void ResolveClips()
        {
            if (_clipsResolved) return;

            _clips = new AudioClip[(int)GameSfx.Count];
            for (int i = 0; i < _clips.Length; i++)
            {
                _clips[i] = Resources.Load<AudioClip>(ClipResourcePaths[i]);
            }

            _clipsResolved = true;
        }

        private void Update()
        {
            // AmbientAudioEngine's crossfade needs a per-frame driver and never
            // had one, so a SetActiveTrack call would latch a transition that
            // never advanced. Cheap: Tick returns on the first line when no
            // transition is in flight.
            AmbientAudioEngine.Tick();
        }

        public static void SetSfxVolume(float volume)
        {
            _sfxVolume = Mathf.Clamp01(volume);
            PlayerPrefs.SetFloat(SfxVolumeKey, _sfxVolume);
        }

        public static void SetMusicVolume(float volume)
        {
            _musicVolume = Mathf.Clamp01(volume);
            PlayerPrefs.SetFloat(MusicVolumeKey, _musicVolume);
            AmbientAudioEngine.SetMasterMusicVolume(_musicVolume);
        }

        // The one entry point every gameplay call site uses. Safe to call before
        // the director exists, with no clip authored, and with the pool missing.
        public static void Play(GameSfx effect)
        {
            GameAudioDirector director = Instance;
            if (director == null) return;

            director.PlayInstance(effect);
        }

        private void PlayInstance(GameSfx effect)
        {
            if (_sfxVolume <= 0f) return;
            if (SfxEngine == null || _clips == null) return;

            int index = (int)effect;
            if (index < 0 || index >= _clips.Length) return;

            AudioClip clip = _clips[index];
            if (clip == null) return;

            SfxEngine.PlaySoundClip(clip, _sfxVolume, false);
        }
    }
}

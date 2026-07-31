using System;
using System.Diagnostics;
using UnityEngine;

namespace FolkIdle.Client.Engine
{
    public static class AmbientAudioEngine
    {
        private static AudioSource[] _tracks = new AudioSource[5];
        private static byte _activeTrackId = 1;
        private static long _transitionStartTimestamp;
        private static long _transitionDurationTicks;
        private static float[] _startVolumes = new float[5];
        private static float[] _targetVolumes = new float[5];

        // Modul: audio pipeline. The crossfade weight per track, 0..1, kept
        // separately from what is actually written to AudioSource.volume.
        //
        // The fade used to read its own start point back out of
        // _tracks[i].volume. Once the master music volume multiplies that value
        // on the way out, reading it back in would fold the master in a second
        // time on every subsequent fade - two fades at 50% volume would land at
        // 25%, three at 12.5%. Tracking the weight explicitly keeps the fade
        // arithmetic independent of how loud the player has set the music.
        private static float[] _currentWeights = new float[5];

        // The Settings music slider's scalar. Applied only at the point the
        // value is written to the AudioSource.
        private static float _masterVolume = 1.0f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _tracks = new AudioSource[5];
            _activeTrackId = 1;
            _transitionStartTimestamp = 0;
            _transitionDurationTicks = (long)(2.0 * Stopwatch.Frequency);
            _startVolumes = new float[5];
            _targetVolumes = new float[5];
            _currentWeights = new float[5];
            _currentWeights[_activeTrackId] = 1.0f;
            _masterVolume = 1.0f;
        }

        public static void SetMasterMusicVolume(float volume)
        {
            _masterVolume = volume < 0f ? 0f : (volume > 1f ? 1f : volume);

            // Re-apply immediately so the slider is audible while dragging,
            // from the current weights rather than the fade's endpoint - this
            // is therefore safe to call mid-crossfade.
            ApplyWeights();
        }

        private static void ApplyWeights()
        {
            for (int i = 1; i <= 4; i++)
            {
                if (_tracks[i] != null)
                {
                    _tracks[i].volume = _currentWeights[i] * _masterVolume;
                }
            }
        }

        public static void RegisterTrack(byte trackId, AudioSource source)
        {
            if (trackId >= 1 && trackId <= 4)
            {
                _tracks[trackId] = source;
                if (source != null)
                {
                    _currentWeights[trackId] = (trackId == _activeTrackId) ? 1.0f : 0.0f;
                    source.volume = _currentWeights[trackId] * _masterVolume;
                }
            }
        }

        public static void SetActiveTrack(byte trackId)
        {
            if (trackId < 1 || trackId > 4 || _activeTrackId == trackId) return;

            _activeTrackId = trackId;
            _transitionStartTimestamp = Stopwatch.GetTimestamp();
            
            for (int i = 1; i <= 4; i++)
            {
                if (_tracks[i] != null)
                {
                    _startVolumes[i] = _currentWeights[i];
                    _targetVolumes[i] = (i == _activeTrackId) ? 1.0f : 0.0f;
                }
            }
        }

        public static void Tick()
        {
            if (_transitionStartTimestamp == 0) return;

            long currentTimestamp = Stopwatch.GetTimestamp();
            long elapsed = currentTimestamp - _transitionStartTimestamp;

            float t = 1.0f;
            if (elapsed < _transitionDurationTicks && elapsed > 0)
            {
                t = (float)((double)elapsed / _transitionDurationTicks);
            }
            
            bool finished = t >= 1.0f;

            for (int i = 1; i <= 4; i++)
            {
                if (_tracks[i] != null)
                {
                    _currentWeights[i] = _startVolumes[i] + (_targetVolumes[i] - _startVolumes[i]) * t;
                }
            }
            ApplyWeights();

            if (finished)
            {
                _transitionStartTimestamp = 0;
            }
        }
    }
}

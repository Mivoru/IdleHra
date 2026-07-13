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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _tracks = new AudioSource[5];
            _activeTrackId = 1;
            _transitionStartTimestamp = 0;
            _transitionDurationTicks = (long)(2.0 * Stopwatch.Frequency);
            _startVolumes = new float[5];
            _targetVolumes = new float[5];
        }

        public static void RegisterTrack(byte trackId, AudioSource source)
        {
            if (trackId >= 1 && trackId <= 4)
            {
                _tracks[trackId] = source;
                if (source != null)
                {
                    source.volume = (trackId == _activeTrackId) ? 1.0f : 0.0f;
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
                    _startVolumes[i] = _tracks[i].volume;
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
                    float currentVol = _startVolumes[i] + (_targetVolumes[i] - _startVolumes[i]) * t;
                    _tracks[i].volume = currentVol;
                }
            }

            if (finished)
            {
                _transitionStartTimestamp = 0;
            }
        }
    }
}

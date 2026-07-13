using UnityEngine;

namespace FolkIdle.Client.Engine
{
    // Modul 19: fixed-size pool of reusable AudioSource components. Play/evict paths
    // never call AddComponent or Instantiate after Awake, so triggering sounds at
    // runtime allocates zero managed memory and creates zero new Unity objects.
    public class SfxPoolEngine : MonoBehaviour
    {
        private const int PoolSize = 16;
        private const int NoActiveSlot = -1;

        private AudioSource[] _audioSourcePool;
        private long[] _sourceStartTicks;
        private bool[] _fading;
        private float[] _fadeStartVolume;
        private long[] _fadeStartTicks;
        private long _fadeDurationTicks;

        private int _worldBossTrackSlot = NoActiveSlot;

        private void Awake()
        {
            _audioSourcePool = new AudioSource[PoolSize];
            _sourceStartTicks = new long[PoolSize];
            _fading = new bool[PoolSize];
            _fadeStartVolume = new float[PoolSize];
            _fadeStartTicks = new long[PoolSize];
            _fadeDurationTicks = System.Diagnostics.Stopwatch.Frequency;

            for (int i = 0; i < PoolSize; i++)
            {
                AudioSource source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                _audioSourcePool[i] = source;
            }
        }

        private void Update()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_fading[i])
                {
                    continue;
                }

                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - _fadeStartTicks[i];
                float t = elapsed >= _fadeDurationTicks
                    ? 1.0f
                    : (float)((double)elapsed / _fadeDurationTicks);

                AudioSource source = _audioSourcePool[i];
                source.volume = _fadeStartVolume[i] * (1.0f - t);

                if (t >= 1.0f)
                {
                    source.Stop();
                    _fading[i] = false;
                }
            }
        }

        public int PlaySoundClip(AudioClip clip, float volume, bool loop)
        {
            int slotIndex = FindIdleSourceIndex();
            if (slotIndex == NoActiveSlot)
            {
                slotIndex = FindOldestSourceIndex();
            }

            AudioSource source = _audioSourcePool[slotIndex];
            source.Stop();
            source.clip = clip;
            source.volume = volume;
            source.loop = loop;
            source.Play();

            _sourceStartTicks[slotIndex] = System.Diagnostics.Stopwatch.GetTimestamp();
            _fading[slotIndex] = false;

            return slotIndex;
        }

        public void StopSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= PoolSize)
            {
                return;
            }

            _fading[slotIndex] = false;
            _audioSourcePool[slotIndex].Stop();
        }

        public void FadeOutSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= PoolSize)
            {
                return;
            }

            _fadeStartVolume[slotIndex] = _audioSourcePool[slotIndex].volume;
            _fadeStartTicks[slotIndex] = System.Diagnostics.Stopwatch.GetTimestamp();
            _fading[slotIndex] = true;
        }

        public void PlayWorldBossCombatTrack(AudioClip clip, float volume)
        {
            _worldBossTrackSlot = PlaySoundClip(clip, volume, true);
        }

        public void StopWorldBossCombatTrack()
        {
            if (_worldBossTrackSlot == NoActiveSlot)
            {
                return;
            }

            FadeOutSlot(_worldBossTrackSlot);
            _worldBossTrackSlot = NoActiveSlot;
        }

        private int FindIdleSourceIndex()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                if (!_audioSourcePool[i].isPlaying)
                {
                    return i;
                }
            }

            return NoActiveSlot;
        }

        private int FindOldestSourceIndex()
        {
            int oldestIndex = 0;
            long oldestTicks = _sourceStartTicks[0];

            for (int i = 1; i < PoolSize; i++)
            {
                if (_sourceStartTicks[i] < oldestTicks)
                {
                    oldestTicks = _sourceStartTicks[i];
                    oldestIndex = i;
                }
            }

            return oldestIndex;
        }
    }
}

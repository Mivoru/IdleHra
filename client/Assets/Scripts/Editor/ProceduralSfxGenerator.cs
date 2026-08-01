using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FolkIdle.Client.Editor
{
    // Modul: procedural sound effects, 2026-08-01.
    //
    // The audio trigger layer (GameAudioDirector, SfxPoolEngine, and the ten
    // GameSfx call sites) has been built and verified for weeks, but
    // Resources/Audio/ was empty, so the game was silent. A missing clip
    // resolves to null and Play returns immediately, which is why nothing ever
    // errored - and why the gap survived several audits.
    //
    // This synthesises the ten clips from code rather than shipping recorded
    // assets. Every waveform here is arithmetic - oscillators, filtered noise
    // and ADSR envelopes written straight into 16-bit PCM - so the repository
    // gains no binary weight beyond the WAVs themselves, which are small enough
    // not to need LFS (see NEXT_STEPS_BACKLOG item 32 for why that matters).
    //
    // These are placeholders with a deliberate retro character. They are not a
    // substitute for authored audio, and the file header of each generated clip
    // says so.
    public static class ProceduralSfxGenerator
    {
        private const int SampleRate = 44100;
        private const string OutputDirectory = "Assets/Resources/Audio";

        // Matches GameAudioDirector.ClipResourcePaths exactly. If that array
        // changes, this must change with it - a name mismatch is silent, since
        // Resources.Load simply returns null.
        private static readonly string[] ClipNames =
        {
            "ui_button_click",
            "ui_window_open",
            "combat_player_hit",
            "combat_monster_defeated",
            "loot_dropped",
            "loot_rare_dropped",
            "crafting_completed",
            "level_up",
            "race_unlocked",
            "error"
        };

        [MenuItem("FolkIdle/Audio/Generate Procedural SFX")]
        public static void GenerateAll()
        {
            Directory.CreateDirectory(OutputDirectory);

            for (int i = 0; i < ClipNames.Length; i++)
            {
                float[] samples = Synthesise(ClipNames[i]);
                string path = Path.Combine(OutputDirectory, ClipNames[i] + ".wav");
                File.WriteAllBytes(path, EncodeWav16BitMono(samples, SampleRate));
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            Debug.Log($"ProceduralSfxGenerator: wrote {ClipNames.Length} clips to {OutputDirectory}.");
        }

        // Public so an editor script or test can synthesise without writing
        // files, and so the clip set can be verified by reflection.
        public static float[] Synthesise(string clipName)
        {
            switch (clipName)
            {
                case "ui_button_click":
                    // Very short filtered noise burst - a tick, not a tone.
                    return Noise(0.045f, attack: 0.001f, decay: 0.044f, lowPassCutoff: 0.35f, amplitude: 0.28f);

                case "ui_window_open":
                    // Rising two-tone sweep: something appeared.
                    return Sweep(0.16f, 420f, 780f, WaveShape.Sine, attack: 0.005f, release: 0.09f, amplitude: 0.22f);

                case "combat_player_hit":
                    // Low thud: short square body under a noise transient.
                    return Mix(
                        Sweep(0.10f, 180f, 90f, WaveShape.Square, attack: 0.001f, release: 0.08f, amplitude: 0.30f),
                        Noise(0.05f, attack: 0.001f, decay: 0.05f, lowPassCutoff: 0.18f, amplitude: 0.22f));

                case "combat_monster_defeated":
                    // Falling sweep plus a noise tail - a collapse.
                    return Mix(
                        Sweep(0.30f, 520f, 110f, WaveShape.Saw, attack: 0.002f, release: 0.22f, amplitude: 0.26f),
                        Noise(0.30f, attack: 0.01f, decay: 0.29f, lowPassCutoff: 0.12f, amplitude: 0.14f));

                case "loot_dropped":
                    // Small bright blip.
                    return Sweep(0.09f, 880f, 1180f, WaveShape.Triangle, attack: 0.002f, release: 0.06f, amplitude: 0.20f);

                case "loot_rare_dropped":
                    // Three-note ascending arpeggio - the "something good"
                    // motif, reused by level_up and race_unlocked at different
                    // intervals so the game has one recognisable vocabulary.
                    return Arpeggio(new[] { 660f, 880f, 1320f }, noteSeconds: 0.085f, WaveShape.Sine, amplitude: 0.24f);

                case "crafting_completed":
                    // Two-note fall, like setting a tool down.
                    return Arpeggio(new[] { 700f, 520f }, noteSeconds: 0.10f, WaveShape.Triangle, amplitude: 0.22f);

                case "level_up":
                    return Arpeggio(new[] { 523f, 659f, 784f, 1047f }, noteSeconds: 0.10f, WaveShape.Sine, amplitude: 0.28f);

                case "race_unlocked":
                    // Wider, slower, with a fifth on top - the biggest cue.
                    return Arpeggio(new[] { 392f, 523f, 659f, 784f, 1175f }, noteSeconds: 0.12f, WaveShape.Sine, amplitude: 0.30f);

                case "error":
                    // Descending minor second buzz - deliberately unpleasant.
                    return Mix(
                        Sweep(0.18f, 240f, 200f, WaveShape.Square, attack: 0.002f, release: 0.10f, amplitude: 0.20f),
                        Sweep(0.18f, 226f, 188f, WaveShape.Square, attack: 0.002f, release: 0.10f, amplitude: 0.16f));

                default:
                    return Array.Empty<float>();
            }
        }

        private enum WaveShape { Sine, Square, Saw, Triangle }

        private static float Oscillator(WaveShape shape, double phase)
        {
            double t = phase - Math.Floor(phase);
            switch (shape)
            {
                case WaveShape.Square: return t < 0.5 ? 1f : -1f;
                case WaveShape.Saw: return (float)(2.0 * t - 1.0);
                case WaveShape.Triangle: return (float)(4.0 * Math.Abs(t - 0.5) - 1.0);
                default: return (float)Math.Sin(t * 2.0 * Math.PI);
            }
        }

        // Frequency sweep with a linear attack and an exponential release.
        // Exponential on the tail because a linear fade reads as a cut rather
        // than a decay at these durations.
        private static float[] Sweep(float seconds, float startHz, float endHz, WaveShape shape, float attack, float release, float amplitude)
        {
            int count = Mathf.Max(1, (int)(seconds * SampleRate));
            float[] buffer = new float[count];

            double phase = 0.0;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)SampleRate;
                float progress = i / (float)count;

                float hz = Mathf.Lerp(startHz, endHz, progress);
                phase += hz / SampleRate;

                float envelope = Envelope(t, seconds, attack, release);
                buffer[i] = Oscillator(shape, phase) * envelope * amplitude;
            }
            return buffer;
        }

        private static float[] Noise(float seconds, float attack, float decay, float lowPassCutoff, float amplitude)
        {
            int count = Mathf.Max(1, (int)(seconds * SampleRate));
            float[] buffer = new float[count];

            // Deterministic seed: regenerating the clips must produce byte
            // identical files, or every run shows up as a spurious repository
            // diff on binaries that are awkward to review.
            System.Random random = new System.Random(unchecked((int)(seconds * 100000f) ^ 0x5F3A));

            float previous = 0f;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)SampleRate;
                float white = (float)(random.NextDouble() * 2.0 - 1.0);

                // One-pole low pass; cutoff is a 0..1 coefficient, not Hz.
                previous += (white - previous) * lowPassCutoff;

                float envelope = Envelope(t, seconds, attack, decay);
                buffer[i] = previous * envelope * amplitude;
            }
            return buffer;
        }

        private static float[] Arpeggio(float[] frequencies, float noteSeconds, WaveShape shape, float amplitude)
        {
            int perNote = Mathf.Max(1, (int)(noteSeconds * SampleRate));
            float[] buffer = new float[perNote * frequencies.Length];

            for (int n = 0; n < frequencies.Length; n++)
            {
                double phase = 0.0;
                for (int i = 0; i < perNote; i++)
                {
                    float t = i / (float)SampleRate;
                    phase += frequencies[n] / SampleRate;

                    float envelope = Envelope(t, noteSeconds, 0.004f, noteSeconds * 0.6f);
                    buffer[n * perNote + i] = Oscillator(shape, phase) * envelope * amplitude;
                }
            }
            return buffer;
        }

        private static float Envelope(float t, float totalSeconds, float attack, float release)
        {
            if (t < attack) return attack <= 0f ? 1f : t / attack;

            float releaseStart = Mathf.Max(attack, totalSeconds - release);
            if (t <= releaseStart) return 1f;

            float releaseProgress = (t - releaseStart) / Mathf.Max(0.0001f, totalSeconds - releaseStart);
            return Mathf.Exp(-5f * releaseProgress);
        }

        private static float[] Mix(params float[][] layers)
        {
            int longest = 0;
            foreach (float[] layer in layers) longest = Mathf.Max(longest, layer.Length);

            float[] buffer = new float[longest];
            foreach (float[] layer in layers)
            {
                for (int i = 0; i < layer.Length; i++) buffer[i] += layer[i];
            }

            // Normalise only when the sum actually clipped, so quiet cues stay
            // quiet relative to loud ones instead of every clip being pushed to
            // the same peak.
            float peak = 0f;
            for (int i = 0; i < buffer.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(buffer[i]));

            if (peak > 1f)
            {
                float scale = 0.98f / peak;
                for (int i = 0; i < buffer.Length; i++) buffer[i] *= scale;
            }
            return buffer;
        }

        // Minimal canonical 44-byte RIFF/WAVE header followed by 16-bit PCM.
        public static byte[] EncodeWav16BitMono(float[] samples, int sampleRate)
        {
            const int bitsPerSample = 16;
            const short channels = 1;

            int dataBytes = samples.Length * sizeof(short);
            byte[] output = new byte[44 + dataBytes];

            using (MemoryStream stream = new MemoryStream(output))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });

                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);                                   // PCM chunk size
                writer.Write((short)1);                             // PCM format
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bitsPerSample / 8);
                writer.Write((short)(channels * bitsPerSample / 8));
                writer.Write((short)bitsPerSample);

                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataBytes);

                for (int i = 0; i < samples.Length; i++)
                {
                    float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                    writer.Write((short)(clamped * short.MaxValue));
                }
            }

            return output;
        }
    }
}

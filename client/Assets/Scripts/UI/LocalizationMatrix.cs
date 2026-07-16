using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    public enum LocalizationKey : byte
    {
        ActiveEventPrefix = 0,
        EventNone = 1,
        EventGoldenHarvest = 2,
        EventBloodMoon = 3,
        EventMasterArtisan = 4,
        EventDiamondStar = 5,
        PushQueuePrefix = 6,
        BossHpPrefix = 7
    }

    // Modul: Production Release Hardening, Part 3. Previously a hardcoded
    // 8-key, 4-language stub - every translated string was a C# string
    // literal baked directly into Boot() below, so adding, correcting, or
    // extending any translation required a code change and a full client
    // rebuild. Now parses server/GameData/localizations.json (mirrored
    // verbatim into StreamingAssets/GameData, the same pattern
    // ClientContentRegistry already uses for monsters/items/gathering
    // nodes/skills) - content authors can add or edit translations without
    // touching code, and the same file is validated server-side at boot
    // (see ContentRegistry.Initialize's own localizations.json handling)
    // so a malformed or incomplete translation file fails loudly before it
    // ever ships.
    //
    // The zero-allocation guarantee is unchanged from the original design:
    // Boot() parses JSON and allocates the unmanaged lookup block exactly
    // once (a one-time startup cost, not a live-gameplay-loop cost); every
    // Lookup/WriteToCharBuffer call afterward only ever reads raw
    // unmanaged memory and copies into a caller-supplied buffer - no
    // managed heap allocation, matching the original struct-of-slots
    // design exactly, just populated from parsed JSON instead of string
    // literals.
    public static unsafe class LocalizationMatrix
    {
        private const int LanguageCount = 4;
        private const int SlotSizeChars = 64;
        private const int SlotSizeBytes = SlotSizeChars * 2;

        // Modul: no longer a compile-time constant - set once in Boot()
        // from the actual number of entries parsed out of
        // localizations.json, which is the entire point of removing the
        // hardcoded KeyCount = 8 constraint. Every existing LocalizationKey
        // enum member still maps to a fixed integer slot index (unchanged,
        // for zero-allocation lookups and so every existing call site
        // keeps compiling unmodified) - what is now dynamic is how many
        // slots actually exist and what text fills them, not the
        // addressing scheme itself.
        private static int _keyCount;

        private static IntPtr _matrixBlock;
        private static bool _booted;

        private sealed class LocalizationEntry
        {
            public string Key { get; set; } = string.Empty;
            public string En { get; set; } = string.Empty;
            public string Cs { get; set; } = string.Empty;
            public string De { get; set; } = string.Empty;
            public string Pl { get; set; } = string.Empty;
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void EditorCleanup()
        {
            Shutdown();
        }

        public static void Boot()
        {
            if (_booted)
            {
                return;
            }

            List<LocalizationEntry> entries = LoadEntries();
            _keyCount = Math.Max(entries.Count, 1);

            int totalBytes = LanguageCount * _keyCount * SlotSizeBytes;
            _matrixBlock = Marshal.AllocHGlobal(totalBytes);
            Unsafe.InitBlock((void*)_matrixBlock, 0, (uint)totalBytes);

            for (int i = 0; i < entries.Count; i++)
            {
                LocalizationEntry entry = entries[i];
                if (!Enum.TryParse(entry.Key, out LocalizationKey key))
                {
                    Debug.LogWarning($"LocalizationMatrix: localizations.json entry Key '{entry.Key}' does not match any known LocalizationKey - skipped.");
                    continue;
                }

                int keyIndex = (int)key;
                if (keyIndex < 0 || keyIndex >= _keyCount)
                {
                    continue;
                }

                // Modul: fallback-safe by construction - an empty
                // translation for a non-English language (should never
                // happen past ContentRegistry's server-side validation,
                // but this is the client's own last line of defense
                // against a stale or hand-edited StreamingAssets copy)
                // falls back to the English string for that same entry,
                // never to an empty slot.
                string en = string.IsNullOrEmpty(entry.En) ? string.Empty : entry.En;
                Load(0, keyIndex, en);
                Load(1, keyIndex, string.IsNullOrEmpty(entry.Cs) ? en : entry.Cs);
                Load(2, keyIndex, string.IsNullOrEmpty(entry.De) ? en : entry.De);
                Load(3, keyIndex, string.IsNullOrEmpty(entry.Pl) ? en : entry.Pl);
            }

            _booted = true;
        }

        // Modul: Windows/Editor/standalone StreamingAssets is a plain
        // filesystem path - matches ClientContentRegistry.LoadList's own
        // identical assumption and doc comment. A missing or malformed
        // file logs a warning and boots with zero entries rather than
        // throwing - unlike ClientContentRegistry's required gameplay
        // content files, missing translations should degrade to visibly
        // blank UI text, not crash the client on boot.
        private static List<LocalizationEntry> LoadEntries()
        {
            string path = Path.Combine(Application.streamingAssetsPath, "GameData", "localizations.json");
            if (!File.Exists(path))
            {
                Debug.LogWarning($"LocalizationMatrix: localizations.json not found at '{path}' - no translations loaded.");
                return new List<LocalizationEntry>();
            }

            string json = File.ReadAllText(path);
            try
            {
                return JsonSerializer.Deserialize<List<LocalizationEntry>>(json) ?? new List<LocalizationEntry>();
            }
            catch (JsonException ex)
            {
                Debug.LogWarning($"LocalizationMatrix: failed to parse localizations.json: {ex.Message} - no translations loaded.");
                return new List<LocalizationEntry>();
            }
        }

        public static void Lookup(byte languageId, LocalizationKey key, char* destBuffer, int maxTargetLength, out int charCount)
        {
            Boot();
            int langIndex = languageId <= 1 ? 0 : languageId - 1;
            if (langIndex < 0 || langIndex >= LanguageCount)
            {
                langIndex = 0;
            }

            int keyIndex = (int)key;
            if (keyIndex < 0 || keyIndex >= _keyCount)
            {
                charCount = 0;
                return;
            }

            char* slot = (char*)_matrixBlock + (langIndex * _keyCount + keyIndex) * SlotSizeChars;
            charCount = (int)slot[0];

            if (charCount > 0)
            {
                int copyCount = Math.Min(charCount, maxTargetLength);
                Unsafe.CopyBlock(destBuffer, slot + 1, (uint)(copyCount * 2));
            }
        }

        public static int WriteToCharBuffer(byte languageId, LocalizationKey key, char[] target, int offset)
        {
            fixed (char* targetPtr = target)
            {
                Lookup(languageId, key, targetPtr + offset, target.Length - offset, out int charCount);
                return offset + charCount;
            }
        }

        public static void Shutdown()
        {
            if (!_booted)
            {
                return;
            }

            Marshal.FreeHGlobal(_matrixBlock);
            _matrixBlock = IntPtr.Zero;
            _booted = false;
            _keyCount = 0;
        }

        private static void Load(int languageIndex, int keyIndex, string text)
        {
            int length = text.Length >= SlotSizeChars ? SlotSizeChars - 1 : text.Length;
            char* slot = (char*)_matrixBlock + (languageIndex * _keyCount + keyIndex) * SlotSizeChars;

            slot[0] = (char)length;
            for (int i = 0; i < length; i++)
            {
                slot[i + 1] = text[i];
            }
        }
    }
}

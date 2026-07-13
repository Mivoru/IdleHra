using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

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

    public static unsafe class LocalizationMatrix
    {
        private const int LanguageCount = 4;
        private const int KeyCount = 8;
        private const int SlotSizeChars = 64;
        private const int SlotSizeBytes = SlotSizeChars * 2;

        private static IntPtr _matrixBlock;
        private static bool _booted;

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

            int totalBytes = LanguageCount * KeyCount * SlotSizeBytes;
            _matrixBlock = Marshal.AllocHGlobal(totalBytes);
            Unsafe.InitBlock((void*)_matrixBlock, 0, (uint)totalBytes);

            LoadLanguage(0,
                "Active Event: ",
                "None",
                "Golden Harvest",
                "Blood Moon",
                "Master Artisan",
                "Diamond Star",
                "Push Queue: ",
                "Boss HP: ");

            LoadLanguage(1,
                "Aktivni event: ",
                "Zadny",
                "Zlata sklizen",
                "Krvavy mesic",
                "Mistr remesel",
                "Diamantova hvezda",
                "Push Fronta: ",
                "Boss HP: ");

            LoadLanguage(2,
                "Aktives Ereignis: ",
                "Keins",
                "Goldene Ernte",
                "Blutmond",
                "Meister Handwerk",
                "Diamantstern",
                "Push Warten: ",
                "Boss LP: ");

            LoadLanguage(3,
                "Aktywne wydarzenie: ",
                "Brak",
                "Zlote zniwa",
                "Krwawy ksiezyc",
                "Mistrz rzemiosla",
                "Diamentowa gwiazda",
                "Kolejka Push: ",
                "Boss PZ: ");

            _booted = true;
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
            if (keyIndex < 0 || keyIndex >= KeyCount)
            {
                keyIndex = 0;
            }

            char* slot = (char*)_matrixBlock + (langIndex * KeyCount + keyIndex) * SlotSizeChars;
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
        }

        private static void LoadLanguage(int languageIndex, string activeEventPrefix, string none, string goldenHarvest, string bloodMoon, string masterArtisan, string diamondStar, string pushQueuePrefix, string bossHpPrefix)
        {
            Load(languageIndex, LocalizationKey.ActiveEventPrefix, activeEventPrefix);
            Load(languageIndex, LocalizationKey.EventNone, none);
            Load(languageIndex, LocalizationKey.EventGoldenHarvest, goldenHarvest);
            Load(languageIndex, LocalizationKey.EventBloodMoon, bloodMoon);
            Load(languageIndex, LocalizationKey.EventMasterArtisan, masterArtisan);
            Load(languageIndex, LocalizationKey.EventDiamondStar, diamondStar);
            Load(languageIndex, LocalizationKey.PushQueuePrefix, pushQueuePrefix);
            Load(languageIndex, LocalizationKey.BossHpPrefix, bossHpPrefix);
        }

        private static void Load(int languageIndex, LocalizationKey key, string text)
        {
            int length = text.Length >= SlotSizeChars ? SlotSizeChars - 1 : text.Length;
            char* slot = (char*)_matrixBlock + (languageIndex * KeyCount + (int)key) * SlotSizeChars;
            
            slot[0] = (char)length;
            for (int i = 0; i < length; i++)
            {
                slot[i + 1] = text[i];
            }
        }
    }
}

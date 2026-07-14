using TMPro;
using UnityEngine;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul 16/21: character attribute + derived combat stat display. Event-driven
    // only - refreshes from VisualSyncProxy.OnCharacterStateUpdated, never from an
    // Update() loop.
    //
    // The derived combat stats (Melee/Ranged Damage, Crit Chance, Max HP) mirror
    // only the base STR/DEX/CON/LCK portion of StatsCalculator.Calculate's formula,
    // for preview purposes only - the same "client mirrors server formula for
    // display" pattern UiEquipmentRerollPanel uses for its reroll cost preview.
    // Equipped-gear/potion/race-mastery/age-phase bonuses are not included here
    // since the client does not have that data; the server remains the sole
    // source of truth for actual combat resolution.
    public class UiCharacterStatsPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Attributes")]
        public TextMeshProUGUI StrText;
        public TextMeshProUGUI DexText;
        public TextMeshProUGUI ConText;
        public TextMeshProUGUI LckText;

        [Header("Derived Combat Stats (Base Preview Only)")]
        public TextMeshProUGUI MeleeDamageText;
        public TextMeshProUGUI RangedDamageText;
        public TextMeshProUGUI CritChanceText;
        public TextMeshProUGUI MaxHpText;

        private readonly char[] _strBuffer = new char[16];
        private readonly char[] _dexBuffer = new char[16];
        private readonly char[] _conBuffer = new char[16];
        private readonly char[] _lckBuffer = new char[16];
        private readonly char[] _meleeBuffer = new char[16];
        private readonly char[] _rangedBuffer = new char[16];
        private readonly char[] _critBuffer = new char[16];
        private readonly char[] _maxHpBuffer = new char[16];

        private void OnEnable()
        {
            VisualSyncProxy.OnCharacterStateUpdated += HandleCharacterStateUpdated;
            RefreshDisplay();
        }

        private void OnDisable()
        {
            VisualSyncProxy.OnCharacterStateUpdated -= HandleCharacterStateUpdated;
        }

        private void HandleCharacterStateUpdated()
        {
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (SyncProxy == null) return;

            int str = SyncProxy.VisualSTR;
            int dex = SyncProxy.VisualDEX;
            int con = SyncProxy.VisualCON;
            int lck = SyncProxy.VisualLCK;

            if (StrText != null)
            {
                int offset = WriteIntToBuffer(_strBuffer, 0, str);
                StrText.SetCharArray(_strBuffer, 0, offset);
            }

            if (DexText != null)
            {
                int offset = WriteIntToBuffer(_dexBuffer, 0, dex);
                DexText.SetCharArray(_dexBuffer, 0, offset);
            }

            if (ConText != null)
            {
                int offset = WriteIntToBuffer(_conBuffer, 0, con);
                ConText.SetCharArray(_conBuffer, 0, offset);
            }

            if (LckText != null)
            {
                int offset = WriteIntToBuffer(_lckBuffer, 0, lck);
                LckText.SetCharArray(_lckBuffer, 0, offset);
            }

            // Base-formula preview only - see class comment.
            int meleeDamage = str * 2;
            int rangedDamage = dex * 2;
            float critChancePct = dex * 0.1f;
            int maxHp = con * 15;

            if (MeleeDamageText != null)
            {
                int offset = WriteIntToBuffer(_meleeBuffer, 0, meleeDamage);
                MeleeDamageText.SetCharArray(_meleeBuffer, 0, offset);
            }

            if (RangedDamageText != null)
            {
                int offset = WriteIntToBuffer(_rangedBuffer, 0, rangedDamage);
                RangedDamageText.SetCharArray(_rangedBuffer, 0, offset);
            }

            if (CritChanceText != null)
            {
                int offset = WriteFloatOneDecimalToBuffer(_critBuffer, 0, critChancePct);
                offset = WriteTextToBuffer(_critBuffer, offset, "%");
                CritChanceText.SetCharArray(_critBuffer, 0, offset);
            }

            if (MaxHpText != null)
            {
                int offset = WriteIntToBuffer(_maxHpBuffer, 0, maxHp);
                MaxHpText.SetCharArray(_maxHpBuffer, 0, offset);
            }
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteIntToBuffer(char[] buffer, int offset, int value)
        {
            if (value == 0)
            {
                buffer[offset++] = '0';
                return offset;
            }

            if (value < 0)
            {
                buffer[offset++] = '-';
                value = -value;
            }

            int temp = value;
            int length = 0;
            while (temp > 0)
            {
                temp /= 10;
                length++;
            }

            int endOffset = offset + length;
            temp = value;
            for (int i = endOffset - 1; i >= offset; i--)
            {
                buffer[i] = (char)('0' + (temp % 10));
                temp /= 10;
            }
            return endOffset;
        }

        // Formats a non-negative float to one decimal place using integer math
        // only (no string.Format/ToString allocations).
        private static int WriteFloatOneDecimalToBuffer(char[] buffer, int offset, float value)
        {
            if (value < 0f) value = 0f;

            int scaledTenths = (int)(value * 10f + 0.5f);
            int whole = scaledTenths / 10;
            int frac = scaledTenths % 10;

            offset = WriteIntToBuffer(buffer, offset, whole);
            buffer[offset++] = '.';
            buffer[offset++] = (char)('0' + frac);
            return offset;
        }
    }
}

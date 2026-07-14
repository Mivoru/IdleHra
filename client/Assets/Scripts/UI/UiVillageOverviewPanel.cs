using UnityEngine;
using TMPro;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul 16: Village passive economy HUD. Event-driven only - never polls
    // VisualSyncProxy from Update(). RefreshUI runs exclusively from the
    // OnVillageStateUpdated subscription, so the panel is fully idle on frames
    // where nothing changed.
    public class UiVillageOverviewPanel : MonoBehaviour
    {
        public VisualSyncProxy SyncProxy;

        [Header("Stock Text - Format: current / max (+rate/s)")]
        public TextMeshProUGUI WoodStockText;
        public TextMeshProUGUI StoneStockText;
        public TextMeshProUGUI IronStockText;

        [Header("Building Level Text")]
        public TextMeshProUGUI LumberjackLevelText;
        public TextMeshProUGUI QuarryLevelText;
        public TextMeshProUGUI MineLevelText;
        public TextMeshProUGUI WarehouseLevelText;

        [Header("Stock Color - Full Warehouse Indicator")]
        public Color NormalStockColor = Color.white;
        public Color FullStockColor = Color.red;

        private const long MaxStoragePerWarehouseLevel = 1000L;
        private const float WoodRatePerLevel = 0.1f;
        private const float StoneRatePerLevel = 0.08f;
        private const float IronRatePerLevel = 0.05f;

        private readonly char[] _woodStockBuffer = new char[48];
        private readonly char[] _stoneStockBuffer = new char[48];
        private readonly char[] _ironStockBuffer = new char[48];
        private readonly char[] _lumberjackLevelBuffer = new char[16];
        private readonly char[] _quarryLevelBuffer = new char[16];
        private readonly char[] _mineLevelBuffer = new char[16];
        private readonly char[] _warehouseLevelBuffer = new char[16];

        private void OnEnable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnVillageStateUpdated += RefreshUI;
            RefreshUI();
        }

        private void OnDisable()
        {
            if (SyncProxy == null) return;

            SyncProxy.OnVillageStateUpdated -= RefreshUI;
        }

        private void RefreshUI()
        {
            if (SyncProxy == null) return;

            int warehouseLevel = SyncProxy.WarehouseLevel;
            long maxStorage = (warehouseLevel <= 0 ? 1 : warehouseLevel) * MaxStoragePerWarehouseLevel;

            WriteStockText(WoodStockText, _woodStockBuffer, SyncProxy.WoodStock, maxStorage, SyncProxy.LumberjackLevel * WoodRatePerLevel);
            WriteStockText(StoneStockText, _stoneStockBuffer, SyncProxy.StoneStock, maxStorage, SyncProxy.QuarryLevel * StoneRatePerLevel);
            WriteStockText(IronStockText, _ironStockBuffer, SyncProxy.IronOreStock, maxStorage, SyncProxy.MineLevel * IronRatePerLevel);

            WriteLevelText(LumberjackLevelText, _lumberjackLevelBuffer, SyncProxy.LumberjackLevel);
            WriteLevelText(QuarryLevelText, _quarryLevelBuffer, SyncProxy.QuarryLevel);
            WriteLevelText(MineLevelText, _mineLevelBuffer, SyncProxy.MineLevel);
            WriteLevelText(WarehouseLevelText, _warehouseLevelBuffer, warehouseLevel);
        }

        private void WriteStockText(TextMeshProUGUI text, char[] buffer, long currentStock, long maxStorage, float ratePerSecond)
        {
            if (text == null) return;

            int offset = WriteLongToBuffer(buffer, 0, currentStock);
            offset = WriteTextToBuffer(buffer, offset, " / ");
            offset = WriteLongToBuffer(buffer, offset, maxStorage);
            offset = WriteTextToBuffer(buffer, offset, " (");
            offset = WriteRateToBuffer(buffer, offset, ratePerSecond);
            offset = WriteTextToBuffer(buffer, offset, "/s)");
            text.SetCharArray(buffer, 0, offset);

            text.color = currentStock >= maxStorage ? FullStockColor : NormalStockColor;
        }

        private static void WriteLevelText(TextMeshProUGUI text, char[] buffer, int level)
        {
            if (text == null) return;

            int offset = WriteTextToBuffer(buffer, 0, "Lv. ");
            offset = WriteIntToBuffer(buffer, offset, level);
            text.SetCharArray(buffer, 0, offset);
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
            return (int)WriteLongToBuffer(buffer, offset, value);
        }

        private static int WriteLongToBuffer(char[] buffer, int offset, long value)
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

            long temp = value;
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

        private static int WriteRateToBuffer(char[] buffer, int offset, float ratePerSecond)
        {
            int tenths = Mathf.RoundToInt(ratePerSecond * 10f);
            if (tenths < 0) tenths = 0;

            int whole = tenths / 10;
            int frac = tenths % 10;

            buffer[offset++] = '+';
            offset = WriteIntToBuffer(buffer, offset, whole);
            buffer[offset++] = '.';
            buffer[offset++] = (char)('0' + frac);
            return offset;
        }
    }
}

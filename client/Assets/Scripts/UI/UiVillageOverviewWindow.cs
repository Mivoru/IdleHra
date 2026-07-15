using System;
using UnityEngine;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 16: village building list + timed upgrade queue window. Distinct
    // from UiVillageOverviewPanel (which already covers the resource
    // generation grid - Lumberjack/Quarry/Mine rates and Warehouse fill,
    // driven by the same VisualSyncProxy.OnVillageStateUpdated event) - this
    // window is the genuinely new piece: per-building level rows with an
    // Upgrade button, and the ticking progress bar for whichever single
    // building is currently queued (VillageManagementEngine.
    // ExecuteUpgradeBuildingAsync enforces at most one upgrade in flight per
    // player at a time, across all buildings).
    public class UiVillageOverviewWindow : MonoBehaviour
    {
        // Modul 16: mirrors VillageManagementEngine's building ids exactly -
        // these are stable server constants, not derived from any packet.
        private const int ForgeBuildingId = 1;
        private const int InnBuildingId = 2;
        private const int BreedingGroundsBuildingId = 3;
        private const int MentorshipAcademyBuildingId = 4;
        private const int LumberjackBuildingId = 5;
        private const int QuarryBuildingId = 6;
        private const int MineBuildingId = 7;
        private const int WarehouseBuildingId = 8;

        private const long BaseUpgradeCost = 1000L;
        private const long BaseProductionUpgradeCost = 100L;
        private const long MinUpgradeDurationSeconds = 30L;

        public VisualSyncProxy SyncProxy;
        public WebSocketClient NetworkClient;

        [Header("Building Rows")]
        public UiVillageBuildingRow ForgeRow;
        public UiVillageBuildingRow InnRow;
        public UiVillageBuildingRow BreedingGroundsRow;
        public UiVillageBuildingRow MentorshipAcademyRow;
        public UiVillageBuildingRow LumberjackRow;
        public UiVillageBuildingRow QuarryRow;
        public UiVillageBuildingRow MineRow;
        public UiVillageBuildingRow WarehouseRow;

        private UiVillageBuildingRow[] _rows;
        private int _lastPendingBuildingId = -1;
        private long _pendingUpgradeTotalDurationSeconds;

        private void Awake()
        {
            _rows = new UiVillageBuildingRow[]
            {
                ForgeRow, InnRow, BreedingGroundsRow, MentorshipAcademyRow,
                LumberjackRow, QuarryRow, MineRow, WarehouseRow
            };

            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i] != null)
                {
                    _rows[i].Bind(HandleUpgradeClicked);
                }
            }
        }

        private void OnEnable()
        {
            if (SyncProxy != null)
            {
                SyncProxy.OnVillageStateUpdated += RefreshRows;
            }
            RefreshRows();
        }

        private void OnDisable()
        {
            if (SyncProxy != null)
            {
                SyncProxy.OnVillageStateUpdated -= RefreshRows;
            }
        }

        // Interpolates the ticking countdown/fill bar client-side between
        // packets, using only the server's PendingUpgradeCompletesAtEpoch -
        // the row-level state change (which building is pending, level text)
        // is still driven exclusively by RefreshRows via the event above.
        private void Update()
        {
            if (SyncProxy == null || SyncProxy.PendingUpgradeBuildingId == 0)
            {
                return;
            }

            UiVillageBuildingRow pendingRow = FindRow(SyncProxy.PendingUpgradeBuildingId);
            if (pendingRow == null) return;

            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long remaining = SyncProxy.PendingUpgradeCompletesAtEpoch - nowEpoch;
            pendingRow.TickRemaining(remaining);

            if (_pendingUpgradeTotalDurationSeconds > 0)
            {
                float elapsedFraction = 1f - Mathf.Clamp01((float)remaining / _pendingUpgradeTotalDurationSeconds);
                pendingRow.SetFillAmount(elapsedFraction);
            }
        }

        private void RefreshRows()
        {
            if (SyncProxy == null) return;

            SetRowLevel(ForgeRow, SyncProxy.VisualForgeLevel);
            SetRowLevel(InnRow, SyncProxy.VisualInnLevel);
            SetRowLevel(BreedingGroundsRow, SyncProxy.VisualBreedingLevel);
            SetRowLevel(MentorshipAcademyRow, SyncProxy.VisualAcademyLevel);
            SetRowLevel(LumberjackRow, SyncProxy.LumberjackLevel);
            SetRowLevel(QuarryRow, SyncProxy.QuarryLevel);
            SetRowLevel(MineRow, SyncProxy.MineLevel);
            SetRowLevel(WarehouseRow, SyncProxy.WarehouseLevel);

            int pendingBuildingId = SyncProxy.PendingUpgradeBuildingId;
            if (pendingBuildingId != _lastPendingBuildingId)
            {
                for (int i = 0; i < _rows.Length; i++)
                {
                    if (_rows[i] != null)
                    {
                        _rows[i].SetPending(_rows[i].BuildingId == pendingBuildingId);
                    }
                }

                _pendingUpgradeTotalDurationSeconds = pendingBuildingId != 0
                    ? EstimateUpgradeDurationSeconds(pendingBuildingId)
                    : 0L;

                _lastPendingBuildingId = pendingBuildingId;
            }
        }

        // Modul: best-effort mirror of VillageManagementEngine.
        // CalculateUpgradeCost/CalculateProductionUpgradeCost/
        // CalculateUpgradeDurationSeconds, used only to derive a fill-bar
        // fraction (the countdown text itself needs no total duration, only
        // the target epoch). CurrentLevel here is read from the row's
        // currently-displayed level, which is still the pre-upgrade level
        // while a request is pending.
        private long EstimateUpgradeDurationSeconds(int buildingId)
        {
            int currentLevel = GetCurrentLevel(buildingId);
            bool isProductionBuilding = buildingId >= LumberjackBuildingId && buildingId <= WarehouseBuildingId;

            double cost = isProductionBuilding
                ? BaseProductionUpgradeCost * Math.Pow(currentLevel + 1, 1.8)
                : BaseUpgradeCost * Math.Pow(1.5, currentLevel);

            long duration = (long)(cost / 10.0);
            return duration < MinUpgradeDurationSeconds ? MinUpgradeDurationSeconds : duration;
        }

        private int GetCurrentLevel(int buildingId)
        {
            switch (buildingId)
            {
                case ForgeBuildingId: return SyncProxy.VisualForgeLevel;
                case InnBuildingId: return SyncProxy.VisualInnLevel;
                case BreedingGroundsBuildingId: return SyncProxy.VisualBreedingLevel;
                case MentorshipAcademyBuildingId: return SyncProxy.VisualAcademyLevel;
                case LumberjackBuildingId: return SyncProxy.LumberjackLevel;
                case QuarryBuildingId: return SyncProxy.QuarryLevel;
                case MineBuildingId: return SyncProxy.MineLevel;
                case WarehouseBuildingId: return SyncProxy.WarehouseLevel;
                default: return 0;
            }
        }

        private static void SetRowLevel(UiVillageBuildingRow row, int level)
        {
            if (row != null)
            {
                row.SetLevel(level);
            }
        }

        private UiVillageBuildingRow FindRow(int buildingId)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i] != null && _rows[i].BuildingId == buildingId)
                {
                    return _rows[i];
                }
            }
            return null;
        }

        private void HandleUpgradeClicked(int buildingId)
        {
            UiVillageBuildingRow row = FindRow(buildingId);
            if (row != null)
            {
                row.LockOptimistically();
            }

            if (NetworkClient != null)
            {
                NetworkClient.SendVillageUpgradeCommandZeroAlloc((uint)buildingId);
            }
        }
    }
}

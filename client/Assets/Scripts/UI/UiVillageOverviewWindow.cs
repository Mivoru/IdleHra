using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
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

        // Modul: Play Mode audit fix. Mirrors server
        // VillageManagementEngine.TownHallBuildingId/CraftingWorkshopBuildingId
        // exactly - these two structural buildings existed server-side
        // (Town Hall gates every other building's max level at
        // 2 + TownHallLevel*2 and boosts passive gold; the Workshop boosts
        // crafting rarity odds) but had no client UI at all, so every
        // other building was permanently stuck at the level-2 ceiling with
        // no way to raise it.
        private const int TownHallBuildingId = 9;
        private const int CraftingWorkshopBuildingId = 10;

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
        public UiVillageBuildingRow TownHallRow;
        public UiVillageBuildingRow CraftingWorkshopRow;

        // Modul: tool upgrade. CommandType.UpgradeTool has been implemented and
        // validated server-side the whole time, and tool tier is one of the
        // larger multipliers in the game - GatheringToolEngine grants +10%
        // through +200% gathering speed across its ten tiers. Its only sender
        // was reachable solely from the dead UiCommandDispatcher, so no player
        // could ever upgrade a tool.
        public Button UpgradeToolButton;
        public TextMeshProUGUI ToolTierText;

        // Modul: villager roster. CommandType.EvictVillager was implemented and
        // validated server-side, and the client had no way to name a target:
        // the wire carries a population COUNT but never which slots are
        // occupied, so there was nothing to evict FROM. Slots are now read from
        // the player statistics snapshot.
        //
        // A fixed row set rather than a pooled list, matching this window's own
        // convention for the building rows - population is capped small by
        // VillageManagementEngine.CalculatePopulationCapacity, so a fixed
        // roster is simpler and cannot collapse the way a pooled list can.
        public const int VillagerRowCount = 12;
        public TextMeshProUGUI[] VillagerSlotTexts = new TextMeshProUGUI[VillagerRowCount];
        public Button[] VillagerEvictButtons = new Button[VillagerRowCount];
        public GameObject[] VillagerRowRoots = new GameObject[VillagerRowCount];

        // Modul: villager roster. Maps each on-screen row to the real
        // VillageResidents.SlotIndex it is showing; -1 means the row is unused.
        private readonly int[] _villagerSlotIndices = new int[VillagerRowCount];

        private UiVillageBuildingRow[] _rows;
        private int _lastPendingBuildingId = -1;
        private long _pendingUpgradeTotalDurationSeconds;

        private void Awake()
        {
            _rows = new UiVillageBuildingRow[]
            {
                ForgeRow, InnRow, BreedingGroundsRow, MentorshipAcademyRow,
                LumberjackRow, QuarryRow, MineRow, WarehouseRow,
                TownHallRow, CraftingWorkshopRow
            };

            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i] != null)
                {
                    _rows[i].Bind(HandleUpgradeClicked);
                }
            }

            if (UpgradeToolButton != null)
            {
                UpgradeToolButton.onClick.AddListener(HandleUpgradeToolClicked);
            }

            for (int rowIndex = 0; rowIndex < VillagerRowCount; rowIndex++)
            {
                if (VillagerEvictButtons == null || rowIndex >= VillagerEvictButtons.Length) break;
                if (VillagerEvictButtons[rowIndex] == null) continue;

                int capturedRow = rowIndex;
                VillagerEvictButtons[rowIndex].onClick.AddListener(() => HandleEvictClicked(capturedRow));
            }
        }

        // Modul: villager roster. Evicts by the villager's real SlotIndex, not
        // by the row it happens to occupy on screen - slots can be sparse once
        // anyone has been evicted, and sending the row index would evict the
        // wrong resident.
        private void HandleEvictClicked(int rowIndex)
        {
            if (NetworkClient == null) return;
            if (rowIndex < 0 || rowIndex >= _villagerSlotIndices.Length) return;

            int slotIndex = _villagerSlotIndices[rowIndex];
            if (slotIndex < 0) return;

            NetworkClient.SendVillagerEvictionCommandZeroAlloc((uint)slotIndex);

            // The eviction resolves off the tick thread against the database,
            // so re-pull rather than predicting the new roster locally.
            PlayerStatisticsCache.RequestSnapshot();
        }

        private void HandleStatisticsUpdated(PlayerStatisticsData data)
        {
            int occupiedCount = 0;

            if (data != null && data.Villagers != null)
            {
                for (int i = 0; i < data.Villagers.Count && occupiedCount < VillagerRowCount; i++)
                {
                    VillagerSlotData villager = data.Villagers[i];

                    _villagerSlotIndices[occupiedCount] = villager.SlotIndex;

                    if (VillagerRowRoots != null && occupiedCount < VillagerRowRoots.Length && VillagerRowRoots[occupiedCount] != null)
                    {
                        VillagerRowRoots[occupiedCount].SetActive(true);
                    }

                    if (VillagerSlotTexts != null && occupiedCount < VillagerSlotTexts.Length && VillagerSlotTexts[occupiedCount] != null)
                    {
                        VillagerSlotTexts[occupiedCount].text =
                            "Villager " + (villager.SlotIndex + 1)
                            + (villager.IsActive ? " - working" : " - idle")
                            + " (efficiency " + villager.EfficiencyModifier.ToString("F2") + ")";
                    }

                    occupiedCount++;
                }
            }

            // Hide the unused rows and clear their slot mapping, so a stale
            // index can never be sent from a row that is no longer shown.
            for (int rowIndex = occupiedCount; rowIndex < VillagerRowCount; rowIndex++)
            {
                _villagerSlotIndices[rowIndex] = -1;
                if (VillagerRowRoots != null && rowIndex < VillagerRowRoots.Length && VillagerRowRoots[rowIndex] != null)
                {
                    VillagerRowRoots[rowIndex].SetActive(false);
                }
            }
        }

        // Modul: tool upgrade. CommandType.UpgradeTool = 21. The server's
        // ExecuteUpgradeToolAsync takes only a player id - there is a single
        // account-wide tool tier that accelerates whichever gathering
        // profession is active, not a tier per tool - so the target id is 0.
        private void HandleUpgradeToolClicked()
        {
            if (NetworkClient == null) return;

            NetworkClient.SendUpgradeCommandZeroAlloc((byte)CommandType.UpgradeTool, 0);
        }

        private void OnEnable()
        {
            if (SyncProxy != null)
            {
                SyncProxy.OnVillageStateUpdated += RefreshRows;
            }

            // Modul: villager roster. Slots come from the REST snapshot, not
            // the tick stream, so they have to be pulled when the screen opens.
            PlayerStatisticsCache.OnStatisticsUpdated += HandleStatisticsUpdated;
            PlayerStatisticsCache.RequestSnapshot();

            RefreshRows();
        }

        private void OnDisable()
        {
            if (SyncProxy != null)
            {
                SyncProxy.OnVillageStateUpdated -= RefreshRows;
            }

            PlayerStatisticsCache.OnStatisticsUpdated -= HandleStatisticsUpdated;
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

        // Modul: tool upgrade. Display-only mirror of
        // GatheringToolEngine.GetToolSpeedBonusPct. The server stays
        // authoritative - this exists so the button can say what the next tier
        // is worth without inventing a second formula, and must be updated if
        // that table ever changes.
        private static int GetToolSpeedBonusPct(int toolTier)
        {
            switch (toolTier)
            {
                case 1: return 10;
                case 2: return 20;
                case 3: return 25;
                case 4: return 40;
                case 5: return 50;
                case 6: return 75;
                case 7: return 85;
                case 8: return 120;
                case 9: return 150;
                case 10: return 200;
                default: return 0;
            }
        }

        private void RefreshRows()
        {
            if (SyncProxy == null) return;

            // Modul: tool upgrade. Names the current tier and what it is worth,
            // because the speed bonus is otherwise invisible - it is applied
            // inside GatheringToolEngine's tick threshold with nothing on
            // screen attributing the change to the tool.
            if (ToolTierText != null)
            {
                int toolTier = SyncProxy.VisualCurrentToolTier;
                ToolTierText.text = toolTier <= 0
                    ? "Tools: none. Upgrading grants a permanent gathering speed bonus."
                    : "Tools: tier " + toolTier + " (+" + GetToolSpeedBonusPct(toolTier) + "% gathering speed)";
            }

            SetRowLevel(ForgeRow, SyncProxy.VisualForgeLevel);
            SetRowLevel(InnRow, SyncProxy.VisualInnLevel);
            SetRowLevel(BreedingGroundsRow, SyncProxy.VisualBreedingLevel);
            SetRowLevel(MentorshipAcademyRow, SyncProxy.VisualAcademyLevel);
            SetRowLevel(LumberjackRow, SyncProxy.LumberjackLevel);
            SetRowLevel(QuarryRow, SyncProxy.QuarryLevel);
            SetRowLevel(MineRow, SyncProxy.MineLevel);
            SetRowLevel(WarehouseRow, SyncProxy.WarehouseLevel);
            SetRowLevel(TownHallRow, SyncProxy.TownHallLevel);
            SetRowLevel(CraftingWorkshopRow, SyncProxy.CraftingWorkshopLevel);

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

            // Modul: Play Mode audit fix. Mirrors VillageManagementEngine.
            // ExecuteUpgradeBuildingAsync exactly - Town Hall/Crafting
            // Workshop are "structural" buildings that use the same
            // CalculateProductionUpgradeCost formula as the four passive-
            // production buildings (just consuming different materials),
            // not CalculateUpgradeCost.
            bool usesProductionCostCurve = (buildingId >= LumberjackBuildingId && buildingId <= WarehouseBuildingId) ||
                buildingId == TownHallBuildingId || buildingId == CraftingWorkshopBuildingId;

            double cost = usesProductionCostCurve
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
                case TownHallBuildingId: return SyncProxy.TownHallLevel;
                case CraftingWorkshopBuildingId: return SyncProxy.CraftingWorkshopLevel;
                default: return 0;
            }
        }

        private void SetRowLevel(UiVillageBuildingRow row, int level)
        {
            if (row == null) return;

            row.SetLevel(level);

            // Modul: UI rework. The Town Hall ceiling
            // (VillageManagementEngine.ResolveMaxBuildingLevel: 2 +
            // TownHallLevel * 2) is the single most confusing thing about
            // this screen - every building silently stops accepting
            // upgrades at level 2 on a fresh account and nothing anywhere
            // explained why. The Town Hall itself is capped by its own
            // level, same as everything else.
            int maxLevel = 2 + (SyncProxy != null ? SyncProxy.TownHallLevel : 0) * 2;
            row.SetUpgradeCost(level, maxLevel);
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

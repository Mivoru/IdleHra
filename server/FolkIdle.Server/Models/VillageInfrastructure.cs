using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    [Table("VillageInfrastructures")]
    public class VillageInfrastructure
    {
        public long PlayerId { get; set; }
        public int BuildingId { get; set; }
        public int CurrentLevel { get; set; }

        // Modul 16: timed upgrade queue. UpgradeTargetLevel == 0 means no
        // upgrade is pending for this building - CurrentLevel does not become
        // UpgradeTargetLevel until VillageManagementEngine.ResolveMaturedUpgradesAsync
        // observes UpgradeCompletesAtEpoch has passed (checked lazily on the
        // next upgrade request or login, plus live in SimulationEngine's tick
        // loop for players already online - see TickStatePayload.PendingUpgradeBuildingId).
        public int UpgradeTargetLevel { get; set; }
        public long UpgradeCompletesAtEpoch { get; set; }
    }
}

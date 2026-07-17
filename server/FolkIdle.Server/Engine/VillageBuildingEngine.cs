using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public sealed class VillageBuildingEngine
    {
        private readonly VillageManagementEngine _managementEngine;

        public VillageBuildingEngine(System.IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _managementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
        }

        public Task ExecuteUpgradeBuildingAsync(long playerId, int buildingType)
        {
            uint mappedBuilding = buildingType switch
            {
                1 => VillageManagementEngine.ForgeBuildingId,
                2 => VillageManagementEngine.InnBuildingId,
                3 => VillageManagementEngine.BreedingGroundsBuildingId,
                4 => VillageManagementEngine.MentorshipAcademyBuildingId,
                _ => 0U
            };

            return mappedBuilding == 0U
                ? Task.CompletedTask
                : _managementEngine.ExecuteUpgradeBuildingAsync(playerId, mappedBuilding);
        }

        public Task ExecuteUpgradeToolAsync(long playerId)
        {
            return Task.CompletedTask;
        }
    }
}

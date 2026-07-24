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

        public Task ExecuteUpgradeToolAsync(long playerId)
        {
            return Task.CompletedTask;
        }
    }
}

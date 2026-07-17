using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public class TimeBankService
    {
        private readonly SimulationEngine _simulationEngine;
        private readonly StateCheckpointManager _checkpointManager;
        private const int TickIntervalMs = 100; // 10 Hz

        public TimeBankService(SimulationEngine simulationEngine, StateCheckpointManager checkpointManager)
        {
            _simulationEngine = simulationEngine;
            _checkpointManager = checkpointManager;
        }

        public void ProcessOfflineDrift(long playerId, DateTime lastLogout, DateTime currentLogin)
        {
            TimeSpan delta = currentLogin - lastLogout;
            if (delta.TotalMilliseconds <= 0)
            {
                return;
            }

            long totalTicks = (long)(delta.TotalMilliseconds / TickIntervalMs);

            TickStatePayload payload = new TickStatePayload
            {
                PlayerId = playerId,
                ActiveActivityId = 1,
                CurrentProgressTicks = 0,
                RequiredProgressTicks = 50,
                InventorySpaceRemaining = 20,
                IsDirty = false,
                TicksSinceLastFlush = 0
            };

            for (long i = 0; i < totalTicks; i++)
            {
                _simulationEngine.ProcessTick(ref payload);
                _checkpointManager.TrackState(ref payload);

                if (payload.InventorySpaceRemaining <= 0)
                {
                    break;
                }
            }
        }
    }
}

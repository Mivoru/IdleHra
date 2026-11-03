using System.Collections.Generic;
using System.Threading;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// Under lockstep the tick thread is parked while the bots think, and a few
    /// REST routes wait for the TICK to answer them: payload snapshots (the
    /// what-if and hunting projections, the skill tree preview, the dungeon
    /// run), world boss strikes and rebirth requests - every route that awaits a
    /// tick-set Completion (2026-11-03). Unserved, a snapshot timed out as
    /// NoSession after 3 wall seconds and a rebirth would have waited for ever
    /// (deadlock: the REST call waits for the tick, the tick waits for the bot,
    /// the bot waits for the REST call). The parked tick calls this between
    /// yields: the same drains, in the same order, under the same lock the tick
    /// holds. Every other REST-to-tick queue is a notification nobody waits on.
    /// </summary>
    internal static class LockstepRestService
    {
        internal static bool HasWaitingCallers(PlayerSessionRegistry registry)
            => !registry.PayloadSnapshotQueue.IsEmpty || !registry.WorldBossStrikeQueue.IsEmpty || !registry.RebirthRequestQueue.IsEmpty;

        internal static void Serve(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            WorldBossEngine? worldBossEngine,
            StateCheckpointManager checkpointManager)
        {
            if (!HasWaitingCallers(registry)) return;
            Monitor.Enter(activePlayers);
            try
            {
                if (worldBossEngine != null) WorldBossTickCoordinator.DrainStrikeOrders(registry, activePlayers, worldBossEngine);
                PayloadSnapshotTickCoordinator.Drain(registry, activePlayers);
                // A rebirth is taken by the tick (suspend + flush); the writer's
                // continuation answers the caller, so the REST call completes
                // while the bot still holds its turn, and the reload lands on the
                // next tick as it would paced.
                RebirthTickCoordinator.Drain(registry, activePlayers, checkpointManager);
            }
            finally
            {
                Monitor.Exit(activePlayers);
            }
        }
    }
}

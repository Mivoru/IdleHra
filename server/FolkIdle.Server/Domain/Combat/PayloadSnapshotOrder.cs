using System.Collections.Generic;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// A read-only copy of one player's live payload, taken ON the tick thread
    /// for a REST handler that has to compute from it (task 78, the hunting
    /// advisor).
    /// </summary>
    /// <remarks>
    /// Modul: A COPY, TAKEN WHERE THE PAYLOAD LIVES. The payload is a struct
    /// the tick owns and mutates ten times a second, so a handler reading it
    /// across threads would read a torn value. The tick answers with a copy
    /// between two ticks, the same hand-off WorldBossStrikeOrder uses, and the
    /// handler does its work off the tick. No session answers null, so the
    /// handler can say so instead of guessing.
    /// </remarks>
    public sealed class PayloadSnapshotOrder
    {
        public required long PlayerId { get; init; }

        public TaskCompletionSource<PayloadSnapshot?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The copy, with the two world-wide terms the XP of a kill depends on.</summary>
    public readonly record struct PayloadSnapshot(TickStatePayload Payload, int GlobalXpMultiplier, int ActiveGlobalEventId);

    internal static class PayloadSnapshotTickCoordinator
    {
        internal static void Drain(PlayerSessionRegistry registry, Dictionary<long, TickStatePayload> activePlayers)
        {
            int budget = registry.PayloadSnapshotQueue.Count;
            for (int taken = 0; taken < budget && registry.PayloadSnapshotQueue.TryDequeue(out var order); taken++)
            {
                if (activePlayers.TryGetValue(order.PlayerId, out var payload))
                {
                    order.Completion.TrySetResult(new PayloadSnapshot(
                        payload, GlobalEngineState.GlobalXpMultiplier, SimulationEngine.ActiveGlobalEventId));
                }
                else
                {
                    order.Completion.TrySetResult(null);
                }
            }
        }
    }
}

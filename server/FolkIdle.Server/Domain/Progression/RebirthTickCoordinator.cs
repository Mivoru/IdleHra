using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// A rebirth asked for over REST, waiting for the tick. Completion is
    /// set to the engine's outcome, or to null when the player has no live
    /// payload - the REST handler then runs the rebirth itself.
    /// </summary>
    public sealed class RebirthRequest
    {
        public long PlayerId { get; init; }
        public int ExpectedRebirthCount { get; init; }
        public RebirthEngine Engine { get; init; } = null!;
        public TaskCompletionSource<RebirthOutcome?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Task 88: the tick's half of a rebirth. The live payload is the old
    /// life and the tick is its only owner, so the rebirth cannot simply write
    /// the database under it - the next checkpoint would write level 96 back
    /// over the reset. The order, all on the existing checkpoint machinery:
    ///
    ///   1. suspend the payload and mark it RebirthPending;
    ///   2. RequestFlush(Command) - everything the session earned (codex,
    ///      deeds, diamonds, the shards' inputs) reaches its rows first;
    ///   3. in the flush's continuation, on the writer: RebirthEngine, then
    ///      LoadPlayerState, then StateReloadQueue - exactly the ReloadState
    ///      shape, so the reborn payload lands through the one drain that
    ///      already knows how to replace a live payload.
    ///
    /// Never FlushStateAndAdvance on the tick (CheckpointOffTickGuardTests).
    /// </summary>
    internal static class RebirthTickCoordinator
    {
        // Requests whose flush is in flight, so a FAILED flush (whose
        // continuation never runs) can still answer the waiting POST.
        private static readonly ConcurrentDictionary<long, RebirthRequest> Pending = new();

        internal static void Drain(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            StateCheckpointManager checkpointManager)
        {
            int budget = registry.RebirthRequestQueue.Count;
            for (int i = 0; i < budget && registry.RebirthRequestQueue.TryDequeue(out var request); i++)
            {
                ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, request.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                {
                    // No live session: nothing on the tick to protect.
                    request.Completion.TrySetResult(null);
                    continue;
                }

                if (payload.RebirthPending || !Pending.TryAdd(request.PlayerId, request))
                {
                    request.Completion.TrySetResult(new RebirthOutcome(RebirthResult.InFlight, request.ExpectedRebirthCount, payload.RenownedRebirths, 0, 0, false));
                    continue;
                }

                payload.IsSuspended = true;
                payload.RebirthPending = true;

                long playerId = request.PlayerId;
                var checkpoints = checkpointManager;
                checkpointManager.RequestFlush(ref payload, FlushReason.Command, then: async () =>
                {
                    RebirthOutcome outcome = new(RebirthResult.Failed, request.ExpectedRebirthCount, 0, 0, 0, false);
                    try
                    {
                        outcome = await request.Engine.RebirthAsync(playerId, request.ExpectedRebirthCount);

                        // Reloaded whatever the outcome: even a refused rebirth
                        // has to un-suspend the player, and the drain of this
                        // payload is what clears RebirthPending.
                        var reloaded = await checkpoints.LoadPlayerState(playerId);
                        reloaded.IsSuspended = false;
                        reloaded.RebirthPending = false;
                        registry.StateReloadQueue.Enqueue(reloaded);
                    }
                    finally
                    {
                        Pending.TryRemove(playerId, out _);
                        request.Completion.TrySetResult(outcome);
                    }
                });
            }
        }

        /// <summary>The rebirth's flush failed; its continuation will never run.</summary>
        internal static void FailPending(long playerId)
        {
            if (Pending.TryRemove(playerId, out var request))
            {
                request.Completion.TrySetResult(new RebirthOutcome(RebirthResult.Failed, request.ExpectedRebirthCount, 0, 0, 0, false));
            }
        }
    }
}

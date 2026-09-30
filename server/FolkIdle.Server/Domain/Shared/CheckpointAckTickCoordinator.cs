using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// What applying one ack asks the caller to do next.
    /// </summary>
    internal enum FlushAckOutcome : byte
    {
        Committed = 0,
        // A periodic or logout flush failed; the payload is re-armed to retry.
        Failed = 1,
        // A command's flush failed: its work never ran, the player was
        // un-suspended here, and the caller owes them a result code.
        CommandFailed = 2
    }

    /// <summary>
    /// The tick thread's half of CheckpointWriter: applies each FlushAck to the
    /// live payload it belongs to.
    ///
    /// Modul: a COORDINATOR, not an engine - static, no fields, called on the
    /// 10 Hz tick thread, which is the only thread allowed to touch a payload.
    /// Drained at the TOP of the tick, ahead of StateReloadQueue, so a reload's
    /// fresh payload is never installed before the ack of the flush that
    /// preceded it (CheckpointWriter posts the ack before it runs the reload).
    /// </summary>
    internal static class CheckpointAckTickCoordinator
    {
        internal static void Drain(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers,
            StateCheckpointManager checkpointManager)
        {
            // Budgeted: acks keep arriving while this runs, and a drain that
            // only ends when the writer pauses is the starvation shape
            // CLAUDE.md records for CombatLootEngine.
            int budget = registry.FlushAckQueue.Count;
            for (int i = 0; i < budget && registry.FlushAckQueue.TryDequeue(out var ack); i++)
            {
                ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, ack.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                {
                    // Modul: THE PLAYER LEFT WHILE THEIR FLUSH WAS IN FLIGHT.
                    // A logout snapshots with a zero delta when an earlier
                    // periodic flush already carried the coins - so if that
                    // earlier one fails, its delta has no payload to return
                    // to. It is banked on its own instead (never on a
                    // split-brain: another session simulated these coins too).
                    if (!ack.Committed && !ack.SplitBrain && ack.GoldDelta != 0L)
                    {
                        checkpointManager.RequestGoldRescue(ack.PlayerId, ack.GoldDelta);
                    }
                    else if (!ack.Committed && ack.SplitBrain && ack.GoldDelta != 0L)
                    {
                        Console.WriteLine($"{CheckpointWriter.DeadLetterPrefix} player={ack.PlayerId} gold_delta={ack.GoldDelta} reason={ack.Reason} detail=\"split-brain, player gone\"");
                    }
                    if (!ack.Committed && (ack.WoodDelta != 0L || ack.StoneDelta != 0L || ack.IronDelta != 0L))
                    {
                        // Village production has no rescue job; say so rather
                        // than drop it in silence.
                        Console.WriteLine($"{CheckpointWriter.DeadLetterPrefix} player={ack.PlayerId} gold_delta=0 reason={ack.Reason} detail=\"player gone; village production lost: wood={ack.WoodDelta} stone={ack.StoneDelta} iron_ore={ack.IronDelta}\"");
                    }
                    continue;
                }

                bool rebirthWasPending = payload.RebirthPending;
                if (Apply(ref payload, in ack) == FlushAckOutcome.CommandFailed)
                {
                    // Modul: SILENT ROLLBACK IS THIS SERVER'S FAVOURITE WAY TO
                    // LIE. The command's engine work never ran; without this
                    // the button did nothing and said nothing.
                    registry.EnqueueCommandResult(ack.PlayerId, (byte)Network.CommandResultCode.CheckpointFailed);

                    // Task 88: the flush a rebirth was waiting on failed, so
                    // its continuation never runs. The player plays on
                    // un-reborn (Apply un-suspended them) and the waiting
                    // request is answered rather than left to time out.
                    if (rebirthWasPending)
                    {
                        payload.RebirthPending = false;
                        Progression.RebirthTickCoordinator.FailPending(ack.PlayerId);
                    }
                }
            }
        }

        internal static FlushAckOutcome Apply(ref TickStatePayload payload, in FlushAck ack)
        {
            if (payload.FlushesInFlight > 0)
            {
                payload.FlushesInFlight--;
            }

            if (ack.Committed)
            {
                // The database is now at CommittedDbEpoch; never step backwards
                // (a later flush's ack may already have been applied by a
                // reload that read a newer row).
                if (ack.CommittedDbEpoch > payload.LogicEpochCounter)
                {
                    payload.LogicEpochCounter = ack.CommittedDbEpoch;
                }
                return FlushAckOutcome.Committed;
            }

            // Never lose coins: the delta the job carried comes back, and the
            // payload is re-armed so the next TrackState retries at once, the
            // way a failed synchronous flush always did. CurrentGold already
            // includes these coins - only the "still owed to the database"
            // counter moves.
            // GoldLedger: coins handed back by a failed flush - owed again,
            // not new income. Their tally comes back beside them, uncounted.
            payload.RedisPendingGoldDelta += ack.GoldDelta;
            payload.PendingGoldIncome.Add(in ack.Income);
            // Village production likewise: still owed to CommodityRecords, and
            // already counted in the Cached*Stock the screen shows.
            payload.PendingWoodDelta += ack.WoodDelta;
            payload.PendingStoneDelta += ack.StoneDelta;
            payload.PendingIronDelta += ack.IronDelta;
            payload.IsDirty = true;
            if (payload.TicksSinceLastFlush < StateCheckpointManager.CheckpointBoundaryTicks)
            {
                payload.TicksSinceLastFlush = StateCheckpointManager.CheckpointBoundaryTicks;
            }

            if (ack.Reason == FlushReason.Command)
            {
                payload.IsSuspended = false;
                return FlushAckOutcome.CommandFailed;
            }

            return FlushAckOutcome.Failed;
        }
    }
}

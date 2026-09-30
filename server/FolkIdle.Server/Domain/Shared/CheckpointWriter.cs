using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// Why a checkpoint was asked for. The tick side answers a failed flush
    /// differently for each: a periodic one is simply retried, a command's
    /// player is un-suspended and told, a reload still reloads, and a logout
    /// is retried by the writer itself because nobody is left to retry it.
    /// </summary>
    public enum FlushReason : byte
    {
        Periodic = 0,
        Command = 1,
        Reload = 2,
        Logout = 3
    }

    internal enum FlushJobKind : byte
    {
        Flush = 0,
        GoldRescue = 1,
        Fence = 2
    }

    /// <summary>
    /// One unit of work for the writer. A class, not a struct: the snapshot
    /// is a whole TickStatePayload, and a class is copied into the channel
    /// once rather than at every hand-off.
    /// </summary>
    internal sealed class FlushJob
    {
        public FlushJobKind Kind;
        public long PlayerId;
        public FlushReason Reason;

        // The payload as it stood when the flush was requested, with its epoch
        // already advanced past every flush still in flight (see
        // StateCheckpointManager.RequestFlush).
        public TickStatePayload Snapshot;

        // The unbanked gold this job carries - moved OFF the live payload at
        // request time. Exactly one of three things happens to it: the flush
        // banks it, the ack hands it back to the live payload, or a rescue
        // banks it on its own / dead-letters it.
        public long GoldDelta;

        // Runs on the writer, after the commit, never after a failed flush -
        // except for a reload (RunThenOnFailure), which reloads regardless,
        // as it always did.
        public Func<Task>? Then;
        public bool RunThenOnFailure;

        public TaskCompletionSource? Fence;
    }

    /// <summary>
    /// What the writer tells the tick thread about one flush.
    /// </summary>
    public struct FlushAck
    {
        public long PlayerId;
        public bool Committed;

        // FlushState writes db.Epoch = snapshot.Epoch + 1 on commit.
        public long CommittedDbEpoch;

        // Non-zero only on a failure the tick must undo: the coins go back on
        // the live payload, or into a rescue if the player has gone.
        public long GoldDelta;

        // Task 79: the job's income tally, on the same terms as GoldDelta -
        // non-empty only when the flush did not commit and there is a live
        // payload to hand it back to. Never rescued on its own: a lost tally
        // under-counts the ledger and costs the player nothing.
        public GoldIncomeTally Income;

        // Live village production the job carried (job.Snapshot.Pending*Delta),
        // on the same terms as GoldDelta: non-zero only when the flush did not
        // commit, so the tick hands it back to the live payload.
        public long WoodDelta;
        public long StoneDelta;
        public long IronDelta;
        public FlushReason Reason;
        public bool SplitBrain;
    }

    /// <summary>
    /// The checkpoint writer: every <c>FlushState</c> the tick thread used to
    /// run synchronously now runs here.
    /// </summary>
    /// <remarks>
    /// Modul: CHECKPOINTS OFF THE TICK THREAD (task 43, plan item 2).
    ///
    /// FlushStateAndAdvance is a Serializable, FOR UPDATE, retrying
    /// transaction, and it ran on the one 10 Hz thread that simulates every
    /// player - from the periodic boundary, reload, logout and five command
    /// handlers. One slow commit stalled everybody's tick.
    ///
    /// FOUR PARTITIONS, playerId % 4, each a single-reader channel. That is the
    /// whole ordering guarantee: one player's jobs are FIFO and never run
    /// concurrently with each other, so the epoch arithmetic in RequestFlush
    /// holds and a command's continuation sees every earlier checkpoint.
    /// Different players still commit in parallel.
    ///
    /// NOT a StartCron worker, so CronWorkerGuardTests does not list it - but
    /// it follows that rule: every job is isolated in its own try/catch that
    /// begins before any connection is opened, so one bad job can never end a
    /// partition's loop.
    /// </remarks>
    public sealed class CheckpointWriter
    {
        public const int PartitionCount = 4;

        // Modul: a logout is the last chance this session's state has, and no
        // tick will come back to retry it - so the writer retries it itself,
        // then rescues the gold alone, then writes a dead-letter line. Today's
        // alternative was a silent loss.
        public const int LogoutAttempts = 3;
        private static readonly int[] LogoutBackoffMs = { 250, 1000 };

        public const string DeadLetterPrefix = "CHECKPOINT-DEADLETTER";

        private readonly Func<TickStatePayload, Task<bool>> _flush;
        private readonly Func<long, long, Task<bool>> _bankGold;
        private readonly Func<long, bool> _consumeSplitBrainMark;
        private readonly Func<ConcurrentQueue<FlushAck>> _acks;
        private readonly Action<long>? _onContinuationFailure;

        private readonly Channel<FlushJob>[] _channels = new Channel<FlushJob>[PartitionCount];
        private readonly Task[] _readers = new Task[PartitionCount];

        private long _queueDepth;
        private long _flushesCommitted;
        private long _flushesFailed;
        private long _deadLetters;

        public long QueueDepth => Interlocked.Read(ref _queueDepth);
        public long FlushesCommitted => Interlocked.Read(ref _flushesCommitted);
        public long FlushesFailed => Interlocked.Read(ref _flushesFailed);
        public long DeadLetters => Interlocked.Read(ref _deadLetters);

        internal CheckpointWriter(
            Func<TickStatePayload, Task<bool>> flush,
            Func<long, long, Task<bool>> bankGold,
            Func<long, bool> consumeSplitBrainMark,
            Func<ConcurrentQueue<FlushAck>> acks,
            Action<long>? onContinuationFailure)
        {
            _flush = flush;
            _bankGold = bankGold;
            _consumeSplitBrainMark = consumeSplitBrainMark;
            _acks = acks;
            _onContinuationFailure = onContinuationFailure;

            for (int i = 0; i < PartitionCount; i++)
            {
                _channels[i] = Channel.CreateUnbounded<FlushJob>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                var reader = _channels[i].Reader;
                _readers[i] = Task.Run(() => RunPartitionAsync(reader));
            }
        }

        internal static int PartitionOf(long playerId) => (int)(((playerId % PartitionCount) + PartitionCount) % PartitionCount);

        internal bool Enqueue(FlushJob job)
        {
            Interlocked.Increment(ref _queueDepth);
            if (_channels[PartitionOf(job.PlayerId)].Writer.TryWrite(job))
            {
                return true;
            }

            // The writer has been completed (shutdown). Nothing can run this
            // job any more, and saying so is better than dropping it quietly.
            Interlocked.Decrement(ref _queueDepth);
            if (job.Kind == FlushJobKind.Fence)
            {
                job.Fence?.TrySetResult();
            }
            else
            {
                DeadLetter(job.PlayerId, job.GoldDelta, job.Kind == FlushJobKind.Flush ? job.Reason.ToString() : "GoldRescue", "writer already stopped");
            }
            return false;
        }

        /// <summary>
        /// Completes once every job enqueued for this player before the call
        /// has finished - including a logout's retries and any continuation.
        /// A login awaits this before it reads the database, or a quick F5
        /// would load the row the logout flush is still writing, and the
        /// logout's commit would then make the new session look split-brained.
        /// </summary>
        public Task WaitForPlayerAsync(long playerId)
        {
            var fence = new FlushJob
            {
                Kind = FlushJobKind.Fence,
                PlayerId = playerId,
                Fence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            Enqueue(fence);
            return fence.Fence.Task;
        }

        /// <summary>Completes once every partition has drained what it held at the call.</summary>
        public Task WhenIdleAsync()
        {
            var fences = new Task[PartitionCount];
            for (int i = 0; i < PartitionCount; i++)
            {
                fences[i] = WaitForPlayerAsync(i);
            }
            return Task.WhenAll(fences);
        }

        /// <summary>
        /// Shutdown: no new jobs, and everything already queued runs to the
        /// end. Returns false if the timeout expired first.
        /// </summary>
        public bool CompleteAndDrain(TimeSpan timeout)
        {
            for (int i = 0; i < PartitionCount; i++)
            {
                _channels[i].Writer.TryComplete();
            }

            try
            {
                return Task.WaitAll(_readers, timeout);
            }
            catch (AggregateException ex)
            {
                // RunPartitionAsync never throws, so this is a bug - but it
                // must not stop the shutdown flush that follows.
                Console.WriteLine($"CheckpointWriter: a partition faulted during shutdown: {ex.InnerException?.Message}");
                return false;
            }
        }

        private async Task RunPartitionAsync(ChannelReader<FlushJob> reader)
        {
            while (true)
            {
                FlushJob job;
                try
                {
                    if (!await reader.WaitToReadAsync().ConfigureAwait(false))
                    {
                        return;
                    }
                    if (!reader.TryRead(out job!))
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    // A channel read cannot really throw, but a reader that
                    // ends here ends every checkpoint in the partition.
                    Console.WriteLine($"CheckpointWriter: channel read failed: {ex.Message}");
                    continue;
                }

                // Modul: THE GUARD OPENS BEFORE ANY CONNECTION DOES. FlushState
                // acquires its connection inside; a throw from there (pool
                // exhausted, server gone) must end this job and not the loop -
                // the CombatLootEngine lesson in CLAUDE.md.
                try
                {
                    await ProcessAsync(job).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"CheckpointWriter: job for player {job.PlayerId} ({job.Kind}/{job.Reason}) threw: {ex.Message}");
                    if (job.Kind == FlushJobKind.Fence)
                    {
                        job.Fence?.TrySetResult();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _queueDepth);
                }
            }
        }

        private async Task ProcessAsync(FlushJob job)
        {
            switch (job.Kind)
            {
                case FlushJobKind.Fence:
                    job.Fence?.TrySetResult();
                    return;

                case FlushJobKind.GoldRescue:
                    if (!await TryBankGoldAsync(job.PlayerId, job.GoldDelta).ConfigureAwait(false))
                    {
                        DeadLetter(job.PlayerId, job.GoldDelta, "GoldRescue", "could not bank the orphaned delta");
                    }
                    return;
            }

            int attempts = job.Reason == FlushReason.Logout ? LogoutAttempts : 1;
            bool committed = false;
            bool splitBrain = false;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                committed = await SafeFlushAsync(job.Snapshot).ConfigureAwait(false);
                if (committed)
                {
                    break;
                }

                splitBrain = _consumeSplitBrainMark(job.PlayerId);
                if (splitBrain)
                {
                    // Another session owns this row now. Retrying cannot win
                    // and would only be refused again.
                    break;
                }

                if (attempt + 1 < attempts)
                {
                    await Task.Delay(LogoutBackoffMs[Math.Min(attempt, LogoutBackoffMs.Length - 1)]).ConfigureAwait(false);
                }
            }

            if (committed)
            {
                Interlocked.Increment(ref _flushesCommitted);
            }
            else
            {
                Interlocked.Increment(ref _flushesFailed);
            }

            long goldForTick = committed ? 0L : job.GoldDelta;
            GoldIncomeTally incomeForTick = committed ? default : job.Snapshot.PendingGoldIncome;
            long woodForTick = committed ? 0L : job.Snapshot.PendingWoodDelta;
            long stoneForTick = committed ? 0L : job.Snapshot.PendingStoneDelta;
            long ironForTick = committed ? 0L : job.Snapshot.PendingIronDelta;
            if (!committed && job.Reason == FlushReason.Logout)
            {
                // Nobody is left to hand the coins back to. Split-brain gold is
                // not rescued: another session simulated this player too, and
                // banking both is the double-pay the sieve exists to stop.
                if (splitBrain)
                {
                    DeadLetter(job.PlayerId, job.GoldDelta, "Logout", "split-brain; the session's state is discarded");
                }
                else if (job.GoldDelta != 0L && await TryBankGoldAsync(job.PlayerId, job.GoldDelta).ConfigureAwait(false))
                {
                    DeadLetter(job.PlayerId, 0L, "Logout", $"flush failed {attempts}x; gold {job.GoldDelta} rescued, the rest of the session's state is lost");
                }
                else
                {
                    DeadLetter(job.PlayerId, job.GoldDelta, "Logout", $"flush failed {attempts}x");
                }
                if (woodForTick != 0L || stoneForTick != 0L || ironForTick != 0L)
                {
                    // Not rescued on its own the way gold is: recorded so a
                    // manual credit is possible, same as the rest of the state.
                    DeadLetter(job.PlayerId, 0L, "Logout", $"village production lost: wood={woodForTick} stone={stoneForTick} iron_ore={ironForTick}");
                }
                goldForTick = 0L;
                incomeForTick = default;
                woodForTick = stoneForTick = ironForTick = 0L;
            }

            // Modul: THE ACK GOES BEFORE THE CONTINUATION. The tick drains acks
            // ahead of StateReloadQueue, so a reload's fresh payload can never
            // land before the ack of the flush that preceded it - the reload
            // merge then carries FlushesInFlight and any handed-back gold over
            // from a live payload that already reflects this job.
            _acks().Enqueue(new FlushAck
            {
                PlayerId = job.PlayerId,
                Committed = committed,
                CommittedDbEpoch = committed ? job.Snapshot.LogicEpochCounter + 1 : 0L,
                GoldDelta = goldForTick,
                Income = incomeForTick,
                WoodDelta = woodForTick,
                StoneDelta = stoneForTick,
                IronDelta = ironForTick,
                Reason = job.Reason,
                SplitBrain = splitBrain
            });

            if (job.Then != null && (committed || (job.RunThenOnFailure && !splitBrain)))
            {
                try
                {
                    await job.Then().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Same answer SafeDispatchAsync gave when this work ran
                    // there: a player whose follow-up failed is disconnected
                    // rather than left suspended with nothing coming.
                    Console.WriteLine($"CheckpointWriter: continuation for player {job.PlayerId} ({job.Reason}) failed: {ex.Message}");
                    _onContinuationFailure?.Invoke(job.PlayerId);
                }
            }
        }

        private async Task<bool> SafeFlushAsync(TickStatePayload snapshot)
        {
            try
            {
                return await _flush(snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // FlushState catches its own; this is for a delegate that does not.
                Console.WriteLine($"CheckpointWriter: flush for player {snapshot.PlayerId} threw: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TryBankGoldAsync(long playerId, long delta)
        {
            if (delta == 0L)
            {
                return true;
            }

            try
            {
                return await _bankGold(playerId, delta).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CheckpointWriter: gold rescue for player {playerId} threw: {ex.Message}");
                return false;
            }
        }

        private void DeadLetter(long playerId, long goldDelta, string reason, string detail)
        {
            Interlocked.Increment(ref _deadLetters);
            // One greppable line per loss: `docker logs server | grep CHECKPOINT-DEADLETTER`
            // is the whole recovery procedure, so it carries everything a
            // manual credit needs.
            Console.WriteLine($"{DeadLetterPrefix} player={playerId} gold_delta={goldDelta} reason={reason} at={DateTimeOffset.UtcNow:O} detail=\"{detail}\"");
        }
    }
}

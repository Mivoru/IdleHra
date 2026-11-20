using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: moved out of SimulationEngine.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md).
    public partial class SimulationEngine
    {
        /// <summary>
        /// Lockstep, before a tick: jump the clock if nobody is online, wake the
        /// bots due, wait for their yields, and ask for a broadcast when a wake
        /// is due right after this tick. False = do not run a tick now (no bots
        /// yet, or the control socket is gone).
        /// </summary>
        private bool LockstepBeforeTick(SimConductor conductor)
        {
            LockstepSettle(everything: false);
            (long TickToRun, IReadOnlyList<SimWake> Wakes, bool NeedsBroadcast)? prep;
            Monitor.Enter(_activePlayers);
            try
            {
                long lastCompleted = conductor.Tick;
                prep = conductor.PrepareTick(_activePlayers.Count,
                    playerId => _activePlayers.ContainsKey(playerId) && _lastLockstepBroadcastTick != lastCompleted);
            }
            finally
            {
                Monitor.Exit(_activePlayers);
            }

            if (prep == null)
            {
                // No bots yet: hold the world still rather than run ahead.
                Thread.Sleep(10);
                return false;
            }

            var (tick, wakes, needsBroadcast) = prep.Value;
            // Before anyone looks at the world, or the clock leaps over it, every
            // piece of work the tick handed off has landed.
            if (wakes.Count > 0 || tick > _lockstepTick + 1) LockstepSettle(everything: true);
            // While the bots think, the game clock reads the state they see:
            // the end of the last completed tick.
            GameClock.SetLockstepTick(tick - 1);
            // Phase 1b: the gameplay crons due by this tick run first, on this
            // thread, and finish before any bot wakes or the tick runs - so a
            // kill's codex entry or loot lands on a fixed tick, at production's
            // lag, whatever the CPU is doing.
            SimLockstep.Crons?.RunDue(tick);
            if (wakes.Count > 0)
            {
                var sink = SimLockstep.WakeSink;
                if (sink == null)
                {
                    Console.WriteLine("[SimulationEngine] LOCKSTEP: bots are due but no control socket is connected; dropping them.");
                    conductor.LeaveAll();
                    return false;
                }
                sink(tick, wakes);
                // Modul: THE TICK ANSWERS REST WHILE THE BOTS THINK (phase 2). A
                // REST call that needs the live payload - the what-if and hunting
                // projections, the skill tree preview, the dungeon run, a world
                // boss strike - queues an order that only the TICK thread answers,
                // and waits 3 wall seconds for it. In lockstep the tick is parked
                // here while the bot thinks, so every such call timed out as
                // NoSession: the first lockstep run's optimal bots never got a
                // projection, never picked a fight, and each failed call cost 3
                // wall seconds (x50). So while waiting, the parked tick thread
                // serves exactly those orders, under the same lock the tick holds.
                while (IsRunning && !conductor.WaitForYields(TimeSpan.FromMilliseconds(1)))
                {
                    ServeWaitingRestCallers();
                }
                ServeWaitingRestCallers();
            }

            _forceBroadcastThisTick = needsBroadcast || conductor.AnyDueBy(tick + 1);
            _lockstepTick = tick;
            GameClock.SetLockstepTick(tick);
            return true;
        }

        /// <summary>
        /// The tick-side answers to REST calls that wait on the tick: payload
        /// snapshots, world boss strikes and rebirth requests (every REST route
        /// that awaits a tick-set Completion, 2026-11-03). Same drains, same order, as the tick
        /// head runs them; lockstep only, between ticks.
        /// </summary>
        private void ServeWaitingRestCallers()
            => LockstepRestService.Serve(_playerRegistry, _activePlayers, _worldBossEngine, _checkpointManager);

        /// <summary>
        /// Modul: LOCKSTEP WAITS FOR THE WORK THE TICK STARTED (phase 1d). In
        /// paced mode a login's database load (SafeDispatchAsync) takes a few
        /// ms of a 100 ms tick; in lockstep the tick runs flat out, so the player
        /// would enter the world at whatever tick the thread pool happened to
        /// finish on - a result that depends on the CPU, which is exactly what
        /// lockstep exists to remove. So before each tick: every dispatch the
        /// tick handed off has finished, and the checkpoint writer is under its
        /// bound (the P0 login wave queued 47). Bounded by a wall timeout, logged,
        /// so a dispatch that itself waits on the tick cannot freeze the world.
        /// The logout-then-login order needs nothing new: a login already fences
        /// on that player's queued checkpoints (WaitForPendingFlushesAsync).
        /// </summary>
        private void LockstepSettle(bool everything)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
            var writer = _checkpointManager.Writer;
            var spin = new SpinWait();
            // Before a wake the writer must be idle too: a bot's REST write that
            // races a checkpoint of the same player is a 40001 and, for some
            // routes, a kick (20-bot smoke, 2026-11-03: 21 sockets lost).
            long queueBound = everything ? 0 : LockstepCheckpointQueueBound;
            while (Volatile.Read(ref everything ? ref _inFlightDispatches : ref _inFlightGatingDispatches) > 0 || writer.QueueDepth > queueBound)
            {
                if (Stopwatch.GetTimestamp() > deadline)
                {
                    Console.WriteLine($"[SimulationEngine] LOCKSTEP: settle timed out ({Volatile.Read(ref _inFlightDispatches)} dispatches in flight, checkpoint queue {writer.QueueDepth}); running the tick anyway.");
                    return;
                }
                spin.SpinOnce(sleep1Threshold: 50);
            }
        }
    }
}

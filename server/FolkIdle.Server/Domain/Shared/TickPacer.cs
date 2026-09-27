using System;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// Task 43, 2d: fixed-timestep pacing for the 10 Hz tick.
    ///
    /// The loop used to end with <c>Thread.Sleep(TickIntervalMs - elapsed)</c>,
    /// so every overrun (a tick that took 130 ms) and every oversleep
    /// (Windows' 15.6 ms timer granularity) was simply lost: the next tick
    /// started late and nothing ever caught up. Each tick is exactly
    /// <c>TickIntervalMs</c> of game time, so live progress per wall-clock
    /// minute ran slower than the 10 Hz the balance tables are measured at,
    /// by however much the box was behind.
    ///
    /// This pacer keeps an absolute schedule instead. The next tick is due
    /// one interval after the previous one was DUE, not after it finished,
    /// so a late tick is followed by ones that start immediately until the
    /// schedule is met. The debt is bounded: at most
    /// <see cref="MaxCatchUpTicks"/> are owed at once, and anything beyond
    /// that is dropped and counted rather than replayed, so a long stall
    /// (a GC pause, a suspended VM) cannot turn into a burst of hundreds of
    /// back-to-back ticks that starve the network threads.
    ///
    /// Pure arithmetic over caller-supplied timestamps, so it is testable
    /// without a clock. Units are whatever the caller passes (the engine
    /// uses Stopwatch timestamps).
    /// </summary>
    public sealed class TickPacer
    {
        public const int MaxCatchUpTicks = 5;

        private readonly long _interval;
        private long _nextDue;
        private bool _anchored;

        public TickPacer(long interval)
        {
            if (interval <= 0) throw new ArgumentOutOfRangeException(nameof(interval));
            _interval = interval;
        }

        /// <summary>Ticks that were owed and never run, because the debt exceeded the cap.</summary>
        public long TicksDropped { get; private set; }

        /// <summary>Ticks that started immediately because the schedule was behind.</summary>
        public long CatchUpTicks { get; private set; }

        /// <summary>Call at the start of every tick.</summary>
        public void OnTickStarted(long now)
        {
            if (!_anchored)
            {
                _nextDue = now;
                _anchored = true;
            }
            _nextDue += _interval;
        }

        /// <summary>
        /// Call when a tick has finished. Returns how long to wait before the
        /// next tick; 0 means start it now.
        /// </summary>
        public long DelayUntilNextTick(long now)
        {
            long lag = now - _nextDue;
            if (lag < 0)
            {
                return -lag;
            }

            // The tick due at _nextDue is owed, plus one per whole interval
            // past it.
            long owed = lag / _interval + 1;
            if (owed > MaxCatchUpTicks)
            {
                long dropped = owed - MaxCatchUpTicks;
                TicksDropped += dropped;
                _nextDue += dropped * _interval;
            }
            CatchUpTicks++;
            return 0;
        }

        /// <summary>
        /// Forget the schedule, e.g. after the loop was deliberately paused
        /// (an era transition). The pause is not owed, so it is neither
        /// caught up nor counted as dropped.
        /// </summary>
        public void Reset()
        {
            _anchored = false;
        }
    }
}

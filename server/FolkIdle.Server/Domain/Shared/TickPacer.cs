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

        /// <summary>
        /// The fastest the dev tick multiplier may run.
        ///
        /// Modul: 20 UNTIL 2026-11-01, THEN 100, THEN 200, on the owner's
        /// requests. x20 was validated against x10 and x100 against x20
        /// (PLAYTEST_BOTS_REPORT.md section 1); x100 ran 2.59 million ticks with
        /// 7 bots and dropped none. At x200 a tick gets 0.5 ms of wall time
        /// against a measured 0.07-0.4 ms; the run's summary.md reports the
        /// effective speed from /metrics, so a run that fell behind shows it.
        /// x1000 is out of reach this way (0.1 ms a tick) - it would need a
        /// lockstep mode, not a faster clock.
        /// </summary>
        public const int MaxTickSpeed = 200;

        /// <summary>
        /// How many ticks the engine runs per <c>TickIntervalMs</c> of wall
        /// time: <c>FOLKIDLE_TICK_SPEED</c>, honoured only on a dev box.
        ///
        /// Modul: BOTS NEED A FAST WORLD, PLAYERS NEVER GET ONE (2026-11-01).
        /// The playtest bots (server/FolkIdle.Bots) measure how long a new
        /// player takes to reach each milestone, and at 10 Hz that is weeks of
        /// wall time. Every tick still advances exactly <c>TickIntervalMs</c> of
        /// GAME time, so the multiplier compresses wall time without changing
        /// a single balance number: combat, gathering, XP, ageing and potion
        /// durations are all counted in ticks. What it does NOT compress is
        /// everything read off <c>DateTime.UtcNow</c> - daily tickets, the
        /// midnight refills, epoch expiries - which stay on the real clock.
        /// Gated on the dev-tools rule, so production (DOTNET_ENVIRONMENT
        /// Production) runs at 1x whatever its .env says.
        /// </summary>
        public static int ResolveTickSpeed(string? flag, bool devToolsEnabled)
        {
            if (!devToolsEnabled || string.IsNullOrWhiteSpace(flag)) return 1;
            if (!int.TryParse(flag.Trim(), out int speed)) return 1;
            return Math.Clamp(speed, 1, MaxTickSpeed);
        }

        private readonly long _interval;
        private long _nextDue;
        private bool _anchored;

        private readonly int _maxCatchUp;

        public TickPacer(long interval) : this(interval, MaxCatchUpTicks) { }

        /// <summary>
        /// Modul: THE CATCH-UP CAP IS A WALL-TIME BOUND (2026-11-01). Five ticks
        /// is half a second of stall at 1x but 25 ms at the bots' 20x, barely
        /// more than one Windows timer slice (15.6 ms), and less than one at any
        /// higher speed - so a sleep would drop ticks. The engine passes
        /// MaxCatchUpTicks x speed, which keeps the same half-second bound.
        /// </summary>
        public TickPacer(long interval, int maxCatchUp)
        {
            if (interval <= 0) throw new ArgumentOutOfRangeException(nameof(interval));
            _interval = interval;
            _maxCatchUp = Math.Max(1, maxCatchUp);
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
            if (owed > _maxCatchUp)
            {
                long dropped = owed - _maxCatchUp;
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

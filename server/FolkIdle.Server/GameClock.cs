using System;
using System.Diagnostics;
using System.Threading;

namespace FolkIdle.Server
{
    /// <summary>
    /// The game's notion of "now": what every gameplay timer reads - offline
    /// catch-up, daily keys and midnight refills, potion and buff expiries,
    /// village upgrades, the world boss calendar, seasonal events, mail and
    /// market ages, per-day ledgers.
    ///
    /// Modul: ONE CLOCK FOR THE GAME, THE WALL CLOCK FOR EVERYTHING ELSE
    /// (2026-11-01). The playtest bots run the engine N times faster
    /// (FOLKIDLE_TICK_SPEED), but everything read off DateTime.UtcNow stayed
    /// at wall speed: a 24-game-hour run at x10 saw a tenth of a day of tickets,
    /// refills and offline time. At speed N this clock runs N times the wall
    /// clock from the moment the server started; at speed 1 - production, and
    /// any server without dev tools - it IS DateTime.UtcNow, so every call site
    /// that switched to it behaves exactly as before.
    ///
    /// What deliberately stays on the wall clock: token and JWT lifetimes,
    /// rate limits and the anti-cheat timing (Environment.TickCount64), secret
    /// rotation, billing and receipts, telemetry and logs, chat timestamps,
    /// retry back-offs, push and e-mail sends, and the live dungeon run's swipe
    /// windows - human reaction time does not speed up.
    ///
    /// Trap: at speed N the clock is AHEAD of the wall clock and writes future
    /// timestamps (LastLogoutTimestamp, expiries) into the database, and a
    /// restart re-anchors it to the wall clock - a timestamp "from the future"
    /// then refuses that login (ValidateLoginTime). Run fast servers on their
    /// own database and throw it away after a run (server/FolkIdle.Bots/README).
    /// </summary>
    public static class GameClock
    {
        private static int _speed = 1;
        private static DateTime _anchorUtc = DateTime.UtcNow;
        private static long _anchorTimestamp = Stopwatch.GetTimestamp();

        /// <summary>How many game seconds pass per wall second (1 outside dev boxes).</summary>
        public static int Speed => _speed;

        /// <summary>
        /// Set once at startup, from TickPacer.ResolveTickSpeed - the same gate
        /// as the tick multiplier, so the two cannot disagree.
        /// </summary>
        public static void Configure(int speed, DateTime? startUtc = null)
        {
            _speed = Math.Max(1, speed);
            // A start time only moves a FAST clock: at speed 1 the game clock is
            // the wall clock, full stop (production).
            _anchorUtc = _speed > 1 && startUtc is { } start ? start : DateTime.UtcNow;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            _lockstep = false;
        }

        /// <summary>
        /// Modul: BOTH SIDES OF A VALIDATION START AT THE SAME GAME HOUR
        /// (night 2026-11-04). The paced reference started at 23:00 UTC and the
        /// lockstep runs at 02:36 and 05:03, inside the sleepers' shared
        /// 00:30-06:30 core, so lockstep bots began playing four game hours
        /// "late" and every milestone compared a different day. FOLKIDLE_SIM_START_UTC
        /// (an ISO UTC time, dev tools only, ignored at speed 1) puts game time
        /// zero at the same instant for every run. Null when unset or unreadable.
        /// </summary>
        public static DateTime? ResolveStartUtc(string? raw, bool devToolsEnabled)
        {
            if (!devToolsEnabled || string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
        }

        public static DateTime UtcNow
            => _lockstep ? LockstepTime(_anchorUtc, Volatile.Read(ref _lockstepTick))
             : _speed <= 1 ? DateTime.UtcNow : Project(_anchorUtc, Stopwatch.GetTimestamp() - _anchorTimestamp, Stopwatch.Frequency, _speed);

        // Modul: LOCKSTEP TIME IS A TICK COUNT, NOT A STOPWATCH (2026-11-03,
        // docs/design/LOCKSTEP_SIM.md). Under FOLKIDLE_SIM_LOCKSTEP the engine
        // runs as fast as the CPU allows and waits for the bots, so wall time
        // says nothing about game time; the clock is the anchor plus the ticks
        // the engine has completed (SimConductor.Tick), 100 ms each. Speed then
        // reads LockstepNominalSpeed for the few wall-time heuristics that
        // divide by it (the projection cache).
        private static bool _lockstep;
        private static long _lockstepTick;
        public const int LockstepNominalSpeed = 1000;
        public const long LockstepTickTimeSpanTicks = 100 * TimeSpan.TicksPerMillisecond;

        public static bool IsLockstep => _lockstep;

        /// <summary>Game time zero under lockstep (tick 0); the bots read the hour of day from it.</summary>
        public static DateTime LockstepAnchorUtc => _anchorUtc;

        public static void ConfigureLockstep(DateTime anchorUtc)
        {
            _anchorUtc = anchorUtc;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lockstepTick, 0);
            _speed = LockstepNominalSpeed;
            _lockstep = true;
        }

        /// <summary>
        /// The anti-cheat and throttling clock: <c>Environment.TickCount64</c>,
        /// except under lockstep, where it is game time in milliseconds.
        ///
        /// Modul: THE GUARDS MUST SEE THE TIME THE BOT LIVES IN (phase 1c). The
        /// 100 ms command rule, the 5-in-100-ms throttler, the token bucket, the
        /// challenge window and the macro detector all measured wall time. In
        /// lockstep a bot's whole turn happens in frozen game time and its turns
        /// are game seconds apart but wall MICROSECONDS apart - so on the wall
        /// clock a careful bot looks like a flood, and its reaction spacing
        /// (what the macro detector judges) would be the CPU's, not the
        /// persona's. Offset by a large base so a "0 = never" sentinel in the
        /// guards still means never at tick 0.
        /// </summary>
        public static long MonotonicMs
            => _lockstep ? LockstepMonotonicBaseMs + Volatile.Read(ref _lockstepTick) * 100 : Environment.TickCount64;

        /// <summary>
        /// The clock for HUMAN reaction windows (the dungeon telegraph): the wall
        /// clock, because reaction time does not speed up at x200 - except under
        /// lockstep, where the bot's reaction is game ticks the conductor waits
        /// out, so the window must be judged in game time too (phase 1c).
        /// </summary>
        public static DateTime ReactionUtcNow => _lockstep ? UtcNow : DateTime.UtcNow;

        /// <summary><see cref="MonotonicMs"/> in Stopwatch units, for the token bucket.</summary>
        public static long MonotonicStopwatchTimestamp
            => _lockstep ? MonotonicMs * (Stopwatch.Frequency / 1000) : Stopwatch.GetTimestamp();

        public const long LockstepMonotonicBaseMs = 1_000_000_000;

        /// <summary>The engine completed <paramref name="tick"/> (lockstep only).</summary>
        public static void SetLockstepTick(long tick) => Volatile.Write(ref _lockstepTick, tick);

        internal static DateTime LockstepTime(DateTime anchorUtc, long tick)
            => anchorUtc.AddTicks(tick * LockstepTickTimeSpanTicks);

        public static DateTimeOffset UtcNowOffset
            => _speed <= 1 ? DateTimeOffset.UtcNow : new DateTimeOffset(UtcNow, TimeSpan.Zero);

        /// <summary>The arithmetic, pure: the anchor plus the elapsed wall time times the speed.</summary>
        internal static DateTime Project(DateTime anchorUtc, long elapsedStopwatchTicks, long stopwatchFrequency, int speed)
        {
            double wallSeconds = (double)elapsedStopwatchTicks / stopwatchFrequency;
            return anchorUtc.AddTicks((long)(wallSeconds * speed * TimeSpan.TicksPerSecond));
        }
    }
}

using System;
using System.Diagnostics;

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
        public static void Configure(int speed)
        {
            _anchorUtc = DateTime.UtcNow;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            _speed = Math.Max(1, speed);
        }

        public static DateTime UtcNow
            => _speed <= 1 ? DateTime.UtcNow : Project(_anchorUtc, Stopwatch.GetTimestamp() - _anchorTimestamp, Stopwatch.Frequency, _speed);

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

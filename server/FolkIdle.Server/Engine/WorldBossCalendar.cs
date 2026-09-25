using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// When the world boss is fought, and how often (owner decision, 2026-09-25).
    /// </summary>
    /// <remarks>
    /// Modul: ONE ENCOUNTER A WEEK, MONDAY TO SUNDAY (UTC), BACK TO BACK, AND
    /// ONE STRIKE A DAY.
    ///
    /// It used to be two windows a month (the 1st-7th and the 15th-22nd) with
    /// three strikes for the whole window. So a player spent three strikes in
    /// the first hour, then had nothing to do for a week, and nothing at all for
    /// the week after that. The owner wanted a reason to come back every day:
    /// - the boss is always there (a new one every Monday, with a new weak
    ///   plate and a new reward pool);
    /// - every player gets one strike per UTC day, so about seven a week.
    ///
    /// The weak plate still lasts the whole week, so the crowd has seven days
    /// to find it by breaking the others. Rewards are still paid once per
    /// encounter, when it falls.
    ///
    /// The client mirrors none of this by hand: WorldBoss.svelte reads the
    /// encounter's end from the wire, and serverMirrors.test.ts pins the
    /// client's "next Monday" arithmetic to this file.
    /// </remarks>
    public static class WorldBossCalendar
    {
        public const int StrikesPerDay = 1;

        /// <summary>The UTC day number (days since the Unix epoch) - the same key daily quests use.</summary>
        public static long DayKey(long nowEpochSeconds) => nowEpochSeconds / 86_400L;

        /// <summary>Monday 00:00:00 UTC of the week containing <paramref name="now"/>.</summary>
        public static DateTimeOffset WeekStart(DateTimeOffset now)
        {
            var utc = now.ToUniversalTime();
            int sinceMonday = ((int)utc.DayOfWeek + 6) % 7; // Monday = 0 ... Sunday = 6
            var monday = utc.Date.AddDays(-sinceMonday);
            return new DateTimeOffset(monday, TimeSpan.Zero);
        }

        /// <summary>
        /// Sunday 23:59:59 UTC of the week containing <paramref name="now"/>:
        /// the EventEndEpoch of that week's encounter. It is also how LiveOps
        /// tells "this week's boss already happened" from "no boss yet".
        /// </summary>
        public static long WeekEndEpoch(DateTimeOffset now) =>
            WeekStart(now).AddDays(7).AddSeconds(-1).ToUnixTimeSeconds();
    }
}

namespace FolkIdle.Bots
{
    /// <summary>
    /// How often a bot looks at the game. The decisions are the same for every
    /// persona (BotBrain); only the attention differs. Between check-ins a
    /// casual or idle bot is LOGGED OUT, so the server's offline catch-up and
    /// offline cap decide what the time away earned (see BotBrain.RunAsync and
    /// GameClock, which makes that time pass at the tick multiplier).
    /// </summary>
    public sealed record Persona(string Name, double CheckInEveryGameHours, double CheckInGameMinutes, bool PatientRebirth = false, bool KeepWholeKit = false, bool Sleeps = false)
    {
        public bool AlwaysOn => CheckInEveryGameHours <= 0;

        // Modul: TWO FACTORS, FOUR PERSONAS (rebirth experiment 0.6.2,
        // 2026-11-03). The first experiment changed WHEN a bot is reborn and
        // WHAT it keeps at the same time, so its result could not say which of
        // the two mattered. PatientRebirth is the timing (a game day without
        // progress instead of six hours without a level); KeepWholeKit is the
        // gear (every worn combat piece it can afford, best first, instead of
        // the weapon when it costs at most half the gold). The 2x2 is
        // optimal (A), patientbare (B), eagerkit (C), patient (D).

        // Modul: EVERY PLAYER SLEEPS (owner, 2026-11-03, docs/design/LOCKSTEP_SIM.md).
        // The four "plays without pause" personas used to stay online 24/7. Even
        // the most active real player sleeps, and on a phone is offline outside
        // the app - so they log out every game night, the morning login runs the
        // real offline catch-up, and with everyone offline at once the lockstep
        // conductor jumps the night instead of ticking it. 24/7 stays available
        // as --no-sleep (SleepDisabled).

        /// <summary>Plays all day, sleeps at night. Eager rebirth, keeps the weapon at most (A).</summary>
        public static readonly Persona Optimal = new("optimal", 0, 0, Sleeps: true);

        /// <summary>
        /// Plays without pause like optimal, but is reborn only when nothing has
        /// progressed (level, dungeon stage, Ascension step) for a game day AND
        /// it can pay to keep its worn gear - the player who saves and pushes
        /// before starting over (rebirth experiment, 2026-11-02). Patient timing
        /// and the whole kit (D).
        /// </summary>
        public static readonly Persona Patient = new("patient", 0, 0, PatientRebirth: true, KeepWholeKit: true, Sleeps: true);

        /// <summary>Patient timing, but keeps only what optimal keeps (B).</summary>
        public static readonly Persona PatientBare = new("patientbare", 0, 0, PatientRebirth: true, Sleeps: true);

        /// <summary>Eager timing, but keeps every worn piece it can afford at that moment (C).</summary>
        public static readonly Persona EagerKit = new("eagerkit", 0, 0, KeepWholeKit: true, Sleeps: true);

        /// <summary>--no-sleep: the old 24/7 personas, for a run that wants them.</summary>
        public static bool SleepDisabled { get; set; }

        /// <summary>
        /// The night, in game UTC: offline from 00:30 minus up to an hour to 06:30
        /// plus up to an hour - 6 to 8 hours, with 00:30-06:30 shared by every
        /// sleeper so the conductor can jump it. The two jitters are drawn per bot
        /// per night from a stable hash, so logouts and logins spread over an hour
        /// instead of landing on one tick, and a rerun with the same names gets
        /// the same nights.
        /// </summary>
        public const double NightCoreStartHour = 0.5;
        public const double NightCoreEndHour = 6.5;

        public bool IsAsleep(DateTime gameUtc, string bot)
        {
            if (!Sleeps || SleepDisabled) return false;
            var (start, end) = NightBounds(gameUtc, bot);
            double pos = NightPosition(gameUtc);
            return pos >= start && pos < end;
        }

        /// <summary>Game hours from <paramref name="gameUtc"/> until this bot's night ends (0 if awake).</summary>
        public double HoursUntilAwake(DateTime gameUtc, string bot)
        {
            if (!IsAsleep(gameUtc, bot)) return 0;
            var (_, end) = NightBounds(gameUtc, bot);
            return end - NightPosition(gameUtc);
        }

        /// <summary>Game hours from <paramref name="gameUtc"/> until this bot's next night starts.</summary>
        public double HoursUntilAsleep(DateTime gameUtc, string bot)
        {
            if (!Sleeps || SleepDisabled) return double.PositiveInfinity;
            if (IsAsleep(gameUtc, bot)) return 0;
            double pos = NightPosition(gameUtc);
            var (start, _) = NightBounds(gameUtc, bot);
            if (pos < start) return start - pos;
            // Tonight is over: tomorrow's night, with tomorrow's jitter.
            var (nextStart, _) = NightBounds(gameUtc.AddHours(24 - pos + 0.01), bot);
            return 24 - pos + nextStart;
        }

        // Hours since 23:00 of the night in question, so the whole night window
        // [23:30, 07:30] is one unbroken range [0.5, 8.5).
        private static double NightPosition(DateTime gameUtc) => (gameUtc.TimeOfDay.TotalHours + 1) % 24;

        private static (double Start, double End) NightBounds(DateTime gameUtc, string bot)
        {
            long night = (long)Math.Floor(((gameUtc - DateTime.UnixEpoch).TotalHours + 1) / 24);
            return (NightCoreStartHour + 1 - Jitter(bot, night, 0), NightCoreEndHour + 1 + Jitter(bot, night, 1));
        }

        /// <summary>0..1 hour, stable across processes (string.GetHashCode is not).</summary>
        internal static double Jitter(string bot, long night, int which)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in bot) { h ^= c; h *= 1099511628211UL; }
                h ^= (ulong)night; h *= 1099511628211UL;
                h ^= (ulong)which; h *= 1099511628211UL;
                h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
                return (h % 10_000) / 10_000.0;
            }
        }

        /// <summary>Looks in three times a day for twenty minutes.</summary>
        public static readonly Persona Casual = new("casual", 8, 20);

        /// <summary>Looks in once a day for twenty minutes.</summary>
        public static readonly Persona Idle = new("idle", 24, 20);

        public static Persona? Parse(string name) => name.Trim().ToLowerInvariant() switch
        {
            "optimal" => Optimal,
            "patient" => Patient,
            "patientbare" => PatientBare,
            "eagerkit" => EagerKit,
            "casual" => Casual,
            "idle" => Idle,
            _ => null,
        };

        /// <summary>Game hours left in the current check-in; infinite for a bot that never logs out.</summary>
        public double HoursLeftInCheckIn(double gameHours)
            => AlwaysOn ? double.PositiveInfinity : Math.Max(0, CheckInGameMinutes / 60.0 - gameHours % CheckInEveryGameHours);

        /// <summary>True while the bot is looking at the game.</summary>
        public bool IsCheckedIn(double gameHours)
            => AlwaysOn || gameHours % CheckInEveryGameHours < CheckInGameMinutes / 60.0;

        /// <summary>Checked in AND not asleep - what the bot loop asks.</summary>
        public bool IsOnline(double gameHours, DateTime gameUtc, string bot)
            => IsCheckedIn(gameHours) && !IsAsleep(gameUtc, bot);

        /// <summary>Game hours from now until this bot is next online (0 if it is).</summary>
        public double HoursUntilOnline(double gameHours, DateTime gameUtc, string bot)
        {
            if (IsOnline(gameHours, gameUtc, bot)) return 0;
            if (IsAsleep(gameUtc, bot)) return HoursUntilAwake(gameUtc, bot);
            double every = CheckInEveryGameHours;
            return (Math.Floor(gameHours / every) + 1) * every - gameHours;
        }
    }
}

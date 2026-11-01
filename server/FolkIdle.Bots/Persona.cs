namespace FolkIdle.Bots
{
    /// <summary>
    /// How often a bot looks at the game. The decisions are the same for every
    /// persona (BotBrain); only the attention differs.
    ///
    /// Modul: A CHECK-IN, NOT A LOGOUT. Between check-ins a casual or idle bot
    /// stays connected and sends nothing, rather than disconnecting. Offline
    /// catch-up is computed from the WALL clock (LastLoginTimestamp), which the
    /// dev tick multiplier does not compress, so a real logout at x10 would
    /// earn a tenth of the offline time it should. Online progress is what
    /// offline catch-up was made to match (offline parity, #120), so staying
    /// online and idle stands in for being away - except for the offline CAP,
    /// which the bot applies itself: once a gap passes the snapshot's
    /// OfflineCapSeconds it stops its activity, as a capped offline session
    /// would earn nothing more.
    /// </summary>
    public sealed record Persona(string Name, double CheckInEveryGameHours, double CheckInGameMinutes)
    {
        public bool AlwaysOn => CheckInEveryGameHours <= 0;

        /// <summary>Plays without pause.</summary>
        public static readonly Persona Optimal = new("optimal", 0, 0);

        /// <summary>Looks in three times a day for twenty minutes.</summary>
        public static readonly Persona Casual = new("casual", 8, 20);

        /// <summary>Looks in once a day for twenty minutes.</summary>
        public static readonly Persona Idle = new("idle", 24, 20);

        public static Persona? Parse(string name) => name.Trim().ToLowerInvariant() switch
        {
            "optimal" => Optimal,
            "casual" => Casual,
            "idle" => Idle,
            _ => null,
        };

        /// <summary>True while the bot is looking at the game.</summary>
        public bool IsCheckedIn(double gameHours)
            => AlwaysOn || gameHours % CheckInEveryGameHours < CheckInGameMinutes / 60.0;

        /// <summary>Game hours since the last check-in ENDED; 0 while checked in.</summary>
        public double HoursAway(double gameHours)
        {
            if (IsCheckedIn(gameHours)) return 0;
            return gameHours % CheckInEveryGameHours - CheckInGameMinutes / 60.0;
        }
    }
}

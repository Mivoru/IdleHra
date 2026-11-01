namespace FolkIdle.Bots
{
    /// <summary>
    /// How often a bot looks at the game. The decisions are the same for every
    /// persona (BotBrain); only the attention differs. Between check-ins a
    /// casual or idle bot is LOGGED OUT, so the server's offline catch-up and
    /// offline cap decide what the time away earned (see BotBrain.RunAsync and
    /// GameClock, which makes that time pass at the tick multiplier).
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
    }
}

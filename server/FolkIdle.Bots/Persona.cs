namespace FolkIdle.Bots
{
    /// <summary>
    /// How often a bot looks at the game. The decisions are the same for every
    /// persona (BotBrain); only the attention differs. Between check-ins a
    /// casual or idle bot is LOGGED OUT, so the server's offline catch-up and
    /// offline cap decide what the time away earned (see BotBrain.RunAsync and
    /// GameClock, which makes that time pass at the tick multiplier).
    /// </summary>
    public sealed record Persona(string Name, double CheckInEveryGameHours, double CheckInGameMinutes, bool PatientRebirth = false, bool KeepWholeKit = false)
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

        /// <summary>Plays without pause. Eager rebirth, keeps the weapon at most (A).</summary>
        public static readonly Persona Optimal = new("optimal", 0, 0);

        /// <summary>
        /// Plays without pause like optimal, but is reborn only when nothing has
        /// progressed (level, dungeon stage, Ascension step) for a game day AND
        /// it can pay to keep its worn gear - the player who saves and pushes
        /// before starting over (rebirth experiment, 2026-11-02). Patient timing
        /// and the whole kit (D).
        /// </summary>
        public static readonly Persona Patient = new("patient", 0, 0, PatientRebirth: true, KeepWholeKit: true);

        /// <summary>Patient timing, but keeps only what optimal keeps (B).</summary>
        public static readonly Persona PatientBare = new("patientbare", 0, 0, PatientRebirth: true);

        /// <summary>Eager timing, but keeps every worn piece it can afford at that moment (C).</summary>
        public static readonly Persona EagerKit = new("eagerkit", 0, 0, KeepWholeKit: true);

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
    }
}

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// How old a fielded character is, and what age costs it.
    ///
    /// ONE PLACE, and that is the point of the file. The thresholds used to be
    /// four bare literals in SimulationEngine.ProcessAgeSlot and the same four
    /// again in OfflineSimulationEngine - the second carrying a comment saying
    /// it "mirrors SimulationEngine.ProcessAgeSlot's exact thresholds", which
    /// is a promise no comment can keep. The stat penalty was a third copy, in
    /// StatsCalculator. Drift between copies of one truth is this codebase's
    /// dominant bug class; AgePhaseCurveTests fails if any of them comes back.
    ///
    /// THE NUMBERS WERE ALSO WRONG, and wrong in a way nobody could escape. A
    /// character reached Old - a permanent -20% to damage, health and attack
    /// speed - after THREE HOURS in a slot, and the designed way back is to
    /// breed a successor and field it. Breeding gated on a `characters.Level`
    /// column whose only writer in the whole server was the dev fixture seeder,
    /// so on the live box every account had the decay running and the recovery
    /// sealed. Measured when this was written: 80 characters, none above level
    /// 1, and the reporting player's main at 59 hours fielded, all at -20%.
    ///
    /// Stretched, a hero lasts a week of play rather than an evening, and the
    /// reason to breed moves off the decay and onto the bloodline - aptitudes
    /// are what survive the ninety-day season reset, which is a better reason
    /// than a penalty the player cannot outrun.
    ///
    /// NO MIGRATION IS NEEDED to change any of this. ProcessAgeSlot recomputes
    /// the phase from AgeTicks on every tick, so AgeTicks is the only durable
    /// input and the phase is derived. Changing a threshold re-derives every
    /// character in the game on the first tick after deploy.
    /// </summary>
    public static class AgePhaseCurve
    {
        public const int Child = 0;
        public const int Adult = 1;
        public const int Senior = 2;
        public const int Elder = 3;

        /// <summary>
        /// The live tick is 100 ms and adds one AgeTicks, so 10 a second. This
        /// constant is the bridge between the stored number and anything a
        /// designer or a player can reason about; every threshold below is
        /// written as hours against it rather than as a raw count.
        /// </summary>
        public const long TicksPerHour = 36_000L;

        public const long ChildEndTicks = 1L * TicksPerHour;
        public const long AdultEndTicks = 40L * TicksPerHour;
        public const long SeniorEndTicks = 80L * TicksPerHour;

        /// <summary>
        /// A newborn matures in an hour; a hero has thirty-nine hours of prime;
        /// the decline starts at forty and settles at eighty.
        /// </summary>
        public static int PhaseFor(long ageTicks)
        {
            if (ageTicks >= SeniorEndTicks) return Elder;
            if (ageTicks >= AdultEndTicks) return Senior;
            if (ageTicks >= ChildEndTicks) return Adult;
            return Child;
        }

        /// <summary>
        /// What the phase multiplies melee damage, ranged damage, max health
        /// and attack speed by.
        ///
        /// Halved from the -10%/-20% it used to be. With the timeline stretched
        /// the penalty no longer needs to be the whip that drives breeding, and
        /// a fifth of a character's whole stat line was a large price for a
        /// clock nothing could stop.
        ///
        /// An out-of-range phase is NOT a penalty. The value arrives from a
        /// payload field and a database column, and a stray one would otherwise
        /// multiply a character's entire stat line toward zero.
        /// </summary>
        public static float PenaltyMultiplier(int phase) => phase switch
        {
            Senior => 0.95f,
            Elder => 0.90f,
            _ => 1.0f,
        };
    }
}

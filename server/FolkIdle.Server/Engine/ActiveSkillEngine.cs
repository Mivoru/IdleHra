namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What is left of the four active skills: the status bits they used to
    /// apply, which outlived them because the set bonuses use one too.
    ///
    /// THE SKILLS THEMSELVES ARE GONE, and by measurement rather than taste.
    /// Four abilities on 3-to-20 second cooldowns, multiplying the next hit by
    /// 150 to 500 percent, against mana that refilled faster than the cooldowns
    /// cleared: at a 1.5 second swing that is 390 of every 400 swings buffed,
    /// which is +90% damage and +136% with the synergy below. Available only to
    /// a player willing to click every three seconds, in an idle game. Two
    /// different games sharing one balance sheet, and the pacing model knew
    /// about neither of them.
    ///
    /// What replaced them is SkillTreeRegistry - the same points, spent on five
    /// passive branches, worth tens of percent when a whole season goes into
    /// one.
    ///
    /// The parse of skills.json went with the skills. Vulnerable and Chilled no
    /// longer have anything that applies them; they are kept because the client
    /// reads all three bits off one TargetStatusEffectBitmask byte, and Burning
    /// is still set every time the Chiming Steel four-piece fires. Removing the
    /// two dead bits would renumber the third.
    /// </summary>
    public static class ActiveSkillEngine
    {
        public const byte StatusFlagVulnerable = 1 << 0;
        public const byte StatusFlagChilled = 1 << 1;

        // Modul: set bonuses made real. Set by the Chiming Steel 4-piece burn
        // in SimulationEngine's damage step. Lives here with the other two so
        // the bitmask has one owner - the client reads all three off the same
        // TargetStatusEffectBitmask byte.
        public const byte StatusFlagBurning = 1 << 2;

        /// <summary>
        /// Kept as a no-op so the boot sequence and the content-validation flag
        /// still read the same. It used to parse and validate skills.json;
        /// there is nothing left to parse, and a method that silently vanishes
        /// from a startup path is harder to notice than one that says why.
        /// </summary>
        public static void Initialize(string? gameDataDirectory = null)
        {
        }
    }
}

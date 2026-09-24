namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Economy numbers that are DECISIONS, not measurements: each one was chosen
    /// by the owner and must only change by a deliberate edit to this file.
    /// </summary>
    public static class EconomyDecisions
    {
        /// <summary>
        /// Combat pays this percentage of a monster's BaseGoldReward, before every
        /// per-player multiplier (race, legacy perks, guild buff, inheritance,
        /// Trophy Hunter).
        /// </summary>
        /// <remarks>
        /// Modul: 75% WAS AN ACCIDENT OF THE AUDIT, AND IS NOW A DECISION.
        ///
        /// Until 2026-09-24 this lived on GlobalEngineState as a mutable "gold drop
        /// multiplier" field that defaulted to 100 and that EcoTelemetryEngine's
        /// hourly audit rewrote: 75 whenever the minted/consumed ratio left
        /// [0.85, 1.15], 100 whenever it came back inside. The ratio counts every
        /// gold balance as "minted" against a handful of sinks, so on the live
        /// server it has never been anywhere near the band - in practice the
        /// audit pinned combat gold at 75% on its first pass after start-up.
        /// Two consequences nobody chose:
        ///
        ///   - every restart (every deploy) paid FULL combat gold until the
        ///     first audit pass landed, because the field started at 100;
        ///   - a telemetry heuristic silently owned a core payout, and a
        ///     database that happened to balance would have raised it by a third.
        ///
        /// The owner kept 75% deliberately on 2026-09-24 (task 37 spec §9). The
        /// audit still writes its ledger row and raises its out-of-band event;
        /// it no longer moves this number. CombatGoldDecisionTests pins all of it.
        /// </remarks>
        public const int CombatGoldPercent = 75;

        /// <summary>
        /// The base gold one kill of a monster with this BaseGoldReward pays,
        /// before per-player multipliers. Both kill paths (the live tick and the
        /// offline projection) call this, so they cannot disagree on the base.
        /// </summary>
        public static long BaseCombatGold(long baseGoldReward) => baseGoldReward * CombatGoldPercent / 100L;
    }
}

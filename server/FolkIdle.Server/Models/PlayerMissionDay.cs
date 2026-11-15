using System;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One player's daily missions for one UTC day (DayKey = days since the
    /// Unix epoch): which missions were drawn, which were claimed, the reroll,
    /// and the Chronicle XP the day paid.
    /// </summary>
    /// <remarks>
    /// Modul: THE DAY IS DRAWN ONCE AND STORED. The draw reads facts that move
    /// during a day (gold, chests, a world boss that dies on Wednesday), so a
    /// draw recomputed on every read would change a mission the player was
    /// halfway through. The first read of the day writes this row; every later
    /// read uses it.
    ///
    /// XpEarned is the Chronicle pass's input. The pass (step 3 of
    /// docs/design/CHRONICLE_PASS.md) sums it over its season's days, so mission
    /// XP never sits on PlayerRecords, which the checkpoint rewrites from the
    /// live payload.
    /// </remarks>
    public class PlayerMissionDay
    {
        public long PlayerId { get; set; }
        public int DayKey { get; set; }

        /// <summary>MissionAction values in slot order, comma-separated ("1,2,10,18").</summary>
        public string Actions { get; set; } = string.Empty;

        /// <summary>Bit N = slot N has been claimed.</summary>
        public int ClaimedMask { get; set; }

        public bool BonusClaimed { get; set; }
        public int RerollsUsed { get; set; }
        public int XpEarned { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    /// <summary>
    /// How often a player did one mission act on one UTC day. Detail tells
    /// apart acts a mission counts separately (the dungeon gate); it is 0 for
    /// the rest. Written only by MissionProgressWorker, from MissionEvents.
    /// </summary>
    public class PlayerMissionCounter
    {
        public long PlayerId { get; set; }
        public int DayKey { get; set; }
        public short Action { get; set; }
        public short Detail { get; set; }
        public int Count { get; set; }
    }
}

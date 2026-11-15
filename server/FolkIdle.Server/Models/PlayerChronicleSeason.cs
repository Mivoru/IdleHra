namespace FolkIdle.Server.Models
{
    /// <summary>
    /// What one player has claimed from one Chronicle season's track. The XP is
    /// not here: it is the sum of PlayerMissionDays.XpEarned over the season's
    /// days (MissionEngine.XpBetweenAsync), so the pass can never disagree with
    /// the missions that earned it.
    /// </summary>
    /// <remarks>
    /// Modul: A NEW TABLE, NOT PlayerChroniclePasses. That one belongs to the
    /// pass hidden on 2026-09-28: its XP came from the client and its free
    /// rewards named item ids that do not exist. Nothing reads it now.
    /// </remarks>
    public class PlayerChronicleSeason
    {
        public long PlayerId { get; set; }
        public int Season { get; set; }

        /// <summary>Bit N (1..50) = level N claimed.</summary>
        public long ClaimedMask { get; set; }

        /// <summary>Bonus chests past level 50 already paid.</summary>
        public int OverflowClaimed { get; set; }
    }
}

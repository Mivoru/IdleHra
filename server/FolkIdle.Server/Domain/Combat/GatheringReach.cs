namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// Which locations a player may gather in: every location they have
    /// killed in, and every region whose door is open.
    /// </summary>
    /// <remarks>
    /// Modul: THE REGION-2 CATCH-22 (owner decision 2026-11-14, option A).
    /// HighestLocationReached used to rise only on a kill in the location, but
    /// a region's fights need that region's fish - on region-1 fish every
    /// region-2 monster projected a death, so the door was open and the larder
    /// could never follow. An open region (HighestUnlockedRegion, raised when
    /// the previous boss falls) is now enough. One function, called by the
    /// live kill, the offline kill (both through ApplyKillProgression) and
    /// hydration, so the three cannot disagree.
    /// </remarks>
    public static class GatheringReach
    {
        public static int Raise(int highestLocationReached, int highestUnlockedRegion)
            => highestUnlockedRegion > highestLocationReached ? highestUnlockedRegion : highestLocationReached;
    }
}

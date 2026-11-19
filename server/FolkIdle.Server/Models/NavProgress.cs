namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One account's Descent into Nav (2026-11-19): records per Circle, Souls,
    /// the Workshop, the research and the run charges. One row per player,
    /// created on the first read.
    /// </summary>
    /// <remarks>
    /// Modul: PACKED STRINGS, NOT A ROW PER UPGRADE. The Workshop and the
    /// research are short lists of (key, level) that are always read and written
    /// together, so "key:level,key:level" (NavRules.Pack/Unpack) keeps a run and
    /// a purchase to one row lock - the same shape BuildPreset uses for War ranks.
    /// </remarks>
    public class NavProgress
    {
        public long PlayerId { get; set; }
        /// <summary>The deepest depth cleared per Circle, "circle:depth".</summary>
        public string BestDepths { get; set; } = string.Empty;
        /// <summary>The highest Circle opened (1 until depth 50 of Circle 1).</summary>
        public int HighestCircle { get; set; } = 1;
        public long Souls { get; set; }
        public long SoulsLifetime { get; set; }
        /// <summary>Workshop levels, "key:level".</summary>
        public string Workshop { get; set; } = string.Empty;
        /// <summary>Finished research levels, "key:level".</summary>
        public string Research { get; set; } = string.Empty;
        /// <summary>The line being researched now, or empty.</summary>
        public string ResearchActive { get; set; } = string.Empty;
        public long ResearchEndsEpoch { get; set; }
        public int Charges { get; set; } = Domain.Progression.NavRules.ChargeCap;
        public long ChargeAnchorEpoch { get; set; }
        /// <summary>First-time milestone chests already paid (NavRules.MilestoneBit).</summary>
        public long MilestoneMask { get; set; }
        public int RunsTotal { get; set; }
        /// <summary>The last run as the screen shows it (JSON), so a reload still shows it.</summary>
        public string LastRunJson { get; set; } = string.Empty;
    }
}

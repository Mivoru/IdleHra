namespace FolkIdle.Server.Models
{
    /// <summary>
    /// A player's wardrobe threads (2026-11-15): what duplicates unravel into,
    /// spent in the Wardrobe shop. Its own table, not a PlayerRecords column,
    /// so the checkpoint - which writes PlayerRecords from the live payload -
    /// can never put an old balance back.
    /// </summary>
    public class PlayerWardrobe
    {
        public long PlayerId { get; set; }
        public long Threads { get; set; }
    }
}

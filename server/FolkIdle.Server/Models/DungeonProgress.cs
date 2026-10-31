namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One player's standing in one dungeon (Kind = DungeonKind: 0 Equipment,
    /// 1 XP, 2 Gold): free tickets, the UTC day they were last topped up, the
    /// highest stage cleared, and which stages' first clear has paid (bit N =
    /// stage N).
    /// </summary>
    /// <remarks>
    /// Modul: ONE ROW PER DUNGEON, NOT COLUMNS PER DUNGEON. The equipment
    /// dungeon shipped as four PlayerRecords columns; three dungeons would have
    /// been twelve, and the roster (owner, 2026-10-31: up to three characters
    /// per run) would add more. The AddDungeonProgress migration copied those
    /// four columns into Kind 0 rows; the old columns are no longer read and
    /// can be dropped once production has run on this table for a while.
    ///
    /// A missing row is a dungeon never opened: tickets top up to the cap on
    /// its first read (TicketDay 0 is the epoch).
    /// </remarks>
    public class DungeonProgress
    {
        public long PlayerId { get; set; }
        public int Kind { get; set; }
        public int Tickets { get; set; }
        public int TicketDay { get; set; }
        public int BestStage { get; set; }
        public int FirstClearMask { get; set; }
    }
}

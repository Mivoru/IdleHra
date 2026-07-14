namespace FolkIdle.Server.Models
{
    // Modul 13.4.3: permanent ledger of first-time region completions (5
    // standard/elite monsters + 1 regional boss, all killcount >= 1000 - see
    // CodexEngine's region-grouping formula). Distinct from the recomputed
    // CompletedAreaFlags bitmask cached on TickStatePayload/PlayerRecord state:
    // this table is the durable source of truth a completion transition is
    // checked against so the same region is never re-granted. Composite key
    // (PlayerId, RegionId) configured via Fluent API in
    // FolkIdleDbContext.OnModelCreating, matching the existing
    // MonsterCodexEntry/PlayerRaceMastery convention.
    public class PlayerRegionCompletion
    {
        public long PlayerId { get; set; }
        public int RegionId { get; set; }
        public long CompletedAtEpoch { get; set; }
    }
}

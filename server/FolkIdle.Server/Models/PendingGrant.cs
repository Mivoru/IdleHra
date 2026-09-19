using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: THE OUTBOX. A grant that already succeeded at deciding WHAT to
    // give a player (a rolled item, a coalesced set of material deltas) but
    // failed to WRITE it - the exact gap CombatLootEngine's per-item
    // try/catch left open: fault isolation, not fault recovery. A row here
    // is resolved data, never "redo this random roll" - see
    // PendingGrantOutbox's own doc comment for why that distinction is load-
    // bearing.
    //
    // Expected to be near-empty in steady state: a successful drain DELETES
    // its row (the drain worker), so this table's size tracks "grants
    // currently failing to apply", not playtime. Unlike EquipmentInstances
    // (which reached 17,836 rows and needed windowing), a permanently full
    // pending_grants table is itself the incident, not a slow screen.
    [Table("pending_grants")]
    public class PendingGrant
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>Which subsystem produced this row. See PendingGrantSourceType.</summary>
        public int SourceType { get; set; }

        /// <summary>
        /// Idempotency key half 2 of 2, with (PlayerId, SourceType). A
        /// per-process, per-SourceType monotonic counter minted once at
        /// enqueue time - not derived from the payload. See
        /// PendingGrantOutbox.NextSourceSequence.
        /// </summary>
        public long SourceSequence { get; set; }

        /// <summary>"commodity_deltas" or "equipment_grant". See PendingGrantPayloadKind.</summary>
        [MaxLength(32)]
        public string PayloadKind { get; set; } = string.Empty;

        public string PayloadJson { get; set; } = string.Empty;

        public long CreatedAtEpochMs { get; set; }

        /// <summary>Eligible for the next drain cycle once this passes. Never null - a fresh row is eligible immediately.</summary>
        public long NextAttemptAtEpochMs { get; set; }

        public int AttemptCount { get; set; }

        [MaxLength(512)]
        public string? LastError { get; set; }

        /// <summary>Null while retries continue. Stamped, never deleted - see AccountPenalty for the same convention.</summary>
        public long? DeadLetteredAtEpochMs { get; set; }
    }

    public static class PendingGrantSourceType
    {
        public const int CombatLoot = 1;
        public const int Gathering = 2;
        public const int OfflineVillageProduction = 3;
    }

    public static class PendingGrantPayloadKind
    {
        /// <summary>PayloadJson is a Dictionary&lt;string, long&gt; of ItemId -> delta (gold included as "gold").</summary>
        public const string CommodityDeltas = "commodity_deltas";

        /// <summary>PayloadJson is one EquipmentGrantPayload (BaseItemId, QualityTier, AffixPayload).</summary>
        public const string EquipmentGrant = "equipment_grant";
    }
}

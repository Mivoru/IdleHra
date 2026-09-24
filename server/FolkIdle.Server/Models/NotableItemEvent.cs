using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: THE DROP RECORD, half two - A ROW FOR EVERYTHING RARE (task 26).
    //
    // Legendary and above (about 1.2% of drops), every forge fusion and every
    // first-clear trophy. The question task 26 had to reconstruct from row ids
    // and affix-key shapes - "were the old Godly pieces dropped or forged?" -
    // is a WHERE clause here. Index (PlayerId, CreatedAtUtc), set in
    // FolkIdleDbContext.
    [Table("notable_item_events")]
    public class NotableItemEvent
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>The piece, when it exists as a row. Null for a mail/claim-style grant with no row yet.</summary>
        public long? EquipmentInstanceId { get; set; }

        [MaxLength(128)]
        public string BaseItemId { get; set; } = string.Empty;

        /// <summary>Engine.DropSource.</summary>
        public short Source { get; set; }

        /// <summary>What the rarity roll produced (the tier before a fusion, for the forge).</summary>
        public short RolledTier { get; set; }

        /// <summary>Where it landed. Above RolledTier means fleece, elevation or a fusion lifted it.</summary>
        public short FinalTier { get; set; }

        /// <summary>The loot luck the roll used; 0 for sources that do not roll.</summary>
        public float LootLuckPct { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}

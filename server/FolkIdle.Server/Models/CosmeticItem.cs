using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// Task 54: one owned cosmetic thing - an unopened chest, an avatar or a
    /// frame. One ROW per thing, duplicates included, because a row is what
    /// the market moves and what a chest is opened into.
    ///
    /// DefinitionId is a CosmeticRegistry id (`chest_rare`, `avatar_malakor`,
    /// `frame_knotwork`); Kind and Rarity are copied from it at insert so a
    /// query by rarity needs no registry join. IsListed is true while the row
    /// sits on the market: it stays the seller's until sold, and it cannot be
    /// opened, worn or listed twice meanwhile.
    ///
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md §3.
    /// </summary>
    [Table("cosmetic_items")]
    public class CosmeticItem
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        public byte Kind { get; set; }

        [MaxLength(48)]
        public string DefinitionId { get; set; } = string.Empty;

        public byte Rarity { get; set; }

        public byte Source { get; set; }

        public DateTime AcquiredAtUtc { get; set; }

        public bool IsListed { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    // Modul: Full-Stack Expansion, Part 1. One material stack in a
    // player's Village Stash - the overflow/long-term storage tier of the
    // unified inventory (see InventoryAndStashSystem). The active
    // "backpack" tier remains CommodityRecords; consumers check
    // Backpack + Stash and drain Backpack first. Quantity is capped at
    // MaxStackQuantity per stack at every deposit site; the table itself
    // is unbounded (the "infinite" stash - unlimited stacks, bounded
    // stack height). Uniqueness on (PlayerId, ItemId) is enforced by a
    // unique index in FolkIdleDbContext.OnModelCreating.
    public class VillageStashInstance
    {
        public const long MaxStackQuantity = 9999L;

        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        [Required]
        [MaxLength(100)]
        public string ItemId { get; set; } = string.Empty;

        public long Quantity { get; set; }
    }
}

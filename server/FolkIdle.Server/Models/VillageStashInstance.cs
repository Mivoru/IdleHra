using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    // Modul: Full-Stack Expansion, Part 1. One material stack in a player's
    // Village Chest - the long-term storage tier of the unified inventory (see
    // InventoryAndStashSystem). The active "backpack" tier remains
    // CommodityRecords; consumers check Backpack + Chest and drain Backpack
    // first. Uniqueness on (PlayerId, ItemId) is enforced by a unique index in
    // FolkIdleDbContext.OnModelCreating.
    //
    // Modul: unlimited village chest. Stack height used to be capped at 9999,
    // and DepositToStashAsync returned whatever would not fit for the caller to
    // deal with - which in practice meant nobody dealt with it, since the only
    // sensible thing a caller could do was hand the overflow back to a backpack
    // that was full enough to be depositing in the first place.
    //
    // The chest is now genuinely unbounded: unlimited stacks AND unlimited
    // stack height. It is the one place in the game a player can put something
    // and know it is safe, and every consumption path already reads through
    // Backpack + Chest, so stored materials stay spendable at the workbench,
    // the forge and the market without being carried back out first. A cap here
    // only ever produced silent item loss at the exact moment a player had
    // succeeded at the game.
    //
    // long.MaxValue is the real ceiling now, which the economy cannot approach:
    // Quantity is a bigint and the largest per-drop grant in the game is single
    // digits.
    public class VillageStashInstance
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        [Required]
        [MaxLength(255)]
        public string ItemId { get; set; } = string.Empty;

        public long Quantity { get; set; }
    }
}

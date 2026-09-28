using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// Task 54 phase 4: one cosmetic (or unopened chest) on the market, at the
    /// price its seller chose - no corridor, by the owner's decision.
    ///
    /// Modul: A TABLE OF ITS OWN, NOT A ROW IN MarketOrderRecords. The
    /// equipment order book, its browser, its matching engine and its price
    /// history all read that table assuming an EquipmentInstance behind every
    /// SELL; a cosmetic order there would be an equipment listing with no
    /// equipment. The item itself never moves while listed: its cosmetic_items
    /// row keeps the seller's PlayerId with IsListed = true, and a sale changes
    /// the PlayerId. Completed sales still go to HistoricalMarketArchives
    /// (OrderType "COSMETIC"), so the fee economy counts them.
    ///
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md §3.
    /// </summary>
    [Table("cosmetic_market_listings")]
    public class CosmeticListing
    {
        [Key]
        public long Id { get; set; }

        public long SellerId { get; set; }

        public long CosmeticItemId { get; set; }

        [MaxLength(48)]
        public string DefinitionId { get; set; } = string.Empty;

        public byte Kind { get; set; }

        public byte Rarity { get; set; }

        public long Price { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}

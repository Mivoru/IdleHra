using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class MarketOrderRecord
    {
        [Key]
        public long Id { get; set; }
        
        public long SellerId { get; set; }
        
        public long? CommodityId { get; set; }
        public CommodityRecord? Commodity { get; set; }
        
        public long? EquipmentInstanceId { get; set; }
        public MarketEquipmentInstance? EquipmentInstance { get; set; }
        
        public long Price { get; set; }
        
        public int Status { get; set; }

        [Required]
        [MaxLength(10)]
        public string OrderType { get; set; } = "SELL";

        [Required]
        [MaxLength(255)]
        public string BaseItemId { get; set; } = string.Empty;

        public int QualityTier { get; set; }

        public bool IsQuarantined { get; set; }

        // Modul 40: deterministic tiebreak for FetchActiveListingsAsync's
        // Price-ascending sort (older listings surface first at an identical
        // price), set once at insert time from the same monotonic clock used
        // elsewhere in this codebase (see HistoricalMarketArchive.ExecutionTimestampEpoch).
        public long CreatedAtEpoch { get; set; }
    }
}

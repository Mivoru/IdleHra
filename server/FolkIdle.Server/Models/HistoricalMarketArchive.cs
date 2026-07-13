using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("historical_market_archives")]
    public class HistoricalMarketArchive
    {
        [Key]
        public long ArchiveId { get; set; }
        
        public long OriginalOrderId { get; set; }
        public long SellerId { get; set; }
        public long BuyerId { get; set; }
        
        public long? CommodityId { get; set; }
        public long? EquipmentInstanceId { get; set; }
        
        public long ExecutionPrice { get; set; }
        public long FeeBurned { get; set; }
        
        [Required]
        [MaxLength(10)]
        public string OrderType { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string BaseItemId { get; set; } = string.Empty;

        public int QualityTier { get; set; }
        
        public bool IsQuarantined { get; set; }
        
        public long ExecutionTimestampEpoch { get; set; }
    }
}

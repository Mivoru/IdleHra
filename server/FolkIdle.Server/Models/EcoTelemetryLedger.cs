using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class EcoTelemetryLedger
    {
        [Key]
        public long LogId { get; set; }
        public long Timestamp { get; set; }
        public long TotalGoldMinted { get; set; }
        public long TotalGoldConsumed { get; set; }
        public long TotalDiamondsMinted { get; set; }
        public long TotalDiamondsConsumed { get; set; }
        public double CalculatedRatio { get; set; }
    }
}

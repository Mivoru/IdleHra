using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class PrimaryPurchaseLedger
    {
        [Key]
        [StringLength(64)]
        public string TransactionId { get; set; } = string.Empty;
        public long PlayerId { get; set; }
        [StringLength(64)]
        public string ProductId { get; set; } = string.Empty;
        public byte PurchaseState { get; set; } // 1 = Completed, 2 = Refunded
        public long TimestampProcessed { get; set; }
    }
}

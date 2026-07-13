using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class EventHorizonPremiumLedger
    {
        [Key]
        public long Id { get; set; }
        
        [StringLength(64)]
        public string TransactionId { get; set; } = string.Empty;
        
        public long PlayerId { get; set; }
        
        public int PreviousBalance { get; set; }
        
        public int NewBalance { get; set; }
        
        public long Timestamp { get; set; }
    }
}

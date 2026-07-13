using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class MailboxInstance
    {
        [Key]
        public long Id { get; set; }
        public long PlayerId { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public int QualityTier { get; set; }
        public int Quantity { get; set; }
        public bool IsClaimed { get; set; }
        public bool IsPending { get; set; }
        public long GoldAttachment { get; set; }
        public long? AttachedEquipmentId { get; set; }
        public long ReceivedTimestamp { get; set; }
    }
}

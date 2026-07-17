using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class CommodityRecord
    {
        [Key]
        public long Id { get; set; }
        
        [Required]
        [MaxLength(255)]
        public string ItemId { get; set; } = string.Empty;
        
        public long PlayerId { get; set; }

        public long Quantity { get; set; }
    }
}

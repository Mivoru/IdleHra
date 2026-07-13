using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    public class EquipmentInstance
    {
        [Key]
        public long Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string BaseItemId { get; set; } = string.Empty;

        public long PlayerId { get; set; }
        
        public int QualityTier { get; set; }
        
        [Column(TypeName = "jsonb")]
        public string AffixPayload { get; set; } = "{}";
        public bool IsAffixLocked { get; set; }
    }
}

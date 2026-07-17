using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class BankEquipmentInstance
    {
        [Key]
        public long Id { get; set; }
        
        [Required]
        [MaxLength(255)]
        public string BaseItemId { get; set; } = string.Empty;

        public long PlayerId { get; set; }
        
        public int QualityTier { get; set; }
        
        [Column(TypeName = "jsonb")]
        public string AffixPayload { get; set; } = "{}";
        
        public bool IsAffixLocked { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class EquipmentInstance
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

        // Modul: Architecture Overhaul, Part 4. Equipment set membership -
        // 0 means "not part of any set" (the vast majority of dropped/
        // crafted gear). Non-zero values match SetBonusEngine's known set
        // catalog (e.g. 1 = Chiming Steel, 10 = Eternal Dreadnought).
        public int SetId { get; set; }
    }
}

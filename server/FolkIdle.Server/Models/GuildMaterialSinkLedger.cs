using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class GuildMaterialSinkLedger
    {
        [Key]
        public long Id { get; set; }
        
        public long GuildId { get; set; }
        public GuildRecord? Guild { get; set; }
        
        [Required]
        [MaxLength(255)]
        public string CommodityId { get; set; } = string.Empty;
        
        public long TotalAmountContributed { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    public class GuildMaterialSinkLedger
    {
        [Key]
        public long Id { get; set; }
        
        public long GuildId { get; set; }
        public GuildRecord? Guild { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string CommodityId { get; set; } = string.Empty;
        
        public long TotalAmountContributed { get; set; }
    }
}

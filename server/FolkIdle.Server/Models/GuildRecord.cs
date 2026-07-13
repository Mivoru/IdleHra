using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class GuildRecord
    {
        [Key]
        public long Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;
        
        public long TotalGoldContributed { get; set; }
        
        public int CurrentTier { get; set; }

        public int MiningMonolithLevel { get; set; }
        public int MiningMonolithProgress { get; set; }
        public int WoodcuttingMonolithLevel { get; set; }
        public int WoodcuttingMonolithProgress { get; set; }
        public int GuildMMR { get; set; } = 1000;
        public int MaxMembers { get; set; } = 10;
        public int ActiveMembers { get; set; } = 1;
    }
}

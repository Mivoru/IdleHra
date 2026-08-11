using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    public class GuildActiveBuff
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        public long GuildId { get; set; }

        [Required]
        [MaxLength(50)]
        public string BuffType { get; set; } = string.Empty;

        public int Tier { get; set; }

        public DateTime ExpiresAt { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class GuildWarDefensiveSnapshot
    {
        [Key]
        public long GuildId { get; set; }
        public string RosterPayloadJson { get; set; } = string.Empty;
    }
}

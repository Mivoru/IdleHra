using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    public class GuildDefenseRoster
    {
        [Key]
        public long GuildId { get; set; }
        public int RegionShardId { get; set; }

        [Column(TypeName = "jsonb")]
        public string DefensiveStatsJson { get; set; } = "{}";
    }
}

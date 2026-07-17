using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

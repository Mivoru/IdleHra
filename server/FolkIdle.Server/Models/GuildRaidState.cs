using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    // Co-op PvE guild raid boss state. One row per guild, distinct from the
    // PvP GuildWarActiveMatch/GuildCombatSimulationEngine system.
    public class GuildRaidState
    {
        [Key]
        public long GuildId { get; set; }

        public int RaidTier { get; set; }
        public long RaidBossCurrentHp { get; set; }
        public long RaidBossMaxHp { get; set; }
    }
}

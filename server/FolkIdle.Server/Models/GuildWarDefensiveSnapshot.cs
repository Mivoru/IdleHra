using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class GuildWarDefensiveSnapshot
    {
        [Key]
        public long GuildId { get; set; }
        public string RosterPayloadJson { get; set; } = string.Empty;
    }
}

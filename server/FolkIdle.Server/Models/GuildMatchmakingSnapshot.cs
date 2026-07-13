using System;
using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class GuildMatchmakingSnapshot
    {
        [Key]
        public Guid MatchUuid { get; set; }
        public long AttackerGuildId { get; set; }
        public long DefenderGuildId { get; set; }
        public long GlobalNodeMaxHp { get; set; }
        public long GlobalNodeRemainingHp { get; set; }
        public int TournamentGroupIndex { get; set; }
        public bool IsComplete { get; set; }
        public int ActiveMatchMmr { get; set; }
        public long FencingToken { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class MentorshipContract
    {
        [Key]
        public long ContractId { get; set; }
        public long MentorPlayerId { get; set; }
        public long MenteePlayerId { get; set; }
        public double ExpBonusMultiplier { get; set; }
        public long TimestampEstablished { get; set; }
    }
}

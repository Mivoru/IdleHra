using System.ComponentModel.DataAnnotations;

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

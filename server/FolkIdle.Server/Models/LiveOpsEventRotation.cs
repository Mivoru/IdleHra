using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class LiveOpsEventRotation
    {
        [Key]
        public int EventId { get; set; }
        public byte EventType { get; set; }
        public uint ModifierBitmask { get; set; }
        public long EndTimestamp { get; set; }
    }
}

using System.ComponentModel.DataAnnotations;

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

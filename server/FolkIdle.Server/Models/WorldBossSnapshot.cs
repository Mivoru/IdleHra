using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class WorldBossSnapshot
    {
        [Key]
        public long BossInstanceId { get; set; }
        public long MaxHp { get; set; }
        public long CurrentHp { get; set; }
        public long TotalDamageContributed { get; set; }
        public long LastActiveTimestamp { get; set; }
    }
}

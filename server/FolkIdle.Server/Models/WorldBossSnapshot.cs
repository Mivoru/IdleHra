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

        // 0 = Inactive (no event window open), 1 = Active (event window open, attacks allowed),
        // 2 = Concluded (window closed, either defeated or failed, dormant until next window).
        public byte EventState { get; set; }
        public long EventEndEpoch { get; set; }
    }
}

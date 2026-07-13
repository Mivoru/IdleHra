using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("player_race_masteries")]
    public class PlayerRaceMastery
    {
        [Key, Column(Order = 0)]
        public long PlayerId { get; set; }

        [Key, Column(Order = 1)]
        public int RaceId { get; set; }

        public int MasteryLevel { get; set; }
        public long CumulativeXp { get; set; }
    }
}

using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("VillageInfrastructures")]
    public class VillageInfrastructure
    {
        public long PlayerId { get; set; }
        public int BuildingId { get; set; }
        public int CurrentLevel { get; set; }
    }
}

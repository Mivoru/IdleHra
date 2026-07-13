using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("VillageResidents")]
    public class VillageResident
    {
        public long PlayerId { get; set; }
        public int SlotIndex { get; set; }
        public bool IsActive { get; set; }
        public double EfficiencyModifier { get; set; }
    }
}

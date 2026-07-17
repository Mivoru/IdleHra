using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

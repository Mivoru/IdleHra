using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    public class EquipmentAffixMatrix
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public int AffixId { get; set; }
        
        public byte StatType { get; set; }
        public int MinBaseValue { get; set; }
        public int MaxBaseValue { get; set; }
        public double GeometricalScalingFactor { get; set; }
    }
}

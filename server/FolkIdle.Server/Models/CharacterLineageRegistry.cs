using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("character_lineage_registry")]
    public class CharacterLineageRegistry
    {
        [Key]
        [ForeignKey("Character")]
        public Guid CharacterId { get; set; }
        
        public Guid? ParentPaternalId { get; set; }
        public Guid? ParentMaternalId { get; set; }
        
        public int GenerationIndex { get; set; }
        public long GeneticVector { get; set; }

        public CharacterRecord? Character { get; set; }
    }
}

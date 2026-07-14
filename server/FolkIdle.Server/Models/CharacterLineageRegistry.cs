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

        // Modul 13.4.3: set when BreedingEngine's grand-mutation roll (separate
        // from GeneticSplicingEngine's per-locus allele noise) triggers on this
        // child's birth. Persisted as a marker only for now - not yet consumed
        // by RaceAttributeGrowth/StatsCalculator, matching the existing
        // LocusSpeed/LocusCrit/LocusYield loci which are also bred but not yet
        // read anywhere downstream.
        public bool IsEpicMutation { get; set; }

        // Modul 13.4.3: set by BreedingEngine when both candidate parents share
        // a common ancestor within 2 generations (parent-child pairing, or full/
        // half siblings sharing a parent). Consumed by RaceAttributeGrowth to
        // apply a -25% level-up growth penalty for this character's lifetime.
        public bool IsInbred { get; set; }

        public CharacterRecord? Character { get; set; }
    }
}

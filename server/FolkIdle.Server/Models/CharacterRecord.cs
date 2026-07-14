using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("characters")]
    public class CharacterRecord
    {
        [Key]
        public Guid Id { get; set; }
        
        public long PlayerId { get; set; }
        public int Level { get; set; }
        public int AgePhase { get; set; } = 1; // 0 = Child, 1 = Adult, 2 = Senior, 3 = Old
        public long AgeTicks { get; set; } = 0;
        public bool IsLockedInEscrow { get; set; }

        // Modul 13.4.3: Breeding Grounds cooldown gate. Set on both parents by
        // BreedingEngine after a successful breed; ExecuteBreedingAsync rejects
        // a new attempt while BreedingCooldownEndEpoch is still in the future.
        public bool IsBreedingActive { get; set; }
        public long BreedingCooldownEndEpoch { get; set; }

        // Relationship to lineage
        public CharacterLineageRegistry? Lineage { get; set; }
    }
}

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    [Table("characters")]
    public class CharacterRecord
    {
        [Key]
        public Guid Id { get; set; }
        
        public long PlayerId { get; set; }
        public int Level { get; set; }

        // Modul: Architecture Overhaul, Part 2. Multi-character slots.
        // SlotIndex 0 is the main character (always unlocked); 1 and 2
        // unlock progressively as the player's main character levels up -
        // see CharacterSlotEngine.IsSlotUnlocked. ActiveActivityId mirrors
        // TickStatePayload.ActiveActivityId's semantics (0 = idle) but is
        // tracked per character row so CharacterSlotEngine can detect two
        // characters belonging to the same player occupying the identical
        // gathering or combat node.
        public int SlotIndex { get; set; }
        public long ActiveActivityId { get; set; }
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

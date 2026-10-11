using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One pet an account owns (seasonal events). PetId is a PetRegistry id
    /// (`pet_ghostie`); an account owns each pet at most ONCE (unique index)
    /// and a pet follows at most one character, CharacterId, null while it
    /// rests. One pet per character is the second unique index. Owner,
    /// 2026-10-09: one pet per character, so the collection is the ceiling and
    /// bonuses cannot grow with the character count.
    ///
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md §3.
    /// </summary>
    [Table("player_pets")]
    public class PlayerPet
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        [MaxLength(48)]
        public string PetId { get; set; } = string.Empty;

        public Guid? CharacterId { get; set; }

        /// <summary>
        /// Which of the character's pet slots it sits in: 0, or 1 once the
        /// account has RebirthRules.SecondPetSlotRebirths rebirths (task 114).
        /// Meaningless while the pet rests. The unique index is
        /// (CharacterId, Slot), so each slot still holds one pet.
        /// </summary>
        public byte Slot { get; set; }

        public DateTime AcquiredAtUtc { get; set; }
    }
}

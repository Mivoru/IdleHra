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

        /// <summary>
        /// When the player was shown the "a pet found you" window for it, or
        /// null while that window is still owed. Kept on the server rather than
        /// the device so a pet found offline, or seen on the phone, is shown
        /// exactly once on every client. A shop purchase is born announced -
        /// the player chose it.
        /// </summary>
        public DateTime? AnnouncedAtUtc { get; set; }

        /// <summary>
        /// Monster-pet duplicates found since the player last closed the
        /// window: each one paid PetRegistry.DuplicateDiamonds by mail, and the
        /// window says how many. Reset with AnnouncedAtUtc.
        /// </summary>
        public int PendingDuplicates { get; set; }
    }
}

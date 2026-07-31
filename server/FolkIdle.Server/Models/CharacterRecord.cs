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

        // Modul: breeding pairs. A character's sex. There was no such concept:
        // ExecuteBreedingAsync simply took two character ids and labelled them
        // "paternal" and "maternal", so a character could be bred with itself's
        // own kind in any combination and the labels meant nothing.
        //
        // It matters now because races are granted as breeding PAIRS - a new
        // account starts with one male and one female Human, and each region
        // boss's first kill grants a male/female pair of the unlocked race. A
        // pair that cannot actually breed would be a decoration, since
        // BreedingEngine also requires both parents to share a race and there
        // is no other way to obtain a second character of a non-Human race.
        //
        // false = male, true = female. A bool rather than an enum because there
        // are exactly two breeding roles and the parent columns are already
        // named paternal/maternal.
        public bool IsFemale { get; set; }

        // Modul: per-character equipment. These six slots used to live on
        // PlayerRecord, which meant all three of a player's characters shared
        // one weapon, one chest piece and one pair of leggings.
        //
        // That was incoherent the moment more than one character could work at
        // once: a character mining needs a pickaxe while another fights with a
        // sword and a third fishes with a rod, and a single account-wide weapon
        // slot cannot hold three things. Gear belongs to whoever is wearing it.
        //
        // The slot set also widened from three (Weapon / Armor / Leggings) to
        // six. AffixRegistry.EquipmentSlotMask has always modelled Helmet,
        // Chest, Leggings, Boots, Gloves and Weapon separately, and
        // AffixRegistry.ResolveSlot has always matched the corresponding
        // "_helmet_armor_slot_", "_gloves_armor_slot_" and "_boots_armor_slot_"
        // BaseId markers - so helmets, gloves and boots already rolled
        // slot-correct affixes that no equip slot could ever receive. The old
        // "Armor" slot swallowed all four armour pieces into one, and three
        // quarters of the armour catalogue was unwearable.
        //
        // Nullable: null means the slot is empty. Every value is an
        // EquipmentInstances.Id.
        public long? EquippedWeaponId { get; set; }
        public long? EquippedHelmetId { get; set; }
        public long? EquippedChestId { get; set; }
        public long? EquippedGlovesId { get; set; }
        public long? EquippedLeggingsId { get; set; }
        public long? EquippedBootsId { get; set; }

        // Modul: offhand slot. The seventh slot, added for exactly the reason
        // the six-slot widening above describes: AffixRegistry.EquipmentSlotMask
        // already included Shield and ResolveSlot already matched the
        // "_helper_offhand_" marker, so the five authored helper items
        // (buckler / quiver / aegis / bulwark, one per region tier) rolled
        // slot-correct affixes with nowhere to be worn.
        public long? EquippedOffhandId { get; set; }

        // Modul 13.4.3: Breeding Grounds cooldown gate. Set on both parents by
        // BreedingEngine after a successful breed; ExecuteBreedingAsync rejects
        // a new attempt while BreedingCooldownEndEpoch is still in the future.
        public bool IsBreedingActive { get; set; }
        public long BreedingCooldownEndEpoch { get; set; }

        // Relationship to lineage
        public CharacterLineageRegistry? Lineage { get; set; }
    }
}

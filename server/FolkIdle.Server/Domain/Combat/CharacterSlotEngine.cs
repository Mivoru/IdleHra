using System;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: Architecture Overhaul, Part 2. Multi-character slot gating and
    // position-occupancy mutex. A player's second and third character slots
    // are progressive rewards for leveling their first (main) character;
    // once unlocked, two characters belonging to the same player must never
    // be permitted to run the identical gathering/combat activity id at the
    // same time, since idle-tick yield is computed per activity assignment
    // and simultaneous multi-farming of one node would double-count drops.
    public static class CharacterSlotEngine
    {
        public const int MaxCharacterSlots = 3;
        public const int Slot1UnlockLevel = 30;
        public const int Slot2UnlockLevel = 60;

        public static int GetSlotUnlockLevelRequirement(int slotIndex)
        {
            return slotIndex switch
            {
                0 => 1,
                1 => Slot1UnlockLevel,
                2 => Slot2UnlockLevel,
                _ => int.MaxValue
            };
        }

        public static bool IsSlotUnlocked(int slotIndex, int mainCharacterLevel)
        {
            if (slotIndex < 0 || slotIndex >= MaxCharacterSlots)
            {
                return false;
            }
            return mainCharacterLevel >= GetSlotUnlockLevelRequirement(slotIndex);
        }

        // Zero-allocation occupancy scan. activeActivityIds holds each of the
        // player's character slots' current activity assignment (0 = idle),
        // indexed by SlotIndex. Returns true when a slot other than
        // requestingSlotIndex already runs targetActivityId - a target of 0
        // (going idle) can never collide.
        public static bool IsActivityOccupiedByAnotherSlot(ReadOnlySpan<long> activeActivityIds, int requestingSlotIndex, long targetActivityId)
        {
            if (targetActivityId <= 0)
            {
                return false;
            }

            for (int i = 0; i < activeActivityIds.Length; i++)
            {
                if (i == requestingSlotIndex)
                {
                    continue;
                }
                if (activeActivityIds[i] == targetActivityId)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

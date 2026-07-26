using System;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: Architecture Overhaul, Part 2. Multi-character slot gating and
    // position-occupancy mutex. Once a slot is unlocked, two characters
    // belonging to the same player must never run the identical
    // gathering/combat activity id at the same time, since idle-tick yield is
    // computed per activity assignment and simultaneous multi-farming of one
    // node would double-count drops.
    //
    // Modul: Town Hall slot gating. The unlock requirement moved from the main
    // character's LEVEL (30 / 60) to the Town Hall's level (3 / 5).
    //
    // Level was the wrong axis. Reaching level 30 is a pure function of leaving
    // combat running, so the extra slots arrived on a timer, rewarded no
    // decision, and had nothing to do with the village they were meant to
    // populate. The Town Hall caps at
    // VillageManagementEngine.MaxStructuralBuildingLevel (5) and is upgraded
    // exclusively with raw_log and copper_ore through the unified
    // Backpack+Stash path, so it can only be raised by actually gathering -
    // which is the thing extra character slots exist to do more of. Slot 2 at
    // Town Hall 3 therefore lands right after a player has had to farm a
    // region's wood and ore; slot 3 requires maxing the building.
    //
    // It also composes with the existing ceiling rule
    // (GetMaxBuildingLevelCeiling = 2 + level*2): the Town Hall already gates
    // every other building's level, so hanging character slots off it puts the
    // whole village on one progression spine instead of two unrelated ones.
    public static class CharacterSlotEngine
    {
        public const int MaxCharacterSlots = 3;

        // Town Hall levels required for the second and third slots. Both sit
        // within the building's hard cap of 5.
        public const int Slot2TownHallRequirement = 3;
        public const int Slot3TownHallRequirement = 5;

        public static int GetSlotUnlockTownHallRequirement(int slotIndex)
        {
            return slotIndex switch
            {
                0 => 0,
                1 => Slot2TownHallRequirement,
                2 => Slot3TownHallRequirement,
                _ => int.MaxValue
            };
        }

        public static bool IsSlotUnlocked(int slotIndex, int townHallLevel)
        {
            if (slotIndex < 0 || slotIndex >= MaxCharacterSlots)
            {
                return false;
            }
            return townHallLevel >= GetSlotUnlockTownHallRequirement(slotIndex);
        }

        // How many slots the player may use at once. The tick loop reads this
        // to decide how many characters to simulate, so it has to agree with
        // IsSlotUnlocked exactly - a slot that simulates but cannot be
        // assigned, or the reverse, is the same split-brain that produced
        // "assigned but nothing ever happens" elsewhere in this codebase.
        public static int GetUnlockedSlotCount(int townHallLevel)
        {
            int unlocked = 0;
            for (int slotIndex = 0; slotIndex < MaxCharacterSlots; slotIndex++)
            {
                if (IsSlotUnlocked(slotIndex, townHallLevel)) unlocked++;
            }
            return unlocked;
        }

        // Zero-allocation occupancy scan. activeActivityIds holds each of the
        // player's character slots' current activity assignment (0 = idle),
        // indexed by SlotIndex. Returns true when a slot other than
        // requestingSlotIndex already runs targetActivityId - a target of 0
        // (going idle) can never collide.
        //
        // This is the entirety of the assignment rule: any character may do
        // anything, combat included, so long as no two of them are doing the
        // SAME thing. Two characters fighting different monsters, or one
        // fishing while another mines, are both legal; two on one monster or
        // one node are not.
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

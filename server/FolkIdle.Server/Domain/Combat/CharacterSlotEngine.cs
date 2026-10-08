using System;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: Architecture Overhaul, Part 2. Multi-character slot gating and
    // position-occupancy mutex. Once a slot is unlocked, two characters
    // belonging to the same player must never do the same KIND of work at the
    // same time (see ActivityCategory) - one fighter, one woodcutter, one
    // miner, one fisher, one crafter.
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

        // Modul: ONE CHARACTER PER KIND OF WORK, 2026-10-08.
        //
        // The rule used to be "never the SAME activity id": two characters
        // fighting two different monsters, or mining two different veins, were
        // both legal. The owner's rule is one character per kind of work -
        // one fighter, one woodcutter, one miner, one fisher, one crafter - so
        // three characters spread the account across the world instead of
        // stacking on whichever loop pays best. The category is what is
        // compared now; the id only decides which category it falls in.
        //
        // Pure integer bands (see ActivityIdBands), so the 10Hz tick and the
        // automation rules can ask it without a lookup.
        public const int CategoryNone = 0;
        public const int CategoryCombat = 1;
        public const int CategoryWoodcutting = 2;
        public const int CategoryMining = 3;
        public const int CategoryFishing = 4;
        public const int CategoryRetiredHerbalism = 5;
        public const int CategoryCrafting = 6;
        public const int CategoryWorldBoss = 7;

        public static int ActivityCategory(long activityId)
        {
            if (activityId <= 0) return CategoryNone;
            if (ActivityIdBands.IsCombatActivity(activityId)) return CategoryCombat;
            if (ActivityIdBands.IsCraftingActivity(activityId)) return CategoryCrafting;
            if (activityId == ActivityIdBands.WorldBossActivityId) return CategoryWorldBoss;
            if (ActivityIdBands.IsGatheringActivity(activityId))
            {
                return (activityId / ActivityIdBands.BandSize) switch
                {
                    1 => CategoryWoodcutting,
                    2 => CategoryMining,
                    3 => CategoryFishing,
                    _ => CategoryRetiredHerbalism
                };
            }
            // Anything else is its own category: two characters can never both
            // hold an id nobody has classified, which errs on the side of the rule.
            return (int)System.Math.Min(int.MaxValue, 1000L + activityId);
        }

        public static bool IsSameKindOfWork(long left, long right)
        {
            int category = ActivityCategory(left);
            return category != CategoryNone && category == ActivityCategory(right);
        }

        // Zero-allocation occupancy scan. activeActivityIds holds each of the
        // player's character slots' current activity assignment (0 = idle),
        // indexed by SlotIndex. Returns true when a slot other than
        // requestingSlotIndex already does the same KIND of work as
        // targetActivityId - a target of 0 (going idle) can never collide.
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
                if (IsSameKindOfWork(activeActivityIds[i], targetActivityId))
                {
                    return true;
                }
            }
            return false;
        }

        // The live check, against the payload rather than the database. The
        // active register holds the character being decided for, so the other
        // two are the parked ones - whichever slots they are. Used by the
        // deploy command, its queue drain, the automation rules and the
        // Ascension start, which all change an activity on the live payload.
        public static bool IsKindOfWorkTakenByParkedSlot(in TickStatePayload payload, long activityId)
        {
            return IsSameKindOfWork(payload.Slot2Activity.ActiveActivityId, activityId)
                || IsSameKindOfWork(payload.Slot3Activity.ActiveActivityId, activityId);
        }

        // Grandfathering: accounts that already field two characters on one
        // kind of work (legal until 2026-10-08) keep the first in slot order
        // and the later ones go idle. Called at hydration, so the conflict
        // never reaches a tick; the checkpoint then writes the idles back.
        public static void ResolveKindOfWorkConflicts(Span<long> activityBySlot)
        {
            for (int i = 1; i < activityBySlot.Length; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (IsSameKindOfWork(activityBySlot[i], activityBySlot[j]))
                    {
                        activityBySlot[i] = 0;
                        break;
                    }
                }
            }
        }
    }
}

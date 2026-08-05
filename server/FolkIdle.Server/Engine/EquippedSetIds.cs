using System;

namespace FolkIdle.Server.Engine
{
    // Modul: seven-slot set bonuses. The SetId of every equipped slot, in one
    // unmanaged value type.
    //
    // Previously this was three loose ints - weapon, "armor", leggings -
    // threaded through the notification queue, TickStatePayload,
    // CharacterActivityState and a StatsCalculator.Calculate parameter list.
    // Three was never enough: the character has SEVEN equip slots, so
    // ComputeEquippedTotalsAsync folded helmet, chest, gloves and boots onto a
    // single "armor" id by taking whichever it happened to see first and
    // discarding the rest.
    //
    // The consequence was invisible but total: SetBonusEngine.Evaluate counts
    // how many equipped pieces share a SetId and awards 2-piece and 4-piece
    // tiers off that count. Fed three ids for a seven-piece loadout, the count
    // could never exceed 3, so **no 4-piece bonus in the game was reachable**,
    // and a full matching armour set counted as one piece rather than four.
    // The engine itself was always correct and always sized for this -
    // SetBonusEngine.MaxTrackedSlots is 8 and its own comment names all seven
    // slots. Only its caller was too narrow.
    //
    // Bundled rather than widened to seven loose ints for the same reason
    // EquippedAffixTotals was bundled: adding a slot should touch one type, not
    // five signatures.
    public struct EquippedSetIds
    {
        public int Weapon;
        public int Helmet;
        public int Chest;
        public int Gloves;
        public int Leggings;
        public int Boots;
        public int Amulet;
        public int Ring;

        // Modul: 7 -> 8. Offhand left, Amulet and Ring arrived - see
        // EquipmentSlotEngine on why an offhand slot was never in the design.
        public const int SlotCount = 8;

        // Writes the eight ids into a caller-provided span, which
        // StatsCalculator stackallocs. Zero allocation - this sits on the
        // 10Hz combat path.
        public void CopyTo(Span<int> destination)
        {
            if (destination.Length < SlotCount) return;

            destination[0] = Weapon;
            destination[1] = Helmet;
            destination[2] = Chest;
            destination[3] = Gloves;
            destination[4] = Leggings;
            destination[5] = Boots;
            destination[6] = Amulet;
            destination[7] = Ring;
        }

        // Modul: eight-slot set bonuses. Assigns by the slot indices
        // EquipmentSlotEngine already defines, so the mapping lives in one
        // place and a new slot cannot be silently dropped.
        public void SetBySlotIndex(int slotIndex, int setId)
        {
            switch (slotIndex)
            {
                case 0: Weapon = setId; break;
                case 1: Helmet = setId; break;
                case 2: Chest = setId; break;
                case 3: Gloves = setId; break;
                case 4: Leggings = setId; break;
                case 5: Boots = setId; break;
                case 6: Amulet = setId; break;
                case 7: Ring = setId; break;
            }
        }
    }
}

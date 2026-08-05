using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Which equipment each monster drops.
    ///
    /// THIS REPLACES A REGION-WIDE POOL, AND THE POOL WAS THE BUG.
    ///
    /// The old rule was "any item whose RegionTier matches the killed monster's
    /// region and whose BaseId contains this category substring", evaluated
    /// fresh per kill. Three things fell out of that, all of them visible to a
    /// player within an hour:
    ///
    /// 1. Every monster in a location dropped the same things, because they all
    ///    matched the same region. Killing a Field Mouse and killing a Wild Boar
    ///    were the same loot event with different art.
    /// 2. Only four categories were ever rolled - melee, ranged, magic, helper -
    ///    so ORDINARY MONSTERS DROPPED NOTHING BUT WEAPONS AND OFFHANDS. Armour
    ///    was on a boss-only roll, which is why a player watching their drops
    ///    saw one of every weapon type and a shield, forever, and never a
    ///    helmet.
    /// 3. The ranged category grepped for "_ranged_weapon_slot_" while every
    ///    canonical bow is authored "_range_weapon_slot_" - no "d". So the
    ///    canonical ranged weapons NEVER DROPPED AT ALL, in any region, and the
    ///    only bow that ever fell was a single legacy region-1 item that
    ///    happened to be spelled the other way.
    ///
    /// What replaces it: each location's droppable equipment is DEALT OUT across
    /// that location's five monsters, one item at a time, slot group by slot
    /// group. Dealing rather than filtering is what makes the two properties the
    /// design actually wants both true by construction:
    ///
    /// - **Every item drops from something.** Each candidate is dealt to exactly
    ///   one monster, so nothing is orphaned. `EquipmentDropTableTests` asserts
    ///   this over the whole catalogue rather than trusting the comment.
    /// - **Every monster drops a mix.** The deal walks slot groups in order with
    ///   a cursor that CONTINUES across group boundaries, so consecutive items
    ///   of one slot land on consecutive monsters and each monster ends up
    ///   holding several different slots. Every location authors at least five
    ///   weapons, so the weapon group alone gives every monster one.
    ///
    /// The tables are derived, not authored, so adding an item to items.json
    /// puts it in a monster's table on the next boot instead of quietly becoming
    /// unobtainable - which is precisely how thirty-three of them got that way.
    /// </summary>
    public static class EquipmentDropTable
    {
        // Slot groups are walked in THIS order. It is a content decision, not an
        // alphabetical one: armour first so the early items dealt to each
        // monster are the pieces a new character is missing, weapons last so
        // they spread across all five monsters from a full rotation rather than
        // clumping on whoever the cursor happened to reach first.
        private static readonly int[] SlotDealOrder =
        {
            EquipmentSlotEngine.SlotHelmet,
            EquipmentSlotEngine.SlotChest,
            EquipmentSlotEngine.SlotGloves,
            EquipmentSlotEngine.SlotLeggings,
            EquipmentSlotEngine.SlotBoots,
            EquipmentSlotEngine.SlotWeapon,
            EquipmentSlotEngine.SlotAmulet,
            EquipmentSlotEngine.SlotRing,
        };

        /// <summary>
        /// The five authored locations are RegionTiers 1-5. Items authored at
        /// RegionTier 6-10 are the endgame ladder above them - real, wearable
        /// gear whose equip gate (see RegionUnlockGate) already demands all five
        /// regions cleared - but no reachable monster carries those tiers, so
        /// before this they could not drop from anything at all.
        ///
        /// They are dealt to the FINAL BOSS. That is the one monster a player
        /// can only reach with all five regions cleared, which is exactly the
        /// condition for wearing them, so the drop and the gate agree. It also
        /// keeps them rare without a second rarity knob: they share one table
        /// with his ordinary share, so any single one of them is a chase item.
        /// </summary>
        private const int FirstEndgameRegionTier = 6;

        private static readonly Lazy<Dictionary<int, int[]>> _tables =
            new(Build, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// The item ids this monster can drop as equipment. Empty for a monster
        /// with no authored location - the 90 legacy monsters, which are not on
        /// the map and are not given a table rather than being given a wrong one.
        /// </summary>
        public static ReadOnlySpan<int> GetDrops(int monsterId)
        {
            return _tables.Value.TryGetValue(monsterId, out var drops)
                ? drops
                : ReadOnlySpan<int>.Empty;
        }

        /// <summary>
        /// Whether an item is equipment a monster could drop. Excludes tools
        /// (crafted from wood, never looted) and anything that resolves to no
        /// slot at all - the amulet and ring BaseIds in items.json name two
        /// slots this game does not have, so they are catalogue entries rather
        /// than obtainable gear and dropping them would hand players items they
        /// can never put on.
        /// </summary>
        public static bool IsDroppableEquipment(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return false;
            if (ContentRegistry.GetToolKind(baseItemId) >= 0) return false;

            int slot = EquipmentSlotEngine.ResolveSlotIndex(baseItemId);
            return slot >= EquipmentSlotEngine.SlotWeapon && slot <= EquipmentSlotEngine.LastGearSlot;
        }

        private static Dictionary<int, int[]> Build()
        {
            // A Lazy caches whatever the first caller produced, forever. If that
            // caller arrives before ContentRegistry.Initialize the catalogue is
            // empty, every table is built empty, and the game silently stops
            // dropping equipment for the lifetime of the process with nothing in
            // the log. Fail loudly on the boot order instead - the identical
            // hazard on _rawFishItemIds once produced a set of garbage ids that
            // indexed past the end of ItemDefinitions.
            if (ContentRegistry.ItemDefinitions.Length == 0)
            {
                throw new InvalidOperationException(
                    "EquipmentDropTable was built before ContentRegistry.Initialize ran; drop tables would be empty forever.");
            }

            var tables = new Dictionary<int, int[]>(ContentRegistry.LocationCount * ContentRegistry.MonstersPerRegion);
            var pending = new Dictionary<int, List<int>>(ContentRegistry.LocationCount * ContentRegistry.MonstersPerRegion);

            for (int location = 1; location <= ContentRegistry.LocationCount; location++)
            {
                int firstMonster = ContentRegistry.FirstCanonicalMonsterId + (location - 1) * ContentRegistry.MonstersPerRegion;
                for (int i = 0; i < ContentRegistry.MonstersPerRegion; i++)
                {
                    pending[firstMonster + i] = new List<int>(8);
                }

                // One cursor for the whole location, advanced across slot groups
                // rather than reset per group - see the class comment on why the
                // continuation is what mixes the slots.
                int cursor = 0;
                for (int s = 0; s < SlotDealOrder.Length; s++)
                {
                    foreach (int itemId in CandidatesForSlot(location, SlotDealOrder[s]))
                    {
                        int monsterId = firstMonster + (cursor % ContentRegistry.MonstersPerRegion);
                        pending[monsterId].Add(itemId);
                        cursor++;
                    }
                }
            }

            // The endgame tiers, all of them, onto the last boss.
            int finalBoss = ContentRegistry.LastCanonicalMonsterId;
            for (int slotIndex = 0; slotIndex < SlotDealOrder.Length; slotIndex++)
            {
                for (int tier = FirstEndgameRegionTier; tier <= ContentRegistry.MaxAuthoredRegionTier; tier++)
                {
                    pending[finalBoss].AddRange(CandidatesForSlot(tier, SlotDealOrder[slotIndex]));
                }
            }

            foreach (var pair in pending)
            {
                tables[pair.Key] = pair.Value.ToArray();
            }

            return tables;
        }

        private static List<int> CandidatesForSlot(int regionTier, int slot)
        {
            var found = new List<int>(4);
            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;

            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].RegionTier != regionTier) continue;

                string baseItemId = ContentRegistry.GetItemBaseId(items[i].Id);
                if (!IsDroppableEquipment(baseItemId)) continue;
                if (EquipmentSlotEngine.ResolveSlotIndex(baseItemId) != slot) continue;

                found.Add(items[i].Id);
            }

            return found;
        }
    }
}

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
    /// that location's five monsters, SLOT BY SLOT, and each slot is dealt round
    /// robin until every monster holds one - wrapping back to the start of the
    /// slot's item list when that list is shorter than five.
    ///
    /// EVERY SLOT HAS THE SAME SHARE OF EVERY MONSTER'S TABLE, and that is the
    /// point of the wrap. The catalogue authors fifteen pieces per location: two
    /// each of helmet, chest, gloves, leggings and boots, three weapons, ONE
    /// amulet and ONE ring. Dealing each item to exactly one monster - which is
    /// what this did before - therefore meant four of the five monsters in every
    /// location dropped no amulet and no ring at all, and a player who settled
    /// on a favourite monster could farm it forever and never see either. The
    /// thin slots were not a content shortage; they were a distribution that
    /// gave one monster the whole supply.
    ///
    /// Three properties, all true by construction and all asserted in
    /// `EquipmentDropTableTests`:
    ///
    /// - **Every item drops from something.** Every candidate is dealt at least
    ///   once, so nothing is orphaned.
    /// - **Every monster drops every slot**, exactly one per slot per pass, so
    ///   an equipment roll is an even eight-way choice between slots rather than
    ///   a lottery weighted by what the location happened to author.
    /// - **Monsters still differ.** WHICH helmet, chest or weapon a monster
    ///   carries rotates with the deal, so consecutive monsters hold different
    ///   pieces of the same slot. Only the slots that author a single piece are
    ///   shared by all five, and that is the alternative to four of them
    ///   dropping nothing.
    ///
    /// The tables are derived, not authored, so adding an item to items.json
    /// puts it in a monster's table on the next boot instead of quietly becoming
    /// unobtainable - which is precisely how thirty-three of them got that way.
    /// Authoring a second amulet needs no code change here: the deal simply
    /// starts alternating them.
    /// </summary>
    public static class EquipmentDropTable
    {
        // Every gear slot, and every one of them is dealt to every monster - so
        // this is a listing rather than a priority. The order still decides
        // which PIECE of a slot a given monster gets first, which is why it is
        // written out by hand instead of looped from 0 to LastGearSlot: a slot
        // silently missing from this array would vanish from every drop table
        // in the game, and a list you can read is how that stays visible.
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

                for (int s = 0; s < SlotDealOrder.Length; s++)
                {
                    var candidates = CandidatesForSlot(location, SlotDealOrder[s]);
                    if (candidates.Count == 0) continue;

                    // Deal until BOTH are satisfied: every candidate placed at
                    // least once, and every monster holding at least one of this
                    // slot. With two helmets and five monsters that is five
                    // deals wrapping the item list; with six it is six deals
                    // wrapping the monster list. Taking the max is what makes
                    // one expression cover both directions.
                    int deals = Math.Max(candidates.Count, ContentRegistry.MonstersPerRegion);
                    for (int d = 0; d < deals; d++)
                    {
                        int monsterId = firstMonster + (d % ContentRegistry.MonstersPerRegion);

                        // ROTATED BY THE SLOT, not dealt straight down. Without
                        // the `+ s` every slot would hand monster 1 its first
                        // piece, monster 2 its second and so on, so the first
                        // monster of a location would carry the first piece of
                        // all eight slots and the third would carry a copy of
                        // it. The rotation makes each monster a different
                        // COMBINATION out of the same fifteen pieces.
                        int itemId = candidates[(d + s) % candidates.Count];

                        // A slot with more pieces than monsters wraps the
                        // monster list, and the same piece must not land on one
                        // monster twice - a duplicate entry would double that
                        // item's odds against everything else in the table,
                        // which is the weighting this whole rework removes.
                        if (!pending[monsterId].Contains(itemId))
                        {
                            pending[monsterId].Add(itemId);
                        }
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

            OrderArmourBySet(regionTier, found);
            return found;
        }

        /// <summary>
        /// Puts a slot's two armour pieces in SET ORDER - family A first,
        /// family B second, the same way for every slot.
        ///
        /// THIS IS WHAT MAKES THE MIX A MIX. The deal already alternated
        /// between a slot's two candidates by index, but the index came from
        /// whatever order items.json happened to list them in, and that order
        /// is not consistent per set: at region 1 it put linen first for the
        /// helmet and steel first for the chest. The alternation was therefore
        /// alternating between arbitrary things, and what fell out was one
        /// monster wearing four linen pieces and a steel boot while the next
        /// wore the mirror image - "almost the whole set, plus boots", which is
        /// not a mix, it is two sets with a swapped shoe.
        ///
        /// Ordered by set, the same `(deal + slot) % 2` rotation produces
        /// THREE pieces of one set and TWO of the other on every monster, and
        /// flips which is which from one monster to the next. So no monster is
        /// "the linen one", finishing a set means killing more than one thing,
        /// and every table still holds a full spread of slots.
        ///
        /// Only armour is touched. Weapons are three archetypes rather than two
        /// sets and belong to no family; amulets and rings author one piece
        /// each, so there is nothing to order.
        /// </summary>
        private static void OrderArmourBySet(int regionTier, List<int> candidates)
        {
            if (candidates.Count < 2) return;

            var families = ArmourSetRegistry.FamiliesAt(regionTier);
            if (families.Count < 2) return;

            candidates.Sort((left, right) =>
            {
                int leftRank = families.IndexOf(ArmourSetRegistry.FamilyOf(ContentRegistry.GetItemBaseId(left)));
                int rightRank = families.IndexOf(ArmourSetRegistry.FamilyOf(ContentRegistry.GetItemBaseId(right)));

                // Anything with no family - a weapon - keeps its catalogue
                // order rather than being shuffled to one end.
                if (leftRank < 0 || rightRank < 0) return left - right;
                if (leftRank != rightRank) return leftRank - rightRank;
                return left - right;
            });
        }
    }
}

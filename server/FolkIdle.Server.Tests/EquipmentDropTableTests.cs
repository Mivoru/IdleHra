using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The two properties the drop rework exists to guarantee, asserted over
    /// the real catalogue rather than over a fixture.
    ///
    /// Both were false before it. Equipment was chosen by scanning the region
    /// for a BaseId containing a category substring, which meant every monster
    /// in a location dropped the same things, ordinary monsters dropped nothing
    /// but weapons and offhands (armour was on a boss-only roll), and the
    /// canonical bows were unobtainable in the entire game because the code
    /// grepped "_ranged_weapon_slot_" against ids authored "_range_weapon_slot_".
    ///
    /// A substring typo is exactly the kind of defect that reads as correct
    /// forever, so these assert the OUTCOME - which items a player can actually
    /// obtain - and not the mechanism.
    /// </summary>
    public class EquipmentDropTableTests
    {
        // ContentRegistry reads its tables from GameData on demand, and the
        // drop tables are derived from those - see RawFishFoodTests for the same
        // note, and EquipmentDropTable.Build for what an uninitialised registry
        // would otherwise cache.
        public EquipmentDropTableTests()
        {
            ContentRegistry.Initialize();
        }

        private static IEnumerable<int> AllCanonicalMonsters()
        {
            for (int id = ContentRegistry.FirstCanonicalMonsterId; id <= ContentRegistry.LastCanonicalMonsterId; id++)
            {
                yield return id;
            }
        }

        [Fact]
        public void EveryDroppableEquipmentItemDropsFromSomeMonster()
        {
            var reachable = new HashSet<int>();
            foreach (int monsterId in AllCanonicalMonsters())
            {
                foreach (int itemId in EquipmentDropTable.GetDrops(monsterId).ToArray())
                {
                    reachable.Add(itemId);
                }
            }

            var orphaned = new List<string>();
            foreach (var item in ContentRegistry.ItemDefinitions.ToArray())
            {
                string baseItemId = ContentRegistry.GetItemBaseId(item.Id);
                if (!EquipmentDropTable.IsDroppableEquipment(baseItemId)) continue;
                if (reachable.Contains(item.Id)) continue;

                orphaned.Add($"{item.Id} {baseItemId} (RegionTier {item.RegionTier})");
            }

            Assert.True(
                orphaned.Count == 0,
                "these equipment items cannot drop from any monster:\n  " + string.Join("\n  ", orphaned));
        }

        [Fact]
        public void NoItemIsDealtToTwoMonsters()
        {
            var owner = new Dictionary<int, int>();
            var duplicates = new List<string>();

            foreach (int monsterId in AllCanonicalMonsters())
            {
                foreach (int itemId in EquipmentDropTable.GetDrops(monsterId).ToArray())
                {
                    if (owner.TryGetValue(itemId, out int first))
                    {
                        duplicates.Add($"{ContentRegistry.GetItemBaseId(itemId)}: {first} and {monsterId}");
                        continue;
                    }

                    owner[itemId] = monsterId;
                }
            }

            Assert.True(duplicates.Count == 0, "dealt twice:\n  " + string.Join("\n  ", duplicates));
        }

        [Fact]
        public void EveryCanonicalMonsterDropsSomething()
        {
            foreach (int monsterId in AllCanonicalMonsters())
            {
                Assert.True(
                    EquipmentDropTable.GetDrops(monsterId).Length > 0,
                    $"{ContentRegistry.GetMonsterName(monsterId)} ({monsterId}) drops no equipment at all");
            }
        }

        /// <summary>
        /// The complaint that started this: every monster dropped one of each
        /// weapon type and a shield, and nothing else. A monster whose whole
        /// table is weapons is that bug coming back.
        /// </summary>
        [Fact]
        public void NoMonsterDropsOnlyWeapons()
        {
            foreach (int monsterId in AllCanonicalMonsters())
            {
                var slots = EquipmentDropTable.GetDrops(monsterId)
                    .ToArray()
                    .Select(id => EquipmentSlotEngine.ResolveSlotIndex(ContentRegistry.GetItemBaseId(id)))
                    .Distinct()
                    .ToList();

                Assert.True(
                    slots.Count >= 2,
                    $"{ContentRegistry.GetMonsterName(monsterId)} ({monsterId}) drops only slot {slots[0]}");
            }
        }

        /// <summary>
        /// Armour is the half of the catalogue that ordinary monsters never
        /// dropped, so it gets its own assertion rather than riding on the
        /// coverage test - "some monster somewhere has a helmet" is not the
        /// property that failed. Every LOCATION must offer all five armour
        /// slots, or a player who has only unlocked region 1 cannot dress.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EveryLocationOffersEveryArmourSlot(int location)
        {
            var offered = new HashSet<int>();
            int first = ContentRegistry.FirstCanonicalMonsterId + (location - 1) * ContentRegistry.MonstersPerRegion;

            for (int i = 0; i < ContentRegistry.MonstersPerRegion; i++)
            {
                foreach (int itemId in EquipmentDropTable.GetDrops(first + i).ToArray())
                {
                    offered.Add(EquipmentSlotEngine.ResolveSlotIndex(ContentRegistry.GetItemBaseId(itemId)));
                }
            }

            int[] required =
            {
                EquipmentSlotEngine.SlotHelmet,
                EquipmentSlotEngine.SlotChest,
                EquipmentSlotEngine.SlotLeggings,
                EquipmentSlotEngine.SlotBoots,
                EquipmentSlotEngine.SlotWeapon,
            };

            foreach (int slot in required)
            {
                Assert.True(offered.Contains(slot), $"location {location} drops nothing for slot {slot}");
            }
        }

        /// <summary>
        /// The bows. Named explicitly because the typo that hid them was
        /// invisible to every other test in this file - a "_range_" item is
        /// perfectly ordinary equipment and would have passed the coverage
        /// check the moment coverage existed, which it did not.
        /// </summary>
        [Fact]
        public void CanonicalRangedWeaponsAreObtainable()
        {
            var reachable = new HashSet<int>();
            foreach (int monsterId in AllCanonicalMonsters())
            {
                foreach (int itemId in EquipmentDropTable.GetDrops(monsterId).ToArray())
                {
                    reachable.Add(itemId);
                }
            }

            var bows = ContentRegistry.ItemDefinitions.ToArray()
                .Where(i => ContentRegistry.GetItemBaseId(i.Id).Contains("_range_weapon_slot_", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(bows);
            foreach (var bow in bows)
            {
                Assert.True(
                    reachable.Contains(bow.Id),
                    $"{ContentRegistry.GetItemBaseId(bow.Id)} cannot be obtained");
            }
        }

        /// <summary>
        /// Amulets and rings name two slots this game does not have, so they
        /// resolve to no slot and cannot be worn. Dropping one would hand the
        /// player an item that does nothing but take up a row in the chest.
        /// </summary>
        [Fact]
        public void UnwearableCatalogueEntriesNeverDrop()
        {
            foreach (int monsterId in AllCanonicalMonsters())
            {
                foreach (int itemId in EquipmentDropTable.GetDrops(monsterId).ToArray())
                {
                    string baseItemId = ContentRegistry.GetItemBaseId(itemId);
                    Assert.True(
                        EquipmentSlotEngine.ResolveSlotIndex(baseItemId) >= 0,
                        $"{ContentRegistry.GetMonsterName(monsterId)} drops unwearable {baseItemId}");
                }
            }
        }
    }
}

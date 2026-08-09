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

        /// <summary>
        /// A monster may share a piece with another monster - that is how a
        /// location with ONE authored amulet still offers an amulet on all five
        /// - but it may never list the same piece twice itself. The roll is
        /// uniform over the table, so a duplicate entry is silently double odds
        /// for that item against everything beside it, which is exactly the
        /// weighting this whole table exists to remove.
        /// </summary>
        [Fact]
        public void NoMonsterListsTheSameItemTwice()
        {
            var offenders = new List<string>();

            foreach (int monsterId in AllCanonicalMonsters())
            {
                var table = EquipmentDropTable.GetDrops(monsterId).ToArray();
                foreach (var group in table.GroupBy(id => id).Where(g => g.Count() > 1))
                {
                    offenders.Add(
                        $"{ContentRegistry.GetMonsterName(monsterId)} ({monsterId}) lists " +
                        $"{ContentRegistry.GetItemBaseId(group.Key)} {group.Count()} times");
                }
            }

            Assert.True(offenders.Count == 0, "duplicated:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// EVERY SLOT HAS THE SAME SHARE OF EVERY TABLE, which is the whole
        /// point of the rework and the thing a player feels.
        ///
        /// The catalogue authors ONE amulet and ONE ring per location against
        /// two of every armour slot and three weapons. Dealing each piece to
        /// exactly one monster therefore left four of the five monsters in every
        /// location dropping no amulet and no ring at all - so a player who
        /// settled on a favourite could farm it forever and never see either,
        /// and the fix for "amulets are thin" was never more amulets.
        ///
        /// Asserted as an EQUAL count rather than "at least one", because the
        /// roll is uniform over the table: a monster carrying three weapons and
        /// one amulet is a monster on which amulets are a third as likely, and
        /// that is the same complaint wearing a smaller number.
        /// </summary>
        [Fact]
        public void EveryMonsterOffersEverySlotInEqualMeasure()
        {
            var complaints = new List<string>();

            foreach (int monsterId in AllCanonicalMonsters())
            {
                // The final boss also carries the endgame tiers (6-10), which
                // are dealt to him alone because he is the one monster nobody
                // reaches without the clearance those items demand. His share of
                // his own location is still even; the extras on top are not, and
                // excluding him here is cheaper than pretending they are.
                if (monsterId == ContentRegistry.LastCanonicalMonsterId) continue;

                var counts = new Dictionary<int, int>();
                for (int slot = EquipmentSlotEngine.SlotWeapon; slot <= EquipmentSlotEngine.LastGearSlot; slot++)
                {
                    counts[slot] = 0;
                }

                foreach (int itemId in EquipmentDropTable.GetDrops(monsterId).ToArray())
                {
                    counts[EquipmentSlotEngine.ResolveSlotIndex(ContentRegistry.GetItemBaseId(itemId))]++;
                }

                var missing = counts.Where(c => c.Value == 0).Select(c => c.Key).ToList();
                if (missing.Count > 0)
                {
                    complaints.Add(
                        $"{ContentRegistry.GetMonsterName(monsterId)} ({monsterId}) drops nothing for slot(s) " +
                        string.Join(", ", missing));
                    continue;
                }

                if (counts.Values.Distinct().Count() > 1)
                {
                    complaints.Add(
                        $"{ContentRegistry.GetMonsterName(monsterId)} ({monsterId}) is uneven: " +
                        string.Join(", ", counts.Select(c => $"slot {c.Key} x{c.Value}")));
                }
            }

            Assert.True(complaints.Count == 0, string.Join("\n  ", complaints));
        }

        /// <summary>
        /// EVERY MONSTER CARRIES A MIX OF BOTH SETS - three pieces of one and
        /// two of the other, flipping from monster to monster.
        ///
        /// Reported from looking at the tables: "monsters always have almost
        /// the whole set + boots". That was real. The deal alternated between a
        /// slot's two candidates by INDEX, and the index came from whatever
        /// order items.json listed them in - which is not consistent per set,
        /// so at region 1 the helmet listed linen first and the chest listed
        /// steel first. The alternation was therefore alternating between
        /// arbitrary things, and it landed on one monster wearing four linen
        /// pieces and a steel boot while the next wore the mirror image. Two
        /// sets with a swapped shoe, not a mix.
        ///
        /// Ordering the candidates by SET makes the same rotation produce 3/2
        /// and 2/3, so no monster is "the linen one" and completing a set means
        /// killing more than one thing.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EveryMonsterWearsThreeOfOneSetAndTwoOfTheOther(int location)
        {
            int first = ContentRegistry.FirstCanonicalMonsterId + (location - 1) * ContentRegistry.MonstersPerRegion;
            var families = ArmourSetRegistry.FamiliesAt(location);

            Assert.Equal(ArmourSetRegistry.SetsPerTier, families.Count);

            var splitsSeen = new HashSet<string>();

            for (int i = 0; i < ContentRegistry.MonstersPerRegion; i++)
            {
                int monsterId = first + i;

                var perFamily = new Dictionary<string, int>();
                foreach (string family in families) perFamily[family] = 0;

                foreach (int itemId in EquipmentDropTable.GetDrops(monsterId).ToArray())
                {
                    string family = ArmourSetRegistry.FamilyOf(ContentRegistry.GetItemBaseId(itemId));
                    if (family.Length == 0) continue;
                    if (perFamily.ContainsKey(family)) perFamily[family]++;
                }

                var counts = perFamily.Values.OrderByDescending(v => v).ToList();
                Assert.Equal(new[] { 3, 2 }, counts);

                splitsSeen.Add(string.Join(",", families.Select(f => f + ":" + perFamily[f])));
            }

            // BOTH ways round appear. A location where every monster split 3/2
            // the SAME way would still pass the count check above while being
            // the original complaint wearing a smaller number.
            Assert.Equal(2, splitsSeen.Count);
        }

        /// <summary>
        /// Sharing the thin slots must not collapse into "every monster in a
        /// location drops the same table", which is the bug the deal replaced.
        /// Two monsters of one location may overlap - with fifteen pieces and
        /// eight slots they have to - but no two may be IDENTICAL.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void NoTwoMonstersInALocationDropTheSameTable(int location)
        {
            int first = ContentRegistry.FirstCanonicalMonsterId + (location - 1) * ContentRegistry.MonstersPerRegion;

            for (int a = 0; a < ContentRegistry.MonstersPerRegion; a++)
            {
                for (int b = a + 1; b < ContentRegistry.MonstersPerRegion; b++)
                {
                    var tableA = EquipmentDropTable.GetDrops(first + a).ToArray().OrderBy(id => id).ToArray();
                    var tableB = EquipmentDropTable.GetDrops(first + b).ToArray().OrderBy(id => id).ToArray();

                    Assert.False(
                        tableA.SequenceEqual(tableB),
                        $"{ContentRegistry.GetMonsterName(first + a)} and {ContentRegistry.GetMonsterName(first + b)} " +
                        "drop exactly the same items");
                }
            }
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

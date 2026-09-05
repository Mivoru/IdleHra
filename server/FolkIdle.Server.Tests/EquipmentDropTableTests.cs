using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// What each monster can actually drop as equipment.
    ///
    /// Reported 2026-09-05: "it's strange that nothing better than Rare has
    /// dropped from the Ice Bat, and I have 23,804 kills". The live database
    /// showed 24,198 Ice Bat kills and ZERO region-4 equipment above Epic -
    /// against roughly 31 expected, on a sweep that spares tier 7 and above, so
    /// any that had dropped would still be there.
    ///
    /// The rarity roll is not per monster, so a monster that produces no
    /// high-tier gear either produces no gear at all or produces somebody
    /// else's. This prints the tables so that question is answerable rather
    /// than argued.
    /// </summary>
    public class EquipmentDropTableTests
    {
        private readonly ITestOutputHelper _output;

        public EquipmentDropTableTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void PrintEveryCanonicalMonstersEquipmentTable()
        {
            var report = new StringBuilder();
            report.AppendLine("monster  name                 region  drops  items");

            for (int region = 1; region <= 5; region++)
            {
                for (int i = 0; i < ContentRegistry.MonstersPerRegion; i++)
                {
                    int id = ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion + i;
                    var drops = EquipmentDropTable.GetDrops(id).ToArray();
                    string items = string.Join(", ", drops.Take(4).Select(ContentRegistry.GetItemBaseId));
                    report.AppendLine(
                        $"{id,7}  {ContentRegistry.GetMonsterName(id),-20} {region,6}  {drops.Length,5}  {items}");
                }
            }

            _output.WriteLine(report.ToString());
        }

        [Fact]
        public void EveryCanonicalMonsterCanDropSomething()
        {
            // A monster with an empty table returns 0 from TryRollEquipment
            // before the rarity is even rolled, so it produces no gear at all -
            // however many times it is killed.
            var barren = new List<string>();
            for (int offset = 0; offset < 5 * ContentRegistry.MonstersPerRegion; offset++)
            {
                int id = ContentRegistry.FirstCanonicalMonsterId + offset;
                if (EquipmentDropTable.GetDrops(id).Length == 0)
                {
                    barren.Add($"{id} {ContentRegistry.GetMonsterName(id)}");
                }
            }

            Assert.True(barren.Count == 0,
                "these monsters drop no equipment at all, however many times they are killed: "
                + string.Join(", ", barren));
        }

        [Fact]
        public void AMonsterDropsGearFromITSOWNREGION()
        {
            // Modul: THE QUESTION THE PLAYER ACTUALLY ASKED.
            //
            // 24,198 Ice Bat kills produced no region-4 gear above Epic. If a
            // region-4 monster is handing out region-3 pieces, the region-4
            // catalogue is unreachable no matter how long anyone farms it - and
            // the rarity roll, which is region-blind, would never be the cause.
            var wrong = new List<string>();

            for (int region = 1; region <= 5; region++)
            {
                for (int i = 0; i < ContentRegistry.MonstersPerRegion; i++)
                {
                    int id = ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion + i;
                    foreach (int itemId in EquipmentDropTable.GetDrops(id))
                    {
                        var def = ContentRegistry.ItemDefinitions[itemId - 1];
                        // The final boss also deals the endgame ladder (region
                        // 6-10), which is deliberate - see EquipmentDropTable's
                        // FirstEndgameRegionTier comment.
                        bool isFinalBoss = id == ContentRegistry.FirstCanonicalMonsterId + 5 * ContentRegistry.MonstersPerRegion - 1;
                        if (def.RegionTier == region) continue;
                        if (isFinalBoss && def.RegionTier >= 6) continue;

                        wrong.Add($"{ContentRegistry.GetMonsterName(id)} (region {region}) drops {ContentRegistry.GetItemBaseId(itemId)} (region {def.RegionTier})");
                    }
                }
            }

            foreach (string line in wrong.Take(20)) _output.WriteLine(line);
            Assert.True(wrong.Count == 0, $"{wrong.Count} monsters drop gear from another region");
        }
    }
}

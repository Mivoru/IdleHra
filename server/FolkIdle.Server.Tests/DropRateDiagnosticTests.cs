using System;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    // Modul: HOW LONG UNTIL A PLAYER IS DRESSED.
    //
    // Equipment is monster loot and nothing else, so the drop chance is the
    // only tap that fills seven slots - and the fusion system needs THREE
    // IDENTICAL pieces at the same rarity, which is a much harder ask than one
    // of each. Neither had ever been put in numbers.
    //
    // Prints rather than merely asserting, because the useful output is the
    // table: kills per drop, drops per region, and how much of a wardrobe that
    // is at the pace combat actually runs at.
    public class DropRateDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public DropRateDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void Test_Drops_HowLongUntilAPlayerIsDressed()
        {
            // Region hours from the pacing model, floor case.
            double[] regionHours = { 2.3, 11.2, 57.3, 258.0, 1120.0 };

            _output.WriteLine($"equipment drop chance: {CombatLootEngine.EquipmentDropChance:P1} per kill");
            _output.WriteLine("");
            _output.WriteLine("region  table  kills/drop  drops in region  per item type");

            for (int region = 1; region <= 5; region++)
            {
                int firstId = 91 + (region - 1) * 5;

                // The four regulars share a location's equipment between them,
                // so what one monster can drop is what matters to a player
                // parked on it.
                int tableSize = EquipmentDropTable.GetDrops(firstId).Length;

                double killsPerDrop = 1.0 / CombatLootEngine.EquipmentDropChance;

                // Seconds per kill at the region's own gear, from
                // ProgressionRateTests' measured figures.
                double[] secondsPerKill = { 21.7, 16.6, 13.9, 17.4, 13.5 };
                double killsInRegion = regionHours[region - 1] * 3600.0 / secondsPerKill[region - 1];
                double dropsInRegion = killsInRegion * CombatLootEngine.EquipmentDropChance;
                double perType = tableSize > 0 ? dropsInRegion / tableSize : 0.0;

                _output.WriteLine($"  {region}   {tableSize,4}  {killsPerDrop,9:F0}  {dropsInRegion,14:F0}  {perType,12:F1}");
            }

            _output.WriteLine("");
            _output.WriteLine("A wearable set is 7 slots. Fusion needs THREE of one item at one rarity.");

            Assert.True(CombatLootEngine.EquipmentDropChance > 0.0);
        }
    }
}

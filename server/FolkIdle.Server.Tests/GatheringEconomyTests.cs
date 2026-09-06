using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// WHAT GATHERING PAYS, AGAINST WHAT THE GAME CHARGES - 2026-09-06.
    ///
    /// Reported as "farming materials is too OP - 2.3 million frostpine logs".
    /// It was, and by more than the report guessed: 2.4 million logs AND 2.3
    /// million ore on one account, while the entire crafting tree of the game
    /// costs 384,000 units and every village upgrade in it about 24,000.
    ///
    /// Three levers compounded, none of them the axe the player blamed:
    ///
    ///   codex yield multiplier   71.9x   (+0.5% per codex level, uncapped)
    ///   mastery speed            14.7x   (+10% per level, linear, uncapped)
    ///   tool tier                 4.5x   (measured, tuned, paid for in materials)
    ///
    /// The first two are now bounded (CodexEngine.MaxYieldMultiplier and
    /// GatheringToolEngine.GetMasterySpeedBonusPct); the tool curve is
    /// untouched, because it was the one lever that had been designed.
    ///
    /// This file prints the supply and the sink side by side, in the same
    /// units, and fails if they drift apart again. It is the missing half of
    /// GatheringShareTests, which measures gathering against COMBAT time and
    /// therefore could not see a surplus that no sink was ever going to absorb.
    /// </summary>
    public class GatheringEconomyTests
    {
        private readonly ITestOutputHelper _o;

        public GatheringEconomyTests(ITestOutputHelper o)
        {
            _o = o;
            ContentRegistry.Initialize();
        }

        private const int TicksPerSecond = 10;

        /// <summary>Units of one material per hour for one character.</summary>
        private static double UnitsPerHour(int baseTicks, int masteryLevel, int toolTier, int villageLevel, float codexYield)
        {
            int ticks = GatheringToolEngine.ComputeRequiredTicks(baseTicks, masteryLevel, toolTier, villageLevel);
            double secondsPerHarvest = ticks / (double)TicksPerSecond;
            double harvestsPerHour = 3600.0 / secondsPerHarvest;

            // SimulationEngine's gathering block rolls the node's table
            // `(100 * codexYield) / 100` times and every entry grants one unit,
            // so the roll count IS the units. 90% of them are the node's common
            // material, 10% its rare one.
            return harvestsPerHour * codexYield;
        }

        private sealed record Profile(string Name, int BaseTicks, int Mastery, int ToolTier, int Village, float CodexYield);

        private static readonly Profile[] Profiles =
        {
            new("new player, region 1, bare hands", 30, 0, 0, 0, 1.0f),
            new("region 1, first axe", 30, 5, 1, 1, 1.05f),
            new("region 3, keeping up", 60, 40, 5, 5, 1.5f),
            new("region 5, geared", 100, 80, 7, 8, 2.0f),
            new("region 5, everything maxed", 100, 127, 10, 10, 2.0f),
        };

        [Fact]
        public void Test_Gathering_SupplyPerHourAgainstEverySink()
        {
            _o.WriteLine("SUPPLY - one character, one node, units of material an hour");
            _o.WriteLine("profile                              ticks   s/harvest   units/h   common/h");
            foreach (var p in Profiles)
            {
                int ticks = GatheringToolEngine.ComputeRequiredTicks(p.BaseTicks, p.Mastery, p.ToolTier, p.Village);
                double units = UnitsPerHour(p.BaseTicks, p.Mastery, p.ToolTier, p.Village, p.CodexYield);
                _o.WriteLine($"{p.Name,-36} {ticks,5}   {ticks / 10.0,9:F1}   {units,7:F0}   {units * 0.9,8:F0}");
            }

            _o.WriteLine("");
            _o.WriteLine("SINKS - what the game charges, in the same units");
            long villageOneUpgradeMax = VillageManagementEngine.CalculateProductionUpgradeCost(4);
            long villageOneBuildingClimb = 0;
            for (int level = 0; level < 12; level++)
            {
                // Logs AND ore, both charged at the same figure.
                villageOneBuildingClimb += VillageManagementEngine.CalculateProductionUpgradeCost(level) * 2;
            }

            long recipeTotal = 0;
            long biggestRecipe = 0;
            foreach (var recipe in ContentRegistry.Recipes.ToArray())
            {
                long cost = recipe.Mat1Count + recipe.Mat2Count;
                recipeTotal += cost;
                if (cost > biggestRecipe) biggestRecipe = cost;
            }

            _o.WriteLine($"  the most expensive single village upgrade      {villageOneUpgradeMax,10:N0}");
            _o.WriteLine($"  one building from 0 to 12 (logs + ore)         {villageOneBuildingClimb,10:N0}");
            _o.WriteLine($"  the most expensive recipe in the game          {biggestRecipe,10:N0}");
            _o.WriteLine($"  every recipe in the crafting tree, once        {recipeTotal,10:N0}");

            var endgame = Profiles[^1];
            double endgameUnits = UnitsPerHour(endgame.BaseTicks, endgame.Mastery, endgame.ToolTier, endgame.Village, endgame.CodexYield);
            double endgameCommon = endgameUnits * 0.9;

            _o.WriteLine("");
            _o.WriteLine("HOURS OF ONE MAXED CHARACTER'S GATHERING, at the endgame rate");
            _o.WriteLine($"  the most expensive village upgrade   {villageOneUpgradeMax / endgameCommon,8:F2} h");
            _o.WriteLine($"  the most expensive recipe            {biggestRecipe / endgameCommon,8:F2} h");
            _o.WriteLine($"  the whole crafting tree              {recipeTotal / endgameCommon,8:F2} h");

            // THE BAND. Both ends are failures with a name:
            //
            //  too fast - the sinks are decoration, which is where this started:
            //  the account that reported it earned the game's ENTIRE material
            //  sink twice over every hour, and held a hundred times the village's
            //  lifetime cost in a single material.
            //
            //  too slow - gathering becomes the game, which this project has
            //  also shipped: see GatheringShareTests, where fishing reached 78%
            //  of region 5.
            double hoursForTheBiggestRecipe = biggestRecipe / endgameCommon;
            Assert.InRange(hoursForTheBiggestRecipe, 1.0, 40.0);

            // The whole tree is a multi-day project for one character and an
            // evening for a full roster with a stockpile - never an afternoon,
            // never a season.
            double hoursForTheWholeTree = recipeTotal / endgameCommon;
            Assert.InRange(hoursForTheWholeTree, 5.0, 200.0);

            // A village upgrade is minutes, not hours. It is the small,
            // frequent sink and pricing it like a tool would rebuild the wall
            // BaseUpgradeCost's comment records tearing down.
            Assert.InRange(villageOneUpgradeMax / endgameCommon * 60.0, 0.05, 20.0);
        }

        [Fact]
        public void Test_Gathering_TheToolIsTheDominantLeverAgain()
        {
            // Modul: the defect this pass exists for, as one comparison.
            //
            // Mastery is free and unbounded; a tool costs materials and was
            // deliberately tuned at 1.35x a tier. When the free lever outgrows
            // the paid one, the paid one stops being a decision - which is
            // exactly the flatness the geometric tool curve was written to end.
            _o.WriteLine("mastery   speed%    vs tier-5 axe (348%)   vs tier-10 (1912%)");
            foreach (int level in new[] { 1, 10, 25, 50, 100, 127, 200, 400 })
            {
                int mastery = GatheringToolEngine.GetMasterySpeedBonusPct(level);
                _o.WriteLine($"{level,7}   {mastery,6}%   {mastery / 348.0,20:F2}x   {mastery / 1912.0,16:F2}x");
            }

            // At the highest mastery seen on the live server, the tool still
            // wins - both the mid-game one and the top one.
            int atLiveMax = GatheringToolEngine.GetMasterySpeedBonusPct(127);
            Assert.True(atLiveMax < GatheringToolEngine.GetToolSpeedBonusPct(6),
                $"mastery 127 is {atLiveMax}%, which outgrows the tier-6 tool - the tool has stopped mattering again.");

            // Every level still pays something, or pays nothing only because the
            // curve has genuinely flattened - never less than the level below.
            int previous = 0;
            for (int level = 1; level <= 500; level++)
            {
                int now = GatheringToolEngine.GetMasterySpeedBonusPct(level);
                Assert.True(now >= previous, $"mastery {level} is slower than {level - 1}.");
                previous = now;
            }

            // And the first level is worth MORE than it used to be. A curve that
            // bounds the top by punishing the bottom is a different, worse game.
            Assert.True(GatheringToolEngine.GetMasterySpeedBonusPct(1) >= 10,
                "the first mastery level must be worth at least the flat 10% it replaced.");
        }

        [Fact]
        public void Test_Codex_YieldMultiplierIsBounded()
        {
            // The live account's codex level sum, and what it used to buy.
            const int liveLevelSum = 14_178;
            float uncapped = 1.0f + liveLevelSum * 0.005f;
            float capped = Math.Min(uncapped, CodexEngine.MaxYieldMultiplier);

            _o.WriteLine($"codex level sum {liveLevelSum}: {uncapped:F1}x uncapped -> {capped:F1}x");

            Assert.Equal(CodexEngine.MaxYieldMultiplier, capped);
            Assert.True(CodexEngine.MaxYieldMultiplier >= 1.5f,
                "a codex worth less than +50% is not worth filling.");
            Assert.True(CodexEngine.MaxYieldMultiplier <= 3.0f,
                "this multiplier is a ROLL COUNT for gathering and for every combat material drop; "
                + "past 3x it is the economy rather than a bonus in it.");
        }
    }
}

using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Rations (FoodRegistry.RationIntervalTicks): a fighting character in
    /// region 2+ eats one fish of the monster's region or later on a fixed
    /// interval, whatever its health; unpaid, it is hungry and kills pay half.
    /// </summary>
    public class RationTests
    {
        private readonly ITestOutputHelper _output;

        public RationTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static int Fish(int tier)
        {
            int id = FoodRegistry.FirstRawFishOfTier(tier);
            Assert.True(id > 0, $"no fish of tier {tier}");
            return id;
        }

        private static TickStatePayload Fighting(int monsterId, int foodItem, int food)
        {
            var payload = new TickStatePayload
            {
                PlayerId = 978_101L,
                CurrentLevel = 45,
                ActiveActivityId = monsterId,
                CurrentMonsterId = monsterId,
                Food1_ItemId = foodItem,
                Food1_Count = food,
            };
            return payload;
        }

        [Fact]
        public void PickRationSlot_PrefersTheCheapestRation_ThenThePoorerFish()
        {
            int t2 = Fish(2), t3 = Fish(3), t4 = Fish(4);

            // Region 3: tier 3 and tier 4 both cost one fish; the poorer goes.
            Assert.Equal(2, FoodRegistry.PickRationSlot(t2, 50, t3, 50, t4, 50, 3, out int cost));
            Assert.Equal(1, cost);
            // An empty slot is skipped.
            Assert.Equal(3, FoodRegistry.PickRationSlot(t2, 50, t3, 0, t4, 50, 3, out cost));
            Assert.Equal(1, cost);
            // Only poorer fish: tier 2 in region 4 is four fish a ration.
            Assert.Equal(1, FoodRegistry.PickRationSlot(t2, 50, 0, 0, 0, 0, 4, out cost));
            Assert.Equal(4, cost);
            // Not enough for one ration, or nothing at all: hungry.
            Assert.Equal(0, FoodRegistry.PickRationSlot(t2, 3, 0, 0, 0, 0, 4, out cost));
            Assert.Equal(0, FoodRegistry.PickRationSlot(0, 0, 0, 0, 0, 0, 2, out cost));
            Assert.Equal(0, cost);
        }

        [Theory]
        [InlineData(5, 5, 1)]
        [InlineData(5, 3, 1)]
        [InlineData(2, 3, 2)]
        [InlineData(1, 5, 16)]
        [InlineData(0, 2, 0)]
        public void RationCost_DoublesPerTierBelowTheRegion(int foodTier, int region, int expected)
        {
            Assert.Equal(expected, FoodRegistry.RationCost(foodTier, region));
        }

        [Fact]
        public void Intervals_RegionOneEatsNothing_AndLaterRegionsEatFaster()
        {
            Assert.Equal(0, FoodRegistry.RationIntervalTicks(1));
            int previous = int.MaxValue;
            for (int region = 2; region <= 5; region++)
            {
                int interval = FoodRegistry.RationIntervalTicks(region);
                Assert.InRange(interval, 1, previous - 1);
                previous = interval;
            }
        }

        [Fact]
        public void TickRation_EatsOnePerInterval_WithoutHealing()
        {
            const int monster = 97; // region 2
            Assert.Equal(2, ContentRegistry.GetMonsterRegionTier(monster));
            int interval = FoodRegistry.RationIntervalTicks(2);
            var payload = Fighting(monster, Fish(2), 10);
            payload.PlayerHp = 1234;

            for (int t = 0; t < interval * 3; t++) SimulationEngine.TickRation(ref payload);

            Assert.Equal(7, payload.Food1_Count);
            Assert.Equal(1234, payload.PlayerHp);
            Assert.False(payload.Hungry);
        }

        [Fact]
        public void TheGapBetweenMonsters_NeitherCountsNorForgivesHunger()
        {
            var payload = Fighting(97, 0, 0);
            payload.Hungry = true;
            payload.CurrentMonsterId = 0;
            SimulationEngine.TickRation(ref payload);
            Assert.True(payload.Hungry);
            Assert.Equal(0, payload.RationTicksSinceMeal);
        }

        [Fact]
        public void TickRation_InRegionOne_EatsNothing()
        {
            var payload = Fighting(91, Fish(1), 10);
            for (int t = 0; t < 10_000; t++) SimulationEngine.TickRation(ref payload);
            Assert.Equal(10, payload.Food1_Count);
            Assert.False(payload.Hungry);
        }

        [Fact]
        public void AnUnpaidRation_IsHunger_AndStockingEndsItOnTheNextTick()
        {
            const int monster = 97;
            int interval = FoodRegistry.RationIntervalTicks(2);
            // One region-1 fish: a ration in region 2 costs two.
            var payload = Fighting(monster, Fish(1), 1);

            for (int t = 0; t < interval; t++) SimulationEngine.TickRation(ref payload);
            Assert.True(payload.Hungry);
            Assert.Equal(ActivityHaltReason.OutOfFood, payload.ActivityHaltReason);
            Assert.Equal(1, payload.Food1_Count);

            payload.Food2_ItemId = Fish(2);
            payload.Food2_Count = 5;
            SimulationEngine.TickRation(ref payload);
            Assert.False(payload.Hungry);
            Assert.Equal(ActivityHaltReason.None, payload.ActivityHaltReason);
            Assert.Equal(4, payload.Food2_Count);
        }

        [Fact]
        public void TheProjection_EatsTheSameRationsAsTheLiveTick()
        {
            // Region 2's interval against an hour, auto-eat off: the advisor's
            // food figure is exactly the rations (an hourly rate even if the
            // unhealed character falls).
            const int monster = 97;
            Assert.Equal(2, ContentRegistry.GetMonsterRegionTier(monster));
            var payload = new TickStatePayload
            {
                PlayerId = 978_102L,
                CurrentLevel = 60,
                STR = 400,
                DEX = 300,
                CON = 400,
                LCK = 25,
                ActiveActivityId = monster,
                AutoEatThreshold = 0,
                Food1_ItemId = Fish(2),
                Food1_Count = 9_999,
                CachedCodexDamageMultiplier = 1f,
            };
            var stats = SimulationEngine.LiveCombatStats(in payload);
            payload.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);

            var estimate = HuntingProjection.Project(in payload, monster, globalXpMultiplier: 100, activeGlobalEventId: 0);
            _output.WriteLine($"food per hour {estimate.FoodPerHour:F0}, survives {estimate.SurvivesWithFood}");
            double rations = HuntingProjection.HorizonTicks / (double)FoodRegistry.RationIntervalTicks(2);
            Assert.InRange(estimate.FoodPerHour, rations - 1, rations + 1);
        }

        [Fact]
        public void OfflineHunger_PaysHalfTheXpOfAFedWindow()
        {
            const int monster = 97;
            TickStatePayload Character(int foodItem)
            {
                var p = new TickStatePayload
                {
                    PlayerId = 978_103L,
                    CurrentLevel = 45,
                    STR = 120,
                    DEX = 90,
                    CON = 90,
                    LCK = 25,
                    ActiveActivityId = monster,
                    AutoEatThreshold = 0,
                    Food1_ItemId = foodItem,
                    Food1_Count = foodItem == 0 ? 0 : 5_000,
                    CachedCodexDamageMultiplier = 1f,
                    // Hungry from the first tick, so the whole window is.
                    Hungry = foodItem == 0,
                };
                var s = SimulationEngine.LiveCombatStats(in p);
                p.PlayerHp = (int)SimulationEngine.EffectiveMaxMilliHpFor(in p, in s);
                return p;
            }

            var fed = Character(Fish(2));
            var hungry = Character(0);
            OfflineSimulationEngine.ProjectCombat(ref fed, monster, 600);
            OfflineSimulationEngine.ProjectCombat(ref hungry, monster, 600);
            while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }

            _output.WriteLine($"fed gold {fed.CurrentGold}, hungry gold {hungry.CurrentGold}; fed {fed.CurrentLevel}/{fed.CurrentXp}, hungry {hungry.CurrentLevel}/{hungry.CurrentXp}");
            Assert.True(hungry.Hungry);
            Assert.False(fed.Hungry);
            Assert.True(fed.CurrentGold > 0);
            // Half the gold, within the per-kill rounding.
            Assert.InRange(hungry.CurrentGold, fed.CurrentGold * 0.40, fed.CurrentGold * 0.60);
        }
    }
}

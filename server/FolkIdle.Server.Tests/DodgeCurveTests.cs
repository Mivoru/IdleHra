using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The monster hit curve (owner, 2026-10-29): 95% at zero dodge as before,
    /// but without the old dead zone where the first 5.26 points of dodge were
    /// swallowed by the 95% clamp - and the defensive potion's dodge in points.
    /// </summary>
    public class DodgeCurveTests
    {
        private static CombatStats WithDodge(float dodge) => new CombatStats { DodgeChancePct = dodge };

        [Fact]
        public void Zero_dodge_is_still_ninety_five_percent()
            => Assert.Equal(0.95f, SimulationEngine.MonsterHitChance(WithDodge(0f)), 4);

        [Theory]
        [InlineData(0.5f)]
        [InlineData(2f)]
        [InlineData(4f)]
        public void Dodge_below_the_old_dead_zone_now_counts(float dodge)
        {
            float hit = SimulationEngine.MonsterHitChance(WithDodge(dodge));
            Assert.True(hit < 0.95f, $"dodge {dodge} -> {hit:P2}");
            Assert.Equal(100f / (100f + dodge) - 0.05f, hit, 4);
        }

        [Fact]
        public void The_curve_falls_with_every_point_and_keeps_its_floor()
        {
            float previous = 1f;
            for (int dodge = 0; dodge <= 200; dodge += 5)
            {
                float hit = SimulationEngine.MonsterHitChance(WithDodge(dodge));
                Assert.True(hit < previous);
                previous = hit;
            }
            Assert.Equal(0.05f, SimulationEngine.MonsterHitChance(WithDodge(100_000f)), 4);
            Assert.Equal(0.95f, SimulationEngine.MonsterHitChance(WithDodge(-50f)), 4);
        }

        [Fact]
        public void A_defensive_potion_adds_one_dodge_point_per_tier()
        {
            ContentRegistry.Initialize();
            int potionId = 0;
            int tier = 0;
            var items = ContentRegistry.ItemDefinitions;
            for (int i = 0; i < items.Length; i++)
            {
                // Any item works for the stat path: StatsCalculator reads only
                // the region tier of whatever id sits in the defensive slot.
                if (items[i].RegionTier >= 2) { potionId = i + 1; tier = items[i].RegionTier; break; }
            }
            Assert.True(potionId > 0);

            var bare = StatsCalculator.Calculate(0, 0, 0, 0);
            var potion = StatsCalculator.Calculate(0, 0, 0, 0, activeDefensivePotionId: potionId);

            Assert.Equal(tier * StatsCalculator.DefensivePotionDodgePerTier, potion.DodgeChancePct - bare.DodgeChancePct, 3);
        }
    }
}

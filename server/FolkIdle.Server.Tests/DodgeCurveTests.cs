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

        // Modul: the defensive slot's flat dodge-by-tier went with the legacy
        // potions (2026-10-31). Any item in the slot used to pay it; a slot item
        // now pays only its own PotionRegistry effect, so a non-potion pays
        // nothing and Ironbark Brew pays armour, not dodge.
        [Fact]
        public void The_combat_slot_pays_only_a_potions_own_effect()
        {
            ContentRegistry.Initialize();
            var bare = StatsCalculator.Calculate(0, 0, 10, 0);
            var notAPotion = StatsCalculator.Calculate(0, 0, 10, 0, activeDefensivePotionId: 272); // a fish
            Assert.Equal(bare.DodgeChancePct, notAPotion.DodgeChancePct, 3);
            Assert.Equal(bare.MaxHp, notAPotion.MaxHp);
            Assert.Equal(bare.FlatPhysicalArmor, notAPotion.FlatPhysicalArmor);

            Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId("ironbark_brew_defensive_potion_consumable", out var ironbark));
            var brewed = StatsCalculator.Calculate(0, 0, 10, 0, activeDefensivePotionId: ironbark.Id);
            Assert.Equal(bare.DodgeChancePct, brewed.DodgeChancePct, 3);
            Assert.True(brewed.FlatPhysicalArmor > bare.FlatPhysicalArmor);
        }
    }
}

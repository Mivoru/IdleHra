using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Two stats the game sold and did not pay, found while writing
    /// docs/architecture/STATS_REFERENCE.md (§16, 2026-10-29): the inheritance
    /// "Gathering yield" stat, which nothing read, and the Fortitude bough's
    /// armour half, which the card promised and the tick never applied. Each
    /// test asserts the EFFECT - harvest rolls, a smaller monster hit - not a
    /// registry value, because a registry value is what both had all along.
    /// </summary>
    public class StatsReferenceFixTests
    {
        public StatsReferenceFixTests()
        {
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Character()
            => new TickStatePayload { CurrentLevel = 40, CON = 50, STR = 50, DEX = 50, LCK = 25 };

        private static GatheringNodeDefinition FirstNode(int profession)
            => ContentRegistry.GatheringNodes.ToArray().First(n => n.ProfessionType == profession);

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Inheritance_gathering_yield_adds_two_points_of_rolls_per_level(int profession)
        {
            var bare = Character();
            var bought = bare;
            bought.Inherit_GatheringYield = InheritanceRegistry.MaxLevel;
            var node = FirstNode(profession);

            var bareYield = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
            var boughtYield = SimulationEngine.GatheringYieldFor(ref bought, in node, 100);

            Assert.Equal(bareYield.MultiplierPct + InheritanceRegistry.MaxLevel * InheritanceRegistry.PercentPerLevel, boughtYield.MultiplierPct);
        }

        [Fact]
        public void Inheritance_gathering_yield_sits_inside_the_codex_multiplier_not_under_its_cap()
        {
            // The codex multiplies the whole additive pool. Its 2x cap bounds
            // the codex factor alone, so a capped codex still doubles the
            // inheritance points rather than swallowing them.
            var bare = Character();
            bare.CachedCodexYieldMultiplier = CodexEngine.MaxYieldMultiplier;
            var bought = bare;
            bought.Inherit_GatheringYield = 10;
            var node = FirstNode(0);

            var bareYield = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
            var boughtYield = SimulationEngine.GatheringYieldFor(ref bought, in node, 100);

            Assert.Equal(bareYield.MultiplierPct + 20 * 2, boughtYield.MultiplierPct);
        }

        [Fact]
        public void Inheritance_gathering_yield_pays_offline_too()
        {
            var node = FirstNode(0);
            var bare = Character();
            bare.ActiveActivityId = node.ActivityId;
            var bought = bare;
            bought.Inherit_GatheringYield = InheritanceRegistry.MaxLevel;

            var bareProjection = OfflineSimulationEngine.CalculateGatheringProjection(ref bare, node, 8 * 3600, new Random(11));
            var boughtProjection = OfflineSimulationEngine.CalculateGatheringProjection(ref bought, node, 8 * 3600, new Random(11));

            Assert.Equal(bareProjection.Actions, boughtProjection.Actions);
            Assert.True(bareProjection.Drops > 0);
            // +40 points on a 100-point base: 1.4x the rolls. 1.25 leaves room
            // for the binomial draw over thousands of harvests.
            Assert.True(boughtProjection.Drops > bareProjection.Drops * 1.25,
                $"offline drops {bareProjection.Drops} -> {boughtProjection.Drops}");
        }

        [Fact]
        public void Fortitude_raises_armour_by_two_percent_a_level()
        {
            var bare = Character();
            var tree = bare;
            tree.Skill_Fortitude = 8;

            var bareStats = SimulationEngine.LiveCombatStats(in bare);
            var treeStats = SimulationEngine.LiveCombatStats(in tree);

            Assert.True(bareStats.FlatPhysicalArmor > 0);
            Assert.Equal(bareStats.FlatPhysicalArmor + (long)bareStats.FlatPhysicalArmor * 160 / 1000, treeStats.FlatPhysicalArmor);
        }

        [Fact]
        public void Fortitude_armour_shrinks_the_monster_hit_and_keeps_its_health_half()
        {
            var bare = Character();
            var tree = bare;
            tree.Skill_Fortitude = 8;

            var bareStats = SimulationEngine.LiveCombatStats(in bare);
            var treeStats = SimulationEngine.LiveCombatStats(in tree);
            long bareHp = SimulationEngine.EffectiveMaxMilliHpFor(in bare, in bareStats);
            long treeHp = SimulationEngine.EffectiveMaxMilliHpFor(in tree, in treeStats);
            Assert.Equal(bareHp + bareHp * 160 / 1000, treeHp);

            // A region-1 hit big enough that armour, not the 1,000 milli-HP
            // floor, decides what lands.
            long bareHit = SimulationEngine.LandedMonsterMilliDamage(200, 1f, in bareStats, 1, bareHp);
            long treeHit = SimulationEngine.LandedMonsterMilliDamage(200, 1f, in treeStats, 1, treeHp);
            Assert.True(treeHit < bareHit, $"monster hit {bareHit} -> {treeHit}");
        }

        [Fact]
        public void The_stat_sheet_shows_the_fortitude_armour()
        {
            var tree = Character();
            tree.Skill_Fortitude = 8;

            var sheet = CharacterStatSheet.Build(tree, 0);
            var armourRow = sheet.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "armour");

            Assert.Equal(SimulationEngine.LiveCombatStats(in tree).FlatPhysicalArmor, armourRow.Value);
        }

        [Theory]
        [InlineData(0, 1000, 1000)]
        [InlineData(1, 1000, 1020)]
        [InlineData(8, 1000, 1160)]
        [InlineData(50, 1000, 1160)]
        [InlineData(8, 0, 0)]
        public void ApplyFortitudeArmour_is_two_percent_a_level_capped_at_the_bough(int level, int armour, int expected)
        {
            Assert.Equal(expected, SkillTreeRegistry.ApplyFortitudeArmour(armour, level));
        }
    }
}

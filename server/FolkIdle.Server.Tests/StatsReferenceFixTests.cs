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

            Assert.Equal(bareYield.MultiplierPct + InheritanceRegistry.GetBonusPct(10) * 2, boughtYield.MultiplierPct);
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
        public void Ironhide_raises_armour_by_its_share()
        {
            var bare = Character();
            var tree = TestTree.With(bare, (WarTreeRegistry.Ironhide, 5));

            var bareStats = SimulationEngine.LiveCombatStats(in bare);
            var treeStats = SimulationEngine.LiveCombatStats(in tree);

            int tenths = WarTreeRegistry.Get(WarTreeRegistry.Ironhide)!.ValueAt(5);
            Assert.True(bareStats.FlatPhysicalArmor > 0);
            Assert.Equal(bareStats.FlatPhysicalArmor + (long)bareStats.FlatPhysicalArmor * tenths / 1000, treeStats.FlatPhysicalArmor);
        }

        [Fact]
        public void Ironhide_shrinks_the_monster_hit_and_vitality_raises_health()
        {
            var bare = Character();
            var tree = TestTree.With(bare, (WarTreeRegistry.Ironhide, 5), (WarTreeRegistry.Vitality, 5));

            var bareStats = SimulationEngine.LiveCombatStats(in bare);
            var treeStats = SimulationEngine.LiveCombatStats(in tree);
            long bareHp = SimulationEngine.EffectiveMaxMilliHpFor(in bare, in bareStats);
            long treeHp = SimulationEngine.EffectiveMaxMilliHpFor(in tree, in treeStats);
            int tenths = WarTreeRegistry.Get(WarTreeRegistry.Vitality)!.ValueAt(5);
            Assert.Equal(bareHp + bareHp * tenths / 1000, treeHp);

            // A region-1 hit big enough that armour, not the 1,000 milli-HP
            // floor, decides what lands.
            long bareHit = SimulationEngine.LandedMonsterMilliDamage(200, 1f, in bareStats, 1, bareHp);
            long treeHit = SimulationEngine.LandedMonsterMilliDamage(200, 1f, in treeStats, 1, treeHp);
            Assert.True(treeHit < bareHit, $"monster hit {bareHit} -> {treeHit}");
        }

        [Fact]
        public void The_stat_sheet_shows_the_ironhide_armour()
        {
            var tree = TestTree.With(Character(), (WarTreeRegistry.Ironhide, 5));

            var sheet = CharacterStatSheet.Build(tree, 0);
            var armourRow = sheet.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "armour");

            Assert.Equal(SimulationEngine.LiveCombatStats(in tree).FlatPhysicalArmor, armourRow.Value);
        }

        private static TickStatePayload OfRace(byte raceId, int mastery)
        {
            var payload = Character();
            payload.Slot1_CharacterId = Guid.NewGuid();
            payload.Slot1_AgePhase = 1;
            payload.Slot1_GeneticVector = raceId;
            if (raceId == RaceIds.Vila) payload.VilaMasteryLevel = mastery;
            if (raceId == RaceIds.Draugr) payload.DraugrMasteryLevel = mastery;
            return payload;
        }

        [Theory]
        [InlineData(10, 10f)]
        [InlineData(50, 12f)]
        public void Draugr_mastery_lifesteal_is_the_percent_it_says(int mastery, float expectedPct)
        {
            var novice = OfRace(RaceIds.Draugr, 0);
            var master = OfRace(RaceIds.Draugr, mastery);

            float gained = SimulationEngine.LiveCombatStats(in master).LifestealPct - SimulationEngine.LiveCombatStats(in novice).LifestealPct;

            Assert.Equal(expectedPct, gained, 3);
        }

        [Fact]
        public void Vila_mastery_and_the_Draugr_penalty_move_the_swing_by_fifteen_points()
        {
            var vilaNovice = OfRace(RaceIds.Vila, 0);
            var vilaMaster = OfRace(RaceIds.Vila, 10);
            Assert.Equal(15f, SimulationEngine.LiveCombatStats(in vilaMaster).AttackSpeedPct - SimulationEngine.LiveCombatStats(in vilaNovice).AttackSpeedPct, 3);

            // A swing measurably shorter: 15 points of a 1500 ms base is 225 ms.
            var noviceStats = SimulationEngine.LiveCombatStats(in vilaNovice);
            var masterStats = SimulationEngine.LiveCombatStats(in vilaMaster);
            Assert.True(SimulationEngine.LiveAttackIntervalMs(in vilaNovice, in noviceStats) - SimulationEngine.LiveAttackIntervalMs(in vilaMaster, in masterStats) >= 200);

            var human = OfRace(RaceIds.Human, 0);
            var draugr = OfRace(RaceIds.Draugr, 0);
            float humanSpeed = SimulationEngine.LiveCombatStats(in human).AttackSpeedPct;
            float draugrSpeed = SimulationEngine.LiveCombatStats(in draugr).AttackSpeedPct;
            Assert.True(humanSpeed - draugrSpeed > 10f, $"human {humanSpeed} vs draugr {draugrSpeed}");
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

using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Phase 5.1 affix families: a family node makes the AFFIX part of its
    /// fields stronger by +3/6/10%, and nothing else - an item's own base power
    /// and a pet's bonus stay as they are.
    /// </summary>
    public class AffixFamilyTests
    {
        private static TickStatePayload PayloadWith(string affixPayload, int baseDefense, int petGold)
        {
            var payload = new TickStatePayload { SkillTreeVersion = WarTreeRegistry.CurrentVersion };
            EquippedAffixTotals totals = default;
            EquipmentSlotEngine.AddAffixTotals(affixPayload, ref totals);
            totals.FlatDefense += baseDefense; // an item's own armour, as ComputeGearTotalsAsync folds it in
            totals.GoldTenthsPct += petGold;   // a pet's gold, as PetRegistry folds it in
            payload.CachedAffixTotals = totals;
            return payload;
        }

        [Fact]
        public void WithoutTheNodes_NothingChanges()
        {
            var payload = PayloadWith("{\"crit_chance_pct@1\":50,\"flat_armor@2\":20,\"gold_find_pct@3\":100}", baseDefense: 100, petGold: 80);
            var t = AffixFamilies.Totals(in payload);
            Assert.Equal(50, t.CritChanceTenthsPct);
            Assert.Equal(120, t.FlatDefense);
            Assert.Equal(180, t.GoldTenthsPct);
        }

        [Theory]
        [InlineData(1, 30)]
        [InlineData(2, 60)]
        [InlineData(3, 100)]
        public void TheNodeScalesOnlyTheAffixPart(int rank, int boostTenths)
        {
            var payload = PayloadWith("{\"crit_chance_pct@1\":50,\"flat_armor@2\":200,\"gold_find_pct@3\":100}", baseDefense: 100, petGold: 80);
            payload.SkillRanks[WarTreeRegistry.PrecisionAffinity] = (byte)rank;
            payload.SkillRanks[WarTreeRegistry.GuardAffinity] = (byte)rank;
            payload.SkillRanks[WarTreeRegistry.FortuneAffinity] = (byte)rank;

            Assert.Equal(boostTenths, AffixFamilies.BoostTenths(in payload, AffixFamily.Precision));
            var t = AffixFamilies.Totals(in payload);
            Assert.Equal(50 + 50 * boostTenths / 1000, t.CritChanceTenthsPct);
            // Base armour 100 is untouched; only the 200 from flat_armor grows.
            Assert.Equal(100 + 200 + 200 * boostTenths / 1000, t.FlatDefense);
            // The pet's 80 is untouched; only the 100 from gold_find grows.
            Assert.Equal(80 + 100 + 100 * boostTenths / 1000, t.GoldTenthsPct);
            // The cache itself is never rewritten - a respec changes the bonus at once.
            Assert.Equal(50, payload.CachedAffixTotals.CritChanceTenthsPct);
        }

        [Fact]
        public void TheCraftNodeScalesTheToolAffixes()
        {
            var payload = new TickStatePayload { ToolAxeSpeedPct = 20 };
            Assert.Equal(20, SimulationEngine.ToolSpeedPctFor(in payload, 0));
            payload.SkillRanks[HearthTreeRegistry.CraftAffinity] = 3;
            Assert.Equal(22, SimulationEngine.ToolSpeedPctFor(in payload, 0));
        }

        [Theory]
        [InlineData("crit_dmg_pct@2", AffixFamily.Precision)]
        [InlineData("melee_dmg_pct", AffixFamily.Power)]
        [InlineData("thorns_pct@1", AffixFamily.Guard)]
        [InlineData("loot_luck_pct", AffixFamily.Fortune)]
        [InlineData("gather_yield_pct", AffixFamily.Craft)]
        public void EveryRollableAffixHasAFamily(string id, AffixFamily family)
        {
            Assert.Equal(family, AffixFamilies.FamilyOf(id));
        }

        [Fact]
        public void NoLiveAffixIsLeftWithoutAFamily()
        {
            foreach (var definition in AffixRegistry.Definitions)
            {
                if (definition.Retired) continue;
                Assert.NotEqual(AffixFamily.None, AffixFamilies.FamilyOf(definition.Id));
            }
        }
    }
}

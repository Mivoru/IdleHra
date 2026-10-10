using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The character stat sheet prints what the tick uses. Each number is held
    /// to the live function it claims to be, and each cap to the clamp the tick
    /// actually applies - a sheet that drifted from the fight would tell a
    /// player to stop buying a stat that still pays, or the reverse.
    /// </summary>
    public class CharacterStatSheetTests
    {
        public CharacterStatSheetTests()
        {
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Character(int dex = 50)
            => new()
            {
                PlayerId = 990_001L,
                CurrentLevel = 40,
                STR = 60,
                DEX = dex,
                CON = 50,
                LCK = 25,
                CachedCodexDamageMultiplier = 1.4f,
                CachedCodexYieldMultiplier = 1.2f,
            };

        private static StatSheetRow Row(StatSheetResponse sheet, string key)
            => sheet.Sections.SelectMany(s => s.Rows).Single(r => r.Key == key);

        [Fact]
        public void CombatNumbers_AreTheLiveTicksOwn()
        {
            var payload = Character();
            var sheet = CharacterStatSheet.Build(payload, 0);

            var stats = SimulationEngine.LiveCombatStats(in payload);
            var copy = payload;
            long attack = SimulationEngine.EffectiveMilliAttackFor(ref copy, in stats, SimulationEngine.LineageOf(in copy).DamageScalePerLevelPct);

            Assert.Equal(System.Math.Round(attack / 1000.0, 1), Row(sheet, "attack").Value);
            Assert.Equal(SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats) / 1000, (long)Row(sheet, "max_hp").Value);
            Assert.Equal(SimulationEngine.LiveAttackIntervalMs(in payload, in stats) / 1000.0, Row(sheet, "swing").Value, 2);
            Assert.Equal(1.4, Row(sheet, "codex_damage").Value, 2);
        }

        [Fact]
        public void AttackSpeed_ReportsItsCap_OnlyWhenTheClampBinds()
        {
            var low = CharacterStatSheet.Build(Character(dex: 10), 0);
            Assert.False(Row(low, "attack_speed").AtCap);
            Assert.Equal(CombatDamageModel.MaxAttackSpeedReduction * 100, Row(low, "attack_speed").Cap!.Value, 3);

            var fast = Character();
            fast.CachedAffixTotals.AttackSpeedTenthsPct = 900; // +90%, past the 60% clamp
            var high = CharacterStatSheet.Build(fast, 0);
            Assert.True(Row(high, "attack_speed").AtCap);
            Assert.Equal(0.6, Row(high, "swing").Value, 2); // 1.5 s x (1 - 0.6)
        }

        [Fact]
        public void GatheringSpeed_IsTheSumTheTickDividesBy()
        {
            var payload = Character();
            payload.WoodcuttingMasteryLevel = 25;
            payload.AxeToolTier = 3;
            payload.LumberjackLevel = 2;

            var node = ContentRegistry.GatheringNodes.ToArray().First(n => n.ProfessionType == 0);
            payload.ActiveActivityId = node.ActivityId;

            var sheet = CharacterStatSheet.Build(payload, 0);
            var row = Row(sheet, "gather_speed_0");
            int total = (int)row.Value;

            Assert.Equal(146 + GatheringToolEngine.GetMasterySpeedBonusPct(25) + 10, total);
            int expectedTicks = System.Math.Max(GatheringToolEngine.MinRequiredTicks, node.BaseTickThreshold * 100 / (100 + total));
            Assert.Equal(expectedTicks, SimulationEngine.RequiredGatherTicks(ref payload, in node));
            Assert.Contains("a harvest", row.Note);
        }

        [Fact]
        public void ASecondSlot_IsReadInItsOwnGear()
        {
            var payload = Character();
            payload.Slot2Activity.CachedAffixTotals.FlatAttack = 500;

            var first = CharacterStatSheet.Build(payload, 0);
            var copy = payload;
            SimulationEngine.SwapSlotIntoActiveRegister(ref copy, 1);
            var second = CharacterStatSheet.Build(copy, 1);

            Assert.True(Row(second, "attack").Value > Row(first, "attack").Value);
        }

        [Fact]
        public void GoldBonus_IsTheFactorsAKillIsPaidAt()
        {
            var payload = Character();
            payload.CachedAffixTotals.GoldTenthsPct = 50; // a +5% pet
            var stats = SimulationEngine.LiveCombatStats(in payload);

            var factors = CombatGoldReward.FactorsFor(in payload, stats.GoldAcquisitionMultiplierPct);
            var sheet = CharacterStatSheet.Build(payload, 0);

            Assert.Equal(System.Math.Round(factors.TotalBonusPct, 1), Row(sheet, "gold").Value);
            Assert.True(Row(sheet, "gold").Value >= 5.0);
        }
    }
}

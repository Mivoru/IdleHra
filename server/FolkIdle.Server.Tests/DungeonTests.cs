using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The three dungeons (owner D1-D6, 2026-10-31): the stage bands are
    /// pinned to the boss ladders they borrow, each boss ability does what
    /// its line says at its first and last stage, the meal cap holds, and the
    /// ticket rules top up without ever passing the cap.
    /// </summary>
    public class DungeonTests
    {
        public DungeonTests() => ContentRegistry.Initialize();

        private static TickStatePayload Character(int level, int attribute) => new()
        {
            PlayerId = 1,
            CurrentLevel = level,
            STR = attribute, DEX = attribute, CON = attribute, LCK = attribute / 2,
            AutoEatThreshold = 50,
        };

        [Fact]
        public void StageTenIsMalakorsTopStepAtTheMeasuredScale()
        {
            int malakor = RaceUnlockRegistry.GetRegionBossMonsterId(5);
            byte cleared = BossFirstClearRules.MarkDefeated(0, malakor);
            var top = BossAscensionRegistry.ModifiersFor(5, BossAscensionRegistry.MaxStep);
            long expected = (long)(BossAscensionRules.ScaleBossAttack(BossFirstClearRules.AttackPowerFor(cleared, malakor), in top) * DungeonSimulator.AscensionBandScale);
            var (_, attack, enrage) = DungeonSimulator.BossStrength(10);
            Assert.InRange(attack, expected - 2, expected + 2);
            Assert.True(enrage);
            Assert.False(DungeonSimulator.BossStrength(6).Enrage); // A2: no Enrage yet
        }

        [Fact]
        public void EveryStageIsHarderThanTheOneBefore()
        {
            for (int stage = 2; stage <= DungeonSimulator.StageCount; stage++)
            {
                var below = DungeonSimulator.BossStrength(stage - 1);
                var here = DungeonSimulator.BossStrength(stage);
                Assert.True(here.AttackPower > below.AttackPower, $"stage {stage} attack");
                Assert.True(here.MilliHp >= below.MilliHp, $"stage {stage} health");
            }
            // Stage 15 is about 5.8x stage 10's attack (1.5 x 1.4^4).
            double ratio = DungeonSimulator.BossStrength(15).AttackPower / (double)DungeonSimulator.BossStrength(10).AttackPower;
            Assert.InRange(ratio, 5.6, 5.9);
        }

        [Fact]
        public void BabaYagasAbilitiesMatchTheirLinesAtFirstAndLastStage()
        {
            Assert.Equal(10, BabaYaga.MortarDodgePct(1), 3);
            Assert.Equal(25, BabaYaga.MortarDodgePct(15), 3);
            Assert.Equal(0, BabaYaga.HexHits(3));
            Assert.Equal(15, BabaYaga.HexEverySeconds(4), 3);
            Assert.Equal(3, BabaYaga.HexHits(4));
            Assert.Equal(10, BabaYaga.HexEverySeconds(15), 3);
            Assert.Equal(5, BabaYaga.HexHits(15));
            Assert.Equal(0, BabaYaga.FencePct(7), 3);
            Assert.Equal(15, BabaYaga.FencePct(8), 3);
            Assert.Equal(30, BabaYaga.FencePct(15), 3);
            Assert.Equal(0, BabaYaga.SweepEverySeconds(10), 3);
            Assert.Equal(30, BabaYaga.SweepEverySeconds(11), 3);
            Assert.Equal(6, BabaYaga.SweepSeconds(11), 3);
            Assert.Equal(20, BabaYaga.SweepEverySeconds(15), 3);
            Assert.Equal(10, BabaYaga.SweepSeconds(15), 3);

            Assert.Equal(1, BabaYaga.Instance.AbilitiesAt(1).Count(a => a.Active));
            Assert.Equal(4, BabaYaga.Instance.AbilitiesAt(11).Count(a => a.Active));
        }

        [Fact]
        public void AFreshCharacterLosesTheTopStage_AndARunIsDeterministic()
        {
            var weak = Character(1, 5);
            int fish = FoodRegistry.FirstRawFishOfTier(1);
            var run = DungeonSimulator.Run(weak, 15, fish, 100);
            Assert.False(run.Won);
            Assert.Equal("wave", run.Events[0].Kind);
            Assert.InRange(run.MealsEaten, 0, DungeonSimulator.MealCap);

            var again = DungeonSimulator.Run(weak, 15, fish, 100);
            Assert.Equal(run.Seconds, again.Seconds);
            Assert.Equal(run.WavesCleared, again.WavesCleared);
        }

        [Fact]
        public void MealsNeverPassTheCapOrTheLarder()
        {
            int fish = FoodRegistry.FirstRawFishOfTier(1);
            for (int stage = 1; stage <= DungeonSimulator.StageCount; stage += 7)
            {
                var run = DungeonSimulator.Run(Character(40, 120), stage, fish, 3);
                Assert.InRange(run.MealsEaten, 0, 3);
                var full = DungeonSimulator.Run(Character(40, 120), stage, fish, 9999);
                Assert.InRange(full.MealsEaten, 0, DungeonSimulator.MealCap);
            }
        }

        [Fact]
        public void TicketsTopUpTwoADayAndNeverPastFour()
        {
            var day = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            var row = new DungeonProgress { Tickets = 0, TicketDay = 0 };
            Assert.True(DungeonEngine.TopUpTickets(row, day));
            Assert.Equal(DungeonEngine.TicketCap, row.Tickets);

            row.Tickets = 1;
            Assert.False(DungeonEngine.TopUpTickets(row, day.AddHours(5)));
            Assert.Equal(1, row.Tickets);

            Assert.True(DungeonEngine.TopUpTickets(row, day.AddDays(1)));
            Assert.Equal(3, row.Tickets);
            Assert.True(DungeonEngine.TopUpTickets(row, day.AddDays(9)));
            Assert.Equal(DungeonEngine.TicketCap, row.Tickets);
        }

        [Fact]
        public void StagesOpenWithTheirBossesAndInOrder()
        {
            byte none = 0;
            byte regionTwo = BossFirstClearRules.MarkDefeated(BossFirstClearRules.MarkDefeated(0, RaceUnlockRegistry.GetRegionBossMonsterId(1)), RaceUnlockRegistry.GetRegionBossMonsterId(2));
            Assert.False(DungeonEngine.IsOpen(none));
            Assert.NotNull(DungeonEngine.StageLockedReason(1, none, 0));
            Assert.Null(DungeonEngine.StageLockedReason(1, regionTwo, 0));
            Assert.NotNull(DungeonEngine.StageLockedReason(2, regionTwo, 0)); // stage 1 not cleared
            Assert.Null(DungeonEngine.StageLockedReason(2, regionTwo, 1));
            Assert.NotNull(DungeonEngine.StageLockedReason(3, regionTwo, 2)); // region 3 boss not beaten
        }

        [Fact]
        public void TheRewardsClimbWithTheBands()
        {
            Assert.Equal(RarityTier.Rare, DungeonEngine.RarityFloorFor(5));
            Assert.Equal(RarityTier.Epic, DungeonEngine.RarityFloorFor(6));
            Assert.Equal(RarityTier.Legendary, DungeonEngine.RarityFloorFor(11));
            Assert.Equal(LootChestTier.Bronze, DungeonEngine.ChestFor(1));
            Assert.Equal(LootChestTier.Silver, DungeonEngine.ChestFor(8));
            Assert.Equal(LootChestTier.Gold, DungeonEngine.ChestFor(13));
            Assert.Equal(LootChestTier.Ancient, DungeonEngine.ChestFor(15));
            Assert.Equal(5 * 5 + 10 * 5 + 20 * 5, Enumerable.Range(1, 15).Sum(DungeonEngine.FirstClearDiamonds));
        }

        [Fact]
        public void KoscheisAbilitiesGrowFromTheirFirstStageToFifteen()
        {
            Assert.Equal(15, Koschei.RisePct(1), 3);
            Assert.Equal(40, Koschei.RisePct(10), 3);
            Assert.Equal(40, Koschei.RisePct(15), 3);
            Assert.Equal(1, Koschei.Rises(12));
            Assert.Equal(2, Koschei.Rises(13));
            Assert.Equal(0, Koschei.WardPct(3), 3);
            Assert.Equal(5, Koschei.WardPct(4), 3);
            Assert.Equal(12, Koschei.WardPct(15), 3);
            Assert.Equal(20, Koschei.WardEverySeconds(4), 3);
            Assert.Equal(14, Koschei.WardEverySeconds(15), 3);
            Assert.Equal(0, Koschei.DrainPct(7), 3);
            Assert.Equal(10, Koschei.DrainPct(8), 3);
            Assert.Equal(30, Koschei.DrainPct(15), 3);
            Assert.Equal(25, Koschei.NeedleCritBonusPct(11), 3);
            Assert.Equal(60, Koschei.NeedleCritBonusPct(15), 3);
            Assert.Equal(20, Koschei.NeedleNormalCutPct(11), 3);
            Assert.Equal(35, Koschei.NeedleNormalCutPct(15), 3);
            Assert.Equal(4, Koschei.Instance.AbilitiesAt(11).Count(a => a.Active));
        }

        [Fact]
        public void ZmeysAbilitiesGrowFromTheirFirstStageToFifteen()
        {
            Assert.Equal(15, Zmey.HeadsFasterPct(1), 3);
            Assert.Equal(30, Zmey.HeadsFasterPct(15), 3);
            Assert.Equal(0, Zmey.BreathEverySwings(3));
            Assert.Equal(6, Zmey.BreathEverySwings(4));
            Assert.Equal(5, Zmey.BreathEverySwings(15));
            Assert.Equal(3, Zmey.BreathPctPerSecond(4), 3);
            Assert.Equal(6, Zmey.BreathPctPerSecond(15), 3);
            Assert.Equal(0, Zmey.RegrowSeconds(7), 3);
            Assert.Equal(20, Zmey.RegrowSeconds(8), 3);
            Assert.Equal(10, Zmey.RegrowSeconds(15), 3);
            Assert.Equal(0, Zmey.HidePct(10), 3);
            Assert.Equal(20, Zmey.HidePct(11), 3);
            Assert.Equal(40, Zmey.HidePct(15), 3);
        }

        [Fact]
        public void KoscheiRisesWhenFirstKilled()
        {
            // A strong character beats stage 1, and Deathless fires on the way.
            var run = DungeonSimulator.Run(Character(60, 2000), DungeonKind.Xp, 1, FoodRegistry.FirstRawFishOfTier(1), 100);
            Assert.True(run.Won);
            Assert.Equal(DungeonKind.Xp, run.Kind);
            Assert.Contains(run.Events, e => e.Text.StartsWith("Deathless"));
        }

        [Fact]
        public void ZmeyLosesHisHeadsOneByOne()
        {
            // A character who needs several blows per head sees both heads fall
            // before the last one (a blow bigger than a head can take two).
            var run = DungeonSimulator.Run(Character(30, 300), DungeonKind.Gold, 1, FoodRegistry.FirstRawFishOfTier(1), 100);
            Assert.True(run.Won, string.Join(" | ", run.Events.Select(e => e.Text)));
            Assert.Equal(2, run.Events.Count(e => e.Text.StartsWith("A head falls")));
        }

        [Fact]
        public void EveryEventCarriesBothBars()
        {
            var run = DungeonSimulator.Run(Character(40, 120), DungeonKind.Gold, 4, FoodRegistry.FirstRawFishOfTier(1), 100);
            Assert.All(run.Events, e =>
            {
                Assert.InRange(e.PlayerHpPct, 0, 100);
                Assert.InRange(e.EnemyHpPct, 0, 100);
            });
            Assert.Equal(100, run.Events[0].PlayerHpPct);
            Assert.Equal(100, run.Events[0].EnemyHpPct);
        }

        [Fact]
        public void IncomeRewardsScaleWithTheStageAndALossPaysAQuarter()
        {
            Assert.Equal(28, DungeonEngine.IncomeMinutes(1));
            Assert.Equal(140, DungeonEngine.IncomeMinutes(15));
            Assert.Equal(60_000L * 28 / 60, DungeonEngine.XpFor(1, 60_000, won: true));
            Assert.Equal(60_000L * 28 / 60 / 4, DungeonEngine.XpFor(1, 60_000, won: false));
            Assert.Equal(GoldRewardScale.ForMinutes(6_000_000, 140), DungeonEngine.GoldFor(15, 6_000_000, won: true));
            Assert.Equal(GoldRewardScale.ForMinutes(6_000_000, 140) / 4, DungeonEngine.GoldFor(15, 6_000_000, won: false));
        }
    }
}

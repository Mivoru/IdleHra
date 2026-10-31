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
    /// The equipment dungeon (owner D1-D6, 2026-10-31): the stage bands are
    /// pinned to the boss ladders they borrow, each Baba Yaga ability does what
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
        public void TheAscensionBandIsMalakorsLadder()
        {
            int malakor = RaceUnlockRegistry.GetRegionBossMonsterId(5);
            byte cleared = BossFirstClearRules.MarkDefeated(0, malakor);
            for (int stage = 6; stage <= 10; stage++)
            {
                var modifiers = BossAscensionRegistry.ModifiersFor(5, (stage - 5) * 2);
                var (hp, attack, _) = DungeonSimulator.BossStrength(stage);
                Assert.Equal(BossAscensionRules.ScaleBossAttack(BossFirstClearRules.AttackPowerFor(cleared, malakor), in modifiers), attack);
                Assert.Equal(BossAscensionRules.ScaleBossHp(BossFirstClearRules.MaxHpFor(cleared, malakor) * 1000L, in modifiers), hp);
            }
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
            Assert.Equal(10, DungeonSimulator.MortarDodgePct(1), 3);
            Assert.Equal(25, DungeonSimulator.MortarDodgePct(15), 3);
            Assert.Equal(0, DungeonSimulator.HexHits(3));
            Assert.Equal(15, DungeonSimulator.HexEverySeconds(4), 3);
            Assert.Equal(3, DungeonSimulator.HexHits(4));
            Assert.Equal(10, DungeonSimulator.HexEverySeconds(15), 3);
            Assert.Equal(5, DungeonSimulator.HexHits(15));
            Assert.Equal(0, DungeonSimulator.FencePct(7), 3);
            Assert.Equal(15, DungeonSimulator.FencePct(8), 3);
            Assert.Equal(30, DungeonSimulator.FencePct(15), 3);
            Assert.Equal(0, DungeonSimulator.SweepEverySeconds(10), 3);
            Assert.Equal(30, DungeonSimulator.SweepEverySeconds(11), 3);
            Assert.Equal(6, DungeonSimulator.SweepSeconds(11), 3);
            Assert.Equal(20, DungeonSimulator.SweepEverySeconds(15), 3);
            Assert.Equal(10, DungeonSimulator.SweepSeconds(15), 3);

            Assert.Equal(1, DungeonSimulator.AbilitiesAt(1).Count(a => a.Active));
            Assert.Equal(4, DungeonSimulator.AbilitiesAt(11).Count(a => a.Active));
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
            var player = new PlayerRecord { DungeonTickets = 0, DungeonTicketDay = 0 };
            Assert.True(DungeonEngine.TopUpTickets(player, day));
            Assert.Equal(DungeonEngine.TicketCap, player.DungeonTickets);

            player.DungeonTickets = 1;
            Assert.False(DungeonEngine.TopUpTickets(player, day.AddHours(5)));
            Assert.Equal(1, player.DungeonTickets);

            Assert.True(DungeonEngine.TopUpTickets(player, day.AddDays(1)));
            Assert.Equal(3, player.DungeonTickets);
            Assert.True(DungeonEngine.TopUpTickets(player, day.AddDays(9)));
            Assert.Equal(DungeonEngine.TicketCap, player.DungeonTickets);
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
    }
}

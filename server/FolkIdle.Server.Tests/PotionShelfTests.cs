using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE POTION SHELF AND OWNER H2 (herbs and potions, 2026-10-31).
    /// A potion is drunk from its shelf on the tick, auto-drink refills a slot
    /// the tick it runs out, and an absence pays exactly the potions that were
    /// running in each part of it - a potion that ran out halfway counts for
    /// half, not for all of it or none.
    /// </summary>
    [Collection("Postgres collection")]
    public class PotionShelfTests
    {
        private readonly PostgresTestFixture _fixture;

        public PotionShelfTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
            PotionRegistry.ResetForTests();
        }

        private static PotionDefinition Potion(PotionKind kind, bool strong = false)
            => PotionRegistry.All.Single(p => p.Kind == kind && p.Strong == strong);

        [Fact]
        public void EveryKindHasANormalAndAStrongPotionInItsSlot()
        {
            foreach (PotionKind kind in Enum.GetValues<PotionKind>().Where(k => k != PotionKind.None))
            {
                var normal = Potion(kind);
                var strong = Potion(kind, strong: true);
                Assert.Equal(normal.Slot, strong.Slot);
                Assert.True(strong.Magnitude > normal.Magnitude, $"{kind}: the strong potion is not stronger");
            }
        }

        [Fact]
        public void DrinkingTakesOneFromTheShelfAndStartsTheFullDuration()
        {
            var hunt = Potion(PotionKind.Hunt);
            var payload = new TickStatePayload { PotionShelf1_ItemId = hunt.ItemId, PotionShelf1_Count = 2 };

            Assert.True(ConsumableEngine.TryDrinkFromShelf(ref payload, PotionSlot.Rewards));
            Assert.Equal(1, payload.PotionShelf1_Count);
            Assert.Equal(hunt.ItemId, payload.ActiveOffensivePotionId);
            Assert.Equal(hunt.DurationMs, payload.OffensivePotionDurationMs);

            Assert.True(ConsumableEngine.TryDrinkFromShelf(ref payload, PotionSlot.Rewards));
            Assert.Equal(0, payload.PotionShelf1_Count);
            Assert.Equal(0, payload.PotionShelf1_ItemId);
            Assert.False(ConsumableEngine.TryDrinkFromShelf(ref payload, PotionSlot.Rewards));
        }

        [Fact]
        public void APotionOnTheWrongShelfIsNeverDrunk()
        {
            var ironbark = Potion(PotionKind.Ironbark);
            var payload = new TickStatePayload { PotionShelf1_ItemId = ironbark.ItemId, PotionShelf1_Count = 3 };
            Assert.False(ConsumableEngine.TryDrinkFromShelf(ref payload, PotionSlot.Rewards));
            Assert.Equal(0, payload.ActiveOffensivePotionId);
            Assert.Equal(3, payload.PotionShelf1_Count);
        }

        [Fact]
        public void AutoDrinkRefillsTheSlotOnTheTickItRunsOut()
        {
            var gatherer = Potion(PotionKind.Gatherer);
            var payload = new TickStatePayload
            {
                ActiveGatheringPotionId = gatherer.ItemId,
                GatheringPotionDurationMs = 100,
                PotionShelf3_ItemId = gatherer.ItemId,
                PotionShelf3_Count = 1,
                PotionAutoDrinkMask = 1 << PotionSlot.Gathering,
            };

            ConsumableEngine.TickBuffCountdowns(ref payload);
            Assert.Equal(gatherer.ItemId, payload.ActiveGatheringPotionId);
            Assert.Equal(gatherer.DurationMs, payload.GatheringPotionDurationMs);
            Assert.Equal(0, payload.PotionShelf3_Count);

            // Without auto-drink the slot simply empties.
            payload.GatheringPotionDurationMs = 100;
            payload.PotionShelf3_ItemId = gatherer.ItemId;
            payload.PotionShelf3_Count = 1;
            payload.PotionAutoDrinkMask = 0;
            ConsumableEngine.TickBuffCountdowns(ref payload);
            Assert.Equal(0, payload.ActiveGatheringPotionId);
            Assert.Equal(1, payload.PotionShelf3_Count);
        }

        [Fact]
        public void APotionPaysOnlyItsOwnEffect()
        {
            var plain = StatsCalculator.Calculate(50, 50, 50, 25);
            var goldmoss = StatsCalculator.Calculate(50, 50, 50, 25, Potion(PotionKind.Goldmoss).ItemId, 0);
            var ironbark = StatsCalculator.Calculate(50, 50, 50, 25, 0, Potion(PotionKind.Ironbark).ItemId);

            Assert.Equal(plain.GoldAcquisitionMultiplierPct + Potion(PotionKind.Goldmoss).Magnitude, goldmoss.GoldAcquisitionMultiplierPct);
            // The legacy block gave every potion flat damage, HP and dodge too.
            Assert.Equal(plain.FlatMeleeDamage, goldmoss.FlatMeleeDamage);
            Assert.Equal(plain.MaxHp, ironbark.MaxHp);
            Assert.Equal(plain.DodgeChancePct, ironbark.DodgeChancePct);
            Assert.True(ironbark.FlatPhysicalArmor >= plain.FlatPhysicalArmor);
        }

        [Fact]
        public void TheAbsenceIsSplitWhereAPotionRanOut()
        {
            var hunt = Potion(PotionKind.Hunt);
            const long logout = 1_000_000L;
            var payload = new TickStatePayload
            {
                OffensivePotionLoadedId = hunt.ItemId,
                OffensivePotionExpiresEpoch = logout + 1200,
            };

            var stretches = OfflinePotionPlan.Plan(in payload, logout, 3600);
            Assert.Equal(2, stretches.Count);
            Assert.Equal(1200, stretches[0].Seconds);
            Assert.Equal(hunt.ItemId, stretches[0].RewardsId);
            Assert.Equal(2400, stretches[1].Seconds);
            Assert.Equal(0, stretches[1].RewardsId);
        }

        [Fact]
        public void AutoDrinkCoversTheAbsenceForAsLongAsTheShelfLasts()
        {
            var fortune = Potion(PotionKind.Fortune);
            long each = fortune.DurationMs / 1000L;
            const long logout = 1_000_000L;
            var payload = new TickStatePayload
            {
                OffensivePotionLoadedId = fortune.ItemId,
                OffensivePotionExpiresEpoch = logout + 600,
                PotionShelf1_ItemId = fortune.ItemId,
                PotionShelf1_Count = 2,
                PotionAutoDrinkMask = 1 << PotionSlot.Rewards,
            };
            long window = 600 + 2 * each + 1000;

            var stretches = OfflinePotionPlan.Plan(in payload, logout, window);
            Assert.Equal(window, stretches.Sum(s => s.Seconds));
            Assert.Equal(600 + 2 * each, stretches.Where(s => s.RewardsId == fortune.ItemId).Sum(s => s.Seconds));

            // Settling the same absence empties the shelf and leaves the slot empty.
            OfflinePotionPlan.Settle(ref payload, logout, window, fortune.ItemId, 0, 0, 0, 0, 0);
            Assert.Equal(0, payload.PotionShelf1_Count);
            Assert.Equal(0, payload.ActiveOffensivePotionId);

            // A shorter absence drinks only what it needed, and the last one is still running.
            payload.PotionShelf1_ItemId = fortune.ItemId;
            payload.PotionShelf1_Count = 2;
            OfflinePotionPlan.Settle(ref payload, logout, 600 + each / 2, fortune.ItemId, 0, 0, 0, 0, 0);
            Assert.Equal(1, payload.PotionShelf1_Count);
            Assert.Equal(fortune.ItemId, payload.ActiveOffensivePotionId);
            Assert.Equal((int)((each - each / 2) * 1000), payload.OffensivePotionDurationMs);
        }

        private static TickStatePayload Hunter(long playerId, long now, long elapsed) => new TickStatePayload
        {
            PlayerId = playerId,
            LastLogoutTimestamp = now - elapsed,
            ActiveActivityId = 31,
            CurrentLevel = 1,
            STR = 50, DEX = 50, CON = 50, LCK = 25,
            InventorySpaceRemaining = 1000,
            AutoEatThreshold = AutoEatDefaults.ThresholdPct,
            Food1_ItemId = ContentRegistry.RawFishItemIds.First(),
            Food1_Count = 100000,
        };

        /// <summary>
        /// Offline = online for a reward potion: a Goldmoss Tonic running the
        /// whole absence pays the live per-kill gold, with the potion's
        /// percentage, for every kill (gold does not change how fast anything
        /// dies, so both windows count the same kills).
        /// </summary>
        [Fact]
        public async Task AnOfflineWindowPaysThePotionsGoldForEveryKill()
        {
            const long elapsed = 3600L;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var goldmoss = Potion(PotionKind.Goldmoss);

            var plain = Hunter(950_250_101L, now, elapsed);
            var buffed = Hunter(950_250_102L, now, elapsed);
            buffed.OffensivePotionLoadedId = goldmoss.ItemId;
            buffed.OffensivePotionExpiresEpoch = now + 600; // still running at sign-in
            buffed.ActiveOffensivePotionId = goldmoss.ItemId;
            buffed.OffensivePotionDurationMs = 600_000;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                plain = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, plain, now);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                buffed = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, buffed, now);

            var monster = ContentRegistry.Monsters.ToArray().Single(m => m.Id == 31);
            float basePct = StatsCalculator.Calculate(50, 50, 50, 25).GoldAcquisitionMultiplierPct;
            long plainPerKill = CombatGoldReward.PerKill(in plain, in monster, basePct);
            long buffedPerKill = CombatGoldReward.PerKill(in buffed, in monster, basePct + goldmoss.Magnitude);
            Assert.True(buffedPerKill > plainPerKill, "the potion raised nothing");

            Assert.True(plain.OfflineGoldEarned > 0, "the offline window killed nothing");
            Assert.Equal(0L, plain.OfflineGoldEarned % plainPerKill);
            long kills = plain.OfflineGoldEarned / plainPerKill;
            Assert.Equal(kills * buffedPerKill, buffed.OfflineGoldEarned);

            // Still running at sign-in: the slot keeps its own countdown.
            Assert.Equal(goldmoss.ItemId, buffed.ActiveOffensivePotionId);
            Assert.Equal(600_000, buffed.OffensivePotionDurationMs);
        }

        /// <summary>
        /// A potion that ran out halfway pays for the stretch it ran and no
        /// more - before H2 it paid the whole absence or none of it.
        /// </summary>
        [Fact]
        public async Task APotionThatRanOutWhileAwayPaysOnlyItsStretch()
        {
            const long elapsed = 3600L;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var goldmoss = Potion(PotionKind.Goldmoss);

            var plain = Hunter(950_250_201L, now, elapsed);
            var half = Hunter(950_250_202L, now, elapsed);
            half.OffensivePotionLoadedId = goldmoss.ItemId;
            half.OffensivePotionExpiresEpoch = now - elapsed / 2; // ran out halfway

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                plain = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, plain, now);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                half = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, half, now);

            double full = plain.OfflineGoldEarned * (1 + goldmoss.Magnitude / 100.0);
            Assert.InRange(half.OfflineGoldEarned, plain.OfflineGoldEarned * 1.02, full * 0.98);
            Assert.Equal(0, half.ActiveOffensivePotionId);
        }
    }
}

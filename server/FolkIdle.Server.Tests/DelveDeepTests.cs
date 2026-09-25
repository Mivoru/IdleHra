using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE DEEP (task 37) - the engine against a real Postgres.
    ///
    /// What these are for: the toll has to LEAVE the gold row, the stake has to
    /// be the server's and frozen, the records have to MOVE, and nothing in the
    /// Deep may mint a diamond or touch a stat. Spec §3, §5 and §6.
    /// </summary>
    [Collection("Postgres collection")]
    public class DelveDeepTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public DelveDeepTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private static readonly Random Pass = new ConstantRandom(0.0);
        private static readonly Random Fail = new ConstantRandom(0.99);

        private DelveEngine Engine(bool deep = true, DateTime? now = null)
        {
            var settings = now == null
                ? new DelveDeepSettings { Enabled = deep }
                : new DelveDeepSettings { Enabled = deep, UtcNow = () => now.Value };
            return new DelveEngine(_fixture.DbContextFactory, settings);
        }

        /// <summary>A player with gold and a sheet, a region-1 codex entry, a clean high-water table and no run.</summary>
        private async Task SeedAsync(long playerId, long gold, int attributes = 400, int weekDiamonds = 0, int weekKey = -1)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM player_gold_daily_high WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DelveRunRecords\" WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerRecords\" WHERE \"Id\" = {0}", playerId);

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                BaseStrength = attributes,
                BaseDexterity = attributes,
                BaseConstitution = attributes,
                BaseLuck = attributes,
                DelveDiamondsThisWeek = weekDiamonds,
                DelveWeekKey = weekKey >= 0 ? weekKey : (weekDiamonds > 0 ? DelveEngine.CurrentWeekKey(DateTime.UtcNow) : 0)
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = gold });
            if (!await db.MonsterCodexEntries.AnyAsync(c => c.PlayerId == playerId))
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = playerId, MonsterId = ContentRegistry.FirstCanonicalMonsterId, KillCount = 1 });
            }
            await db.SaveChangesAsync();
        }

        private async Task<long> GoldAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return (await db.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold")).Quantity;
        }

        private async Task SetGoldAsync(long playerId, long gold)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE \"CommodityRecords\" SET \"Quantity\" = {1} WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold'", playerId, gold);
        }

        private async Task<PlayerRecord> PlayerAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
        }

        private async Task<DelveRunRecord?> RunAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.DelveRunRecords.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId);
        }

        private static long RegionOneFee => DelveRegistry.EntryFeeForRegion(1);

        private async Task SetLanternsBoughtAsync(long playerId, int bought)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE \"DelveRunRecords\" SET \"LanternsBought\" = {1} WHERE \"PlayerId\" = {0}", playerId, bought);
        }

        /// <summary>A Deep run on floor 9 with its light out. Returns the frozen stake.</summary>
        private async Task<long> DeepRunInTheDarkAsync(DelveEngine engine, long playerId, long gold)
        {
            await SeedAsync(playerId, gold);
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            for (int i = 0; i < DelveRegistry.LanternCharges; i++) await engine.ChooseDoorAsync(playerId, 0, Fail);
            return view.StakeGold;
        }

        // ------------------------------------------------------------------
        // Lanterns (task 37 phase 2).
        // ------------------------------------------------------------------

        [Fact]
        public async Task TheLightGoingOutInTheDeepIsAnOfferNotAnEnd()
        {
            const long playerId = 982000030L;
            var engine = Engine();
            long stake = await DeepRunInTheDarkAsync(engine, playerId, 20_000_000);

            var view = await engine.GetViewAsync(playerId);
            Assert.True(view.Active);
            Assert.Equal(0, view.ChargesRemaining);
            Assert.Equal(stake, view.LanternPrice);
            Assert.Equal(DelveRegistry.MaxLanternRefills, view.LanternRefillsLeft);

            // A door in the dark is refused, visibly, and changes nothing.
            var door = await engine.ChooseDoorAsync(playerId, 0, Pass);
            Assert.Equal(DelveResult.LanternOut, door.Result);
            Assert.Equal(8, (await RunAsync(playerId))!.FloorsCleared);
        }

        [Fact]
        public async Task ALanternCostsExactlyStakeTimesTwoToTheKAndAddsOneCharge()
        {
            const long playerId = 982000031L;
            var engine = Engine();
            long stake = await DeepRunInTheDarkAsync(engine, playerId, 200_000_000);

            long spentBefore = (await PlayerAsync(playerId)).DelveDeepGoldSpent;
            long lanternsPaid = 0;
            for (int k = 0; k < 3; k++)
            {
                long before = await GoldAsync(playerId);
                var bought = await engine.BuyLanternAsync(playerId);

                Assert.Equal(DelveResult.Ok, bought.Result);
                Assert.Equal(stake * (1L << k), bought.GoldCharged);
                Assert.Equal(before - stake * (1L << k), await GoldAsync(playerId));
                Assert.Equal(1, bought.View.ChargesRemaining);
                Assert.Equal(k + 1, bought.View.LanternsBought);
                lanternsPaid += bought.GoldCharged;

                await engine.ChooseDoorAsync(playerId, 0, Fail);
            }

            // Every lantern is counted where Phase 3 and the eco audit read it.
            Assert.Equal(spentBefore + lanternsPaid, (await PlayerAsync(playerId)).DelveDeepGoldSpent);
        }

        [Fact]
        public async Task ALanternIsRefusedWhileTheLightStillBurnsOrWithNoDeepRun()
        {
            const long playerId = 982000032L;
            var engine = Engine();
            await SeedAsync(playerId, 20_000_000);

            Assert.Equal(DelveResult.NoRunInProgress, (await engine.BuyLanternAsync(playerId)).Result);

            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            Assert.Equal(DelveResult.NoRunInProgress, (await engine.BuyLanternAsync(playerId)).Result);

            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            long gold = await GoldAsync(playerId);
            Assert.Equal(DelveResult.ChargesRemain, (await engine.BuyLanternAsync(playerId)).Result);
            Assert.Equal(gold, await GoldAsync(playerId));
            Assert.Equal(0, (await RunAsync(playerId))!.LanternsBought);
        }

        [Fact]
        public async Task TheNinthLanternIsRefused()
        {
            const long playerId = 982000033L;
            var engine = Engine();
            await DeepRunInTheDarkAsync(engine, playerId, 20_000_000);
            await SetLanternsBoughtAsync(playerId, DelveRegistry.MaxLanternRefills);
            long gold = await GoldAsync(playerId);

            var refused = await engine.BuyLanternAsync(playerId);

            Assert.Equal(DelveResult.NoMoreLanterns, refused.Result);
            Assert.Equal(gold, await GoldAsync(playerId));
            Assert.Equal(0, refused.View.LanternPrice);
        }

        [Fact]
        public async Task ALanternThePlayerCannotAffordChangesNothing()
        {
            const long playerId = 982000034L;
            var engine = Engine();
            long stake = await DeepRunInTheDarkAsync(engine, playerId, 20_000_000);
            await SetGoldAsync(playerId, stake - 1);

            var refused = await engine.BuyLanternAsync(playerId);

            Assert.Equal(DelveResult.NotEnoughGold, refused.Result);
            Assert.Equal(stake - 1, await GoldAsync(playerId));
            var run = await RunAsync(playerId);
            Assert.Equal(0, run!.ChargesRemaining);
            Assert.Equal(0, run.LanternsBought);
        }

        /// <summary>
        /// Modul: THE CLIENT NEVER SENDS A PRICE. The engine's purchase takes the
        /// player and nothing else, so no request shape can carry one - asserted
        /// on the signature, where a later "just pass the quoted price" would
        /// have to show up.
        /// </summary>
        [Fact]
        public void BuyingALanternTakesNoPriceFromTheCaller()
        {
            var method = typeof(DelveEngine).GetMethod(nameof(DelveEngine.BuyLanternAsync))!;
            var parameters = method.GetParameters();
            Assert.Single(parameters);
            Assert.Equal("playerId", parameters[0].Name);
        }

        [Fact]
        public async Task DescendingFromTheBottomBanksWhatWalkingOutWouldAndDebitsTheFirstToll()
        {
            const long descender = 982000001L;
            const long walker = 982000002L;
            await SeedAsync(descender, gold: 5_000_000);
            await SeedAsync(walker, gold: 5_000_000);

            var engine = Engine();
            await engine.DevPlaceRunAtBottomAsync(descender);
            await engine.DevPlaceRunAtBottomAsync(walker);

            var view = await engine.GetViewAsync(descender);
            Assert.True(view.CanDescend);
            long expectedStake = DelveRegistry.Stake(RegionOneFee, 5_000_000);
            Assert.Equal(expectedStake, view.StakeGold);
            Assert.Equal(expectedStake, view.DescendQuote);

            var walked = await engine.BankAsync(walker);
            var descended = await engine.DescendAsync(descender, view.StakeGold, Pass);

            Assert.Equal(DelveResult.Ok, descended.Result);
            Assert.Equal(walked.DiamondsGranted, descended.DiamondsGranted);
            Assert.Equal(walked.GoldReturned, descended.GoldReturned);
            Assert.Equal(DelveRegistry.TollForFloor(expectedStake, 9), descended.GoldCharged);
            Assert.Equal(5_000_000 + descended.GoldReturned - descended.GoldCharged, await GoldAsync(descender));

            var player = await PlayerAsync(descender);
            Assert.Equal(walked.DiamondsGranted, player.PremiumDiamonds);
            Assert.Equal(walked.DiamondsGranted, player.DelveDiamondsThisWeek);
            // The toll is recorded as Deep spend; the walker, who paid none, has none.
            Assert.Equal(descended.GoldCharged, player.DelveDeepGoldSpent);
            Assert.Equal(0L, (await PlayerAsync(walker)).DelveDeepGoldSpent);

            Assert.True(descended.View.IsDeep);
            Assert.Equal(9, descended.View.CurrentFloor);
            Assert.Equal(8, descended.View.FloorsCleared);
            _output.WriteLine($"descent: {descended.DiamondsGranted} diamonds banked, {descended.GoldCharged:N0}g toll");
        }

        [Fact]
        public async Task ADescentPastTheCeilingBanksConsolationGoldAndThenTolls()
        {
            const long playerId = 982000003L;
            await SeedAsync(playerId, gold: 5_000_000, weekDiamonds: DelveRegistry.MaxDiamondsPerWeek);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            Assert.True(view.ConsolationGoldIfCapped > 0);

            var descended = await engine.DescendAsync(playerId, view.StakeGold, Pass);

            Assert.Equal(DelveResult.Ok, descended.Result);
            Assert.Equal(0, descended.DiamondsGranted);
            Assert.Equal(view.ConsolationGoldIfCapped, descended.GoldReturned);
            Assert.Equal(5_000_000 + view.ConsolationGoldIfCapped - view.DescendQuote, await GoldAsync(playerId));
        }

        [Fact]
        public async Task ADescentThePlayerCannotAffordChangesNothingAtAll()
        {
            const long playerId = 982000004L;
            await SeedAsync(playerId, gold: 1_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);

            var outcome = await engine.DescendAsync(playerId, long.MaxValue, Pass);

            Assert.Equal(DelveResult.NotEnoughGold, outcome.Result);
            Assert.Equal(1_000, await GoldAsync(playerId));

            var player = await PlayerAsync(playerId);
            Assert.Equal(0, player.PremiumDiamonds);
            Assert.Equal(0, player.DelveDiamondsThisWeek);
            Assert.Equal(0, player.DelveDeepestFloor);

            var run = await RunAsync(playerId);
            Assert.NotNull(run);
            Assert.False(run!.IsDeep);
            Assert.Equal(DelveRegistry.FloorCount, run.FloorsCleared);

            // And it can still walk out and be paid the full clear.
            var walked = await engine.BankAsync(playerId);
            Assert.Equal(DelveRegistry.DiamondsForBanking(DelveRegistry.FloorCount), walked.DiamondsGranted);
            Assert.True(outcome.View.CanDescend, "the refusal's view must still offer the descent and its price");
            Assert.Equal(view.DescendQuote, outcome.View.DescendQuote);
        }

        [Fact]
        public async Task TheStakeIsPricedOnTheSevenDayHighNotOnWhatIsHeldNow()
        {
            const long playerId = 982000005L;
            var day0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            await SeedAsync(playerId, gold: 50_000_000);

            // A checkpoint saw 50M; then 48M left for an alt.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await GoldHighWater.RecordAsync(db, playerId, 50_000_000, GoldHighWater.Today(day0));
            }
            await SetGoldAsync(playerId, 2_000_000);

            // Day 6 after: still priced on 50M.
            var laterInTheWeek = Engine(now: day0.AddDays(6));
            await laterInTheWeek.DevPlaceRunAtBottomAsync(playerId);
            var outcome = await laterInTheWeek.DescendAsync(playerId, long.MaxValue, Pass);
            Assert.Equal(DelveResult.Ok, outcome.Result);
            Assert.Equal(250_000, outcome.GoldCharged); // 0.005 x 50M, not the 7k region floor

            // Day 8: the 50M row has aged out; priced on what is held.
            await laterInTheWeek.BankAsync(playerId);
            await SetGoldAsync(playerId, 2_000_000);
            var nextWeek = Engine(now: day0.AddDays(8));
            await nextWeek.DevPlaceRunAtBottomAsync(playerId);
            var fresh = await nextWeek.GetViewAsync(playerId);
            Assert.Equal(DelveRegistry.Stake(RegionOneFee, 2_000_000 + fresh.ConsolationGoldIfCapped), fresh.StakeGold);
            Assert.True(fresh.StakeGold < 250_000);
        }

        [Fact]
        public async Task AQuoteBelowTheServersStakeIsRefusedAndOneAboveIsChargedTheServersNumber()
        {
            const long playerId = 982000006L;
            await SeedAsync(playerId, gold: 40_000_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);

            var refused = await engine.DescendAsync(playerId, view.StakeGold - 1, Pass);
            Assert.Equal(DelveResult.PriceChanged, refused.Result);
            Assert.Equal(40_000_000, await GoldAsync(playerId));
            Assert.False((await RunAsync(playerId))!.IsDeep);
            Assert.Equal(view.StakeGold, refused.View.StakeGold);
            Assert.Equal(0, (await PlayerAsync(playerId)).PremiumDiamonds);

            var charged = await engine.DescendAsync(playerId, view.StakeGold * 10, Pass);
            Assert.Equal(DelveResult.Ok, charged.Result);
            Assert.Equal(view.StakeGold, charged.GoldCharged);
            Assert.Equal(view.StakeGold, (await RunAsync(playerId))!.StakeGold);
        }

        [Fact]
        public async Task DescendingWithDoorsStillAheadIsRefused()
        {
            const long playerId = 982000007L;
            await SeedAsync(playerId, gold: 5_000_000);

            var engine = Engine();
            await engine.StartRunAsync(playerId, Pass);
            long before = await GoldAsync(playerId);

            var outcome = await engine.DescendAsync(playerId, long.MaxValue, Pass);

            Assert.Equal(DelveResult.NotAtTheBottom, outcome.Result);
            Assert.False(outcome.View.CanDescend);
            Assert.Equal(before, await GoldAsync(playerId));

            Assert.Equal(DelveResult.NoRunInProgress, (await engine.DescendAsync(982000099L, long.MaxValue, Pass)).Result);
        }

        [Fact]
        public async Task WithTheFlagOffTheDeepIsRefusedAndNeverOffered()
        {
            const long playerId = 982000008L;
            await SeedAsync(playerId, gold: 5_000_000);

            var engine = Engine(deep: false);
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);

            Assert.False(view.DeepEnabled);
            Assert.False(view.CanDescend);

            var outcome = await engine.DescendAsync(playerId, long.MaxValue, Pass);
            Assert.Equal(DelveResult.DeepDisabled, outcome.Result);
            Assert.Equal(5_000_000, await GoldAsync(playerId));
            Assert.False((await RunAsync(playerId))!.IsDeep);
        }

        [Fact]
        public async Task TheStakeIsFrozenAtTheDescent()
        {
            const long playerId = 982000009L;
            await SeedAsync(playerId, gold: 20_000_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            long stake = view.StakeGold;
            await engine.DescendAsync(playerId, stake, Pass);
            var cleared = await engine.ChooseDoorAsync(playerId, 0, Pass);
            Assert.Equal(DelveResult.FloorCleared, cleared.Result);

            // A fortune arrives mid-run. The next toll must not notice.
            await SetGoldAsync(playerId, 120_000_000);
            var landing = await engine.GetViewAsync(playerId);
            Assert.Equal(stake, landing.StakeGold);
            Assert.Equal(DelveRegistry.TollForFloor(stake, 10), landing.DescendQuote);

            var deeper = await engine.DescendAsync(playerId, landing.StakeGold, Pass);
            Assert.Equal(DelveResult.Ok, deeper.Result);
            Assert.Equal(DelveRegistry.TollForFloor(stake, 10), deeper.GoldCharged);
            Assert.Equal(10, deeper.View.CurrentFloor);
        }

        [Fact]
        public async Task ADeepDoorRollsAgainstTheDeepCurve()
        {
            const long playerId = 982000010L;
            await SeedAsync(playerId, gold: 5_000_000, attributes: 400);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            var descended = await engine.DescendAsync(playerId, view.StakeGold, Pass);

            double bottom = DelveRegistry.SuccessChance(400, DelveRegistry.FloorCount);
            double deep = DelveRegistry.DeepSuccessChance(400, 9);
            Assert.True(deep < bottom);
            Assert.Equal(deep, descended.View.DoorOdds[0], 10);

            // A roll between the two: floor 8's odds would pass it, the Deep's must not.
            var between = new ConstantRandom((deep + bottom) / 2.0);
            var outcome = await engine.ChooseDoorAsync(playerId, 0, between);
            Assert.Equal(DelveResult.ChargeLost, outcome.Result);
        }

        [Fact]
        public async Task AClearRaisesBothRecords()
        {
            const long playerId = 982000011L;
            await SeedAsync(playerId, gold: 5_000_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);

            var afterDescent = await PlayerAsync(playerId);
            Assert.Equal(8, afterDescent.DelveDeepestFloor);
            Assert.Equal(8, afterDescent.DelveDeepestThisWeek);

            var cleared = await engine.ChooseDoorAsync(playerId, 0, Pass);

            var player = await PlayerAsync(playerId);
            Assert.Equal(9, player.DelveDeepestFloor);
            Assert.Equal(9, player.DelveDeepestThisWeek);
            Assert.NotNull(player.DelveDeepestThisWeekAtUtc);
            Assert.Equal(9, cleared.View.DeepestFloor);
            Assert.True(cleared.View.AtLanding);
            Assert.True(cleared.View.CanDescend);
        }

        /// <summary>Bank first, then record: a stale week is rolled by the bank and the weekly record starts from zero.</summary>
        [Fact]
        public async Task AStaleWeekIsResetBeforeTheWeeklyRecordIsWritten_BankFirst()
        {
            const long playerId = 982000012L;
            int lastWeek = DelveEngine.CurrentWeekKey(DateTime.UtcNow.AddDays(-7));
            await SeedAsync(playerId, gold: 5_000_000, weekDiamonds: DelveRegistry.MaxDiamondsPerWeek, weekKey: lastWeek);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"PlayerRecords\" SET \"DelveDeepestThisWeek\" = 30, \"DelveDeepestFloor\" = 30 WHERE \"Id\" = {0}", playerId);
            }

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            Assert.Equal(0, view.DeepestThisWeek);
            var descended = await engine.DescendAsync(playerId, view.StakeGold, Pass);

            // Last week's ceiling did not shut this week's tap...
            Assert.Equal(DelveRegistry.DiamondsForBanking(DelveRegistry.FloorCount), descended.DiamondsGranted);
            var player = await PlayerAsync(playerId);
            Assert.Equal(DelveEngine.CurrentWeekKey(DateTime.UtcNow), player.DelveWeekKey);
            // ...and last week's 30 did not survive into this week's board.
            Assert.Equal(8, player.DelveDeepestThisWeek);
            Assert.Equal(30, player.DelveDeepestFloor);
        }

        /// <summary>Record first, then bank: a Deep clear that turns the week clears the diamond counter too.</summary>
        [Fact]
        public async Task AStaleWeekIsResetBeforeTheWeeklyRecordIsWritten_RecordFirst()
        {
            const long playerId = 982000013L;
            await SeedAsync(playerId, gold: 5_000_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);

            // The week turns over while the run is in the Deep.
            int lastWeek = DelveEngine.CurrentWeekKey(DateTime.UtcNow.AddDays(-7));
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"PlayerRecords\" SET \"DelveWeekKey\" = {1}, \"DelveDiamondsThisWeek\" = {2}, \"DelveDeepestThisWeek\" = 50 WHERE \"Id\" = {0}",
                    playerId, lastWeek, DelveRegistry.MaxDiamondsPerWeek);
            }

            await engine.ChooseDoorAsync(playerId, 0, Pass);

            var player = await PlayerAsync(playerId);
            Assert.Equal(DelveEngine.CurrentWeekKey(DateTime.UtcNow), player.DelveWeekKey);
            Assert.Equal(9, player.DelveDeepestThisWeek);
            Assert.Equal(0, player.DelveDiamondsThisWeek);

            // And a bank later this week is paid in full, not shut by last week's count.
            await engine.BankAsync(playerId);
            await engine.DevPlaceRunAtBottomAsync(playerId);
            var walked = await engine.BankAsync(playerId);
            Assert.Equal(DelveRegistry.DiamondsForBanking(DelveRegistry.FloorCount), walked.DiamondsGranted);
            Assert.Equal(9, (await PlayerAsync(playerId)).DelveDeepestThisWeek);
        }

        [Fact]
        public async Task ADoorOutsideTheOfferedRangeIsRefusedAndChangesNothingInTheDeep()
        {
            const long playerId = 982000014L;
            await SeedAsync(playerId, gold: 5_000_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            long gold = await GoldAsync(playerId);

            foreach (int door in new[] { -1, DelveRegistry.DoorsPerFloor, 99 })
            {
                Assert.Equal(DelveResult.InvalidDoor, (await engine.ChooseDoorAsync(playerId, door, Pass)).Result);
            }

            var run = await RunAsync(playerId);
            Assert.Equal(8, run!.FloorsCleared);
            Assert.Equal(9, run.CurrentFloor);
            Assert.Equal(DelveRegistry.LanternCharges, run.ChargesRemaining);
            Assert.Equal(gold, await GoldAsync(playerId));
        }

        [Fact]
        public async Task ADeepRunSurvivesARelogin()
        {
            const long playerId = 982000015L;
            await SeedAsync(playerId, gold: 5_000_000);

            var view = await Engine().DevPlaceRunAtBottomAsync(playerId);
            await Engine().DescendAsync(playerId, view.StakeGold, Pass);

            // A new engine is a restarted server: the row is the run.
            var again = await Engine().GetViewAsync(playerId);
            Assert.True(again.Active);
            Assert.True(again.IsDeep);
            Assert.Equal(9, again.CurrentFloor);
            Assert.Equal(view.StakeGold, again.StakeGold);
            Assert.False(again.AtLanding);
        }

        [Fact]
        public async Task WalkingOutOfTheDeepPaysNothingAndEndsTheRun()
        {
            const long playerId = 982000016L;
            await SeedAsync(playerId, gold: 5_000_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            long gold = await GoldAsync(playerId);
            int diamonds = (await PlayerAsync(playerId)).PremiumDiamonds;

            var walked = await engine.BankAsync(playerId);

            Assert.Equal(DelveResult.Ok, walked.Result);
            Assert.Equal(0, walked.DiamondsGranted);
            Assert.Equal(0, walked.GoldReturned);
            Assert.Equal(gold, await GoldAsync(playerId));
            Assert.Equal(diamonds, (await PlayerAsync(playerId)).PremiumDiamonds);
            Assert.Null(await RunAsync(playerId));
        }

        // ------------------------------------------------------------------
        // Guard rails (spec §6).
        // ------------------------------------------------------------------

        [Fact]
        public async Task AWholeDeepSessionMintsNoDiamonds()
        {
            const long walkerId = 982000017L;
            const long loserId = 982000018L;
            var engine = Engine();

            // Descend, three doors (a toll between each), walk out.
            await SeedAsync(walkerId, gold: 50_000_000);
            var view = await engine.DevPlaceRunAtBottomAsync(walkerId);
            var descended = await engine.DescendAsync(walkerId, view.StakeGold, Pass);
            var afterBank = await PlayerAsync(walkerId);
            Assert.Equal(descended.DiamondsGranted, afterBank.PremiumDiamonds);

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(DelveResult.FloorCleared, (await engine.ChooseDoorAsync(walkerId, 0, Pass)).Result);
                if (i < 2)
                {
                    var landing = await engine.GetViewAsync(walkerId);
                    Assert.Equal(DelveResult.Ok, (await engine.DescendAsync(walkerId, landing.StakeGold, Pass)).Result);
                }
            }
            await engine.BankAsync(walkerId);

            var walker = await PlayerAsync(walkerId);
            Assert.Equal(afterBank.PremiumDiamonds, walker.PremiumDiamonds);
            Assert.Equal(afterBank.DelveDiamondsThisWeek, walker.DelveDiamondsThisWeek);
            Assert.Equal(11, walker.DelveDeepestFloor);

            // Descend, buy a lantern, fail to 0 charges with every lantern
            // bought: RunLost, and still nothing minted.
            await SeedAsync(loserId, gold: 50_000_000);
            view = await engine.DevPlaceRunAtBottomAsync(loserId);
            await engine.DescendAsync(loserId, view.StakeGold, Pass);
            var afterLoserBank = await PlayerAsync(loserId);

            for (int i = 0; i < DelveRegistry.LanternCharges; i++) await engine.ChooseDoorAsync(loserId, 0, Fail);
            Assert.Equal(DelveResult.Ok, (await engine.BuyLanternAsync(loserId)).Result);
            await SetLanternsBoughtAsync(loserId, DelveRegistry.MaxLanternRefills);

            var last = await engine.ChooseDoorAsync(loserId, 0, Fail);
            Assert.Equal(DelveResult.RunLost, last.Result);

            var loser = await PlayerAsync(loserId);
            Assert.Equal(afterLoserBank.PremiumDiamonds, loser.PremiumDiamonds);
            Assert.Equal(afterLoserBank.DelveDiamondsThisWeek, loser.DelveDiamondsThisWeek);
            Assert.Equal(8, loser.DelveDeepestFloor);
            Assert.Null(await RunAsync(loserId));
        }

        /// <summary>
        /// Modul: THE DEEP BUYS NO POWER, asserted as a whole-row diff. After the
        /// descent's floors-1-8 bank, a Deep session may change only the record
        /// columns, the week key and the title - every stat, attribute,
        /// currency and combat column must read exactly as before.
        /// </summary>
        [Fact]
        public async Task ADeepSessionChangesOnlyTheAllowedPlayerColumns()
        {
            const long playerId = 982000019L;
            await SeedAsync(playerId, gold: 50_000_000);

            var engine = Engine();
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            var before = await PlayerAsync(playerId);

            await engine.ChooseDoorAsync(playerId, 0, Pass);
            var landing = await engine.GetViewAsync(playerId);
            await engine.DescendAsync(playerId, landing.StakeGold, Pass);
            await engine.ChooseDoorAsync(playerId, 0, Fail);
            await engine.ChooseDoorAsync(playerId, 0, Pass);
            await engine.BankAsync(playerId);

            var after = await PlayerAsync(playerId);

            var allowed = new[]
            {
                nameof(PlayerRecord.DelveDeepestFloor),
                nameof(PlayerRecord.DelveDeepestThisWeek),
                nameof(PlayerRecord.DelveDeepestThisWeekAtUtc),
                nameof(PlayerRecord.DelveWeekKey),
                nameof(PlayerRecord.ActiveTitleSlug),
                // What the tolls came to - its own test pins the amount.
                nameof(PlayerRecord.DelveDeepGoldSpent),
            };

            foreach (var property in typeof(PlayerRecord).GetProperties())
            {
                if (allowed.Contains(property.Name)) continue;
                if (!property.PropertyType.IsValueType && property.PropertyType != typeof(string)) continue;
                Assert.True(Equals(property.GetValue(before), property.GetValue(after)),
                    $"a Deep session changed PlayerRecords.{property.Name}: {property.GetValue(before)} -> {property.GetValue(after)}");
            }
            Assert.Equal(10, after.DelveDeepestFloor);
        }

        // Modul: THE DEEP SHOWS UP IN THE ECONOMY'S LEDGER (2026-09-25). The eco
        // audit counted guild sinks and market fees as consumed and nothing
        // else, so the game's biggest repeatable sink was invisible to the one
        // table Phase 3 measures with. The audit now sums DelveDeepGoldSpent.
        [Fact]
        public async Task TheEcoAuditCountsDeepSpendAsConsumed()
        {
            const long playerId = 982000041L;
            await SeedAsync(playerId, gold: 1_000_000);
            var audit = new EcoTelemetryEngine(_fixture.ServiceProvider);

            async Task<long> ConsumedAsync()
            {
                await audit.ExecuteAuditAsync(System.Threading.CancellationToken.None);
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                return await db.EcoTelemetryLedgers.AsNoTracking().OrderByDescending(l => l.LogId).Select(l => l.TotalGoldConsumed).FirstAsync();
            }

            long before = await ConsumedAsync();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"PlayerRecords\" SET \"DelveDeepGoldSpent\" = \"DelveDeepGoldSpent\" + 7777777 WHERE \"Id\" = {0}", playerId);
            }
            long after = await ConsumedAsync();

            Assert.Equal(7_777_777L, after - before);
        }

        private static string ServerFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName, "server", "FolkIdle.Server" }.Concat(parts).ToArray());
        }

        /// <summary>Code only: comments may (and do) name the path they are explaining the absence of.</summary>
        internal static string CodeWithoutComments(string path)
        {
            var lines = File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith("//") && !l.StartsWith("*") && !l.StartsWith("/*"));
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Two gold paths, and mixing them pays the player twice: every Deep
        /// price is a DB debit, never the tick's pending delta, and never the
        /// chest-sale queue.
        /// </summary>
        [Fact]
        public void TheDeepTouchesNeitherTheTicksGoldPathNorTheChestSaleQueue()
        {
            foreach (var file in new[] { ServerFile("Domain", "Economy", "DelveEngine.cs"), ServerFile("Engine", "DelveRegistry.cs") })
            {
                string code = CodeWithoutComments(file);
                Assert.DoesNotContain("RedisPendingGoldDelta", code);
                Assert.DoesNotContain("ChestSaleGoldQueue", code);
            }
        }
    }
}

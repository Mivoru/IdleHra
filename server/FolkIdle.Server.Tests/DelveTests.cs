using System;
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
    /// A Random whose rolls are decided by the test rather than by chance.
    ///
    /// Modul: the odds in DelveRegistry are CLAMPED at both ends - 0.25 floor,
    /// 0.92 ceiling - precisely so no sheet ever makes a run certain in either
    /// direction. That is right for the game and useless for a test, so the
    /// determinism comes from the roll instead: 0.0 passes every check
    /// (0.0 &lt; 0.25) and 0.99 fails every one (0.99 &gt; 0.92).
    /// </summary>
    internal sealed class ConstantRandom : Random
    {
        private readonly double _value;
        public ConstantRandom(double value) => _value = value;
        public override double NextDouble() => _value;
        public override int Next(int maxValue) => 0;
    }

    /// <summary>
    /// THE DELVE - the rules, with no database in sight.
    /// </summary>
    public class DelveRegistryTests
    {
        private readonly ITestOutputHelper _output;
        public DelveRegistryTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void TheFloorLadderNeverDescendsAndEndsUnderTheLastMilestone()
        {
            int previous = 0;
            for (int floor = 1; floor <= DelveRegistry.FloorCount; floor++)
            {
                int requirement = DelveRegistry.RequirementForFloor(floor);
                _output.WriteLine($"floor {floor}: asks {requirement}");
                Assert.True(requirement > previous, $"floor {floor} asks less than floor {floor - 1}");
                previous = requirement;
            }

            // Modul: pinned to the milestone ladder rather than invented. The
            // bottom floor must sit UNDER the last attribute milestone (300),
            // or no character in the game could ever reach it - and over the
            // fourth (200), or a single maxed attribute would walk the whole
            // run.
            Assert.InRange(previous, 201, 299);
        }

        [Fact]
        public void SuccessChanceIsADiminishingCurveWithBothEndsClamped()
        {
            // Linear-and-uncapped is the one shape PowerCeilingTests never
            // allows, and this is a lever a player can pour 300 points into.
            int par = DelveRegistry.RequirementForFloor(4);
            double atHalf = DelveRegistry.SuccessChance(par / 2, 4);
            double atPar = DelveRegistry.SuccessChance(par, 4);
            double atDouble = DelveRegistry.SuccessChance(par * 2, 4);
            double atQuadruple = DelveRegistry.SuccessChance(par * 4, 4);
            double atOctuple = DelveRegistry.SuccessChance(par * 8, 4);

            _output.WriteLine(
                $"floor 4 odds: half {atHalf:P1}, par {atPar:P1}, x2 {atDouble:P1}, x4 {atQuadruple:P1}, x8 {atOctuple:P1}");

            Assert.True(atHalf < atPar && atPar < atDouble && atDouble < atQuadruple, "the curve must rise with the sheet");
            Assert.Equal(0.5, atPar, 3);

            // Modul: measured across EQUAL DOUBLINGS, which is the only way to
            // ask whether a curve diminishes. The first draft of this compared
            // par->x2 against x2->x10 and failed a perfectly good curve,
            // because x10 is five doublings of headroom and of course gains
            // more in total. A test that measures the wrong interval reports a
            // defect that is not there.
            double firstDoubling = atDouble - atPar;
            double secondDoubling = atQuadruple - atDouble;
            double thirdDoubling = atOctuple - atQuadruple;
            _output.WriteLine($"per doubling: +{firstDoubling:P1}, +{secondDoubling:P1}, +{thirdDoubling:P1}");
            Assert.True(firstDoubling > secondDoubling && secondDoubling > thirdDoubling,
                "each doubling of the sheet must buy less than the one before it");

            // The floor is a real clamp and fires on a hopeless door.
            Assert.Equal(DelveRegistry.MinSuccessChance, DelveRegistry.SuccessChance(0, DelveRegistry.FloorCount), 3);

            // The ceiling is the curve's asymptote: approached, never passed.
            // Asserting it as an equality is what caught MaxSuccessChance being
            // set to a value the clamp could not reach - a constant that read
            // like a rule and enforced nothing.
            Assert.Equal(DelveRegistry.MaxSuccessChance, DelveRegistry.SuccessChance(100_000_000, 1), 3);
            Assert.True(DelveRegistry.SuccessChance(int.MaxValue, 1) <= DelveRegistry.MaxSuccessChance);
        }

        [Fact]
        public void FortuneBuysKnowingAndCannotBuyCertainty()
        {
            double bare = DelveRegistry.DoorRevealChance(0);
            double built = DelveRegistry.DoorRevealChance(100);
            double absurd = DelveRegistry.DoorRevealChance(100_000);

            _output.WriteLine($"reveal: 0 Fortune {bare:P0}, 100 Fortune {built:P0}, 100k Fortune {absurd:P0}");

            Assert.True(built > bare, "Fortune must move the reveal rate - it is the stat's second home");
            Assert.True(absurd <= 0.90, "the reveal rate must never reach certainty");
            Assert.True(built - bare > absurd - built, "and it must diminish");
        }

        [Fact]
        public void ThePayoutRisesWithDepthAndAFullClearIsWorthAboutTwentyDiamonds()
        {
            int previous = -1;
            for (int floors = 1; floors <= DelveRegistry.FloorCount; floors++)
            {
                int diamonds = DelveRegistry.DiamondsForBanking(floors);
                _output.WriteLine(
                    $"bank at floor {floors}: {DelveRegistry.CumulativeEmbers(floors)} embers " +
                    $"x{DelveRegistry.BankMultiplier(floors):F2} = {diamonds} diamonds");
                Assert.True(diamonds >= previous, "a deeper run may never pay less");
                previous = diamonds;
            }

            int full = DelveRegistry.DiamondsForBanking(DelveRegistry.FloorCount);

            // Modul: THE TAP HAS TO STAY UNDER THE STORE. The smallest
            // purchasable pack is 500 diamonds and the premium Chronicle pass
            // is 950. Three full clears reaching the weekly ceiling is the
            // shape this is tuned to; a full clear worth much more than that
            // would make the ceiling the only thing anyone ever saw.
            Assert.InRange(full, 15, 25);
            Assert.InRange(DelveRegistry.MaxDiamondsPerWeek / (double)full, 2.0, 4.0);
        }

        [Fact]
        public void BankingNothingPaysNothing()
        {
            Assert.Equal(0, DelveRegistry.DiamondsForBanking(0));
            Assert.Equal(0, DelveRegistry.ConsolationGold(250_000, 0));
        }
    }

    /// <summary>
    /// THE DELVE - the engine, against a real Postgres.
    ///
    /// What these are actually for: the gold has to LEAVE, the diamonds have to
    /// ARRIVE, the ceiling has to hold, and none of it may be reachable from a
    /// number the client chose. A screen full of doors that all silently do
    /// nothing renders perfectly.
    /// </summary>
    [Collection("Postgres collection")]
    public class DelveEngineTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public DelveEngineTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private DelveEngine Engine() => new DelveEngine(_fixture.DbContextFactory);

        /// <summary>
        /// A player with gold, a sheet, and a codex entry deep enough to price
        /// the run. Returns the id.
        /// </summary>
        private async Task<long> SeedPlayerAsync(long playerId, long gold, int attributeValue, int regionMonsterId, int weekDiamonds = 0)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var existing = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
            if (existing != null) db.PlayerRecords.Remove(existing);
            var existingRun = await db.DelveRunRecords.SingleOrDefaultAsync(r => r.PlayerId == playerId);
            if (existingRun != null) db.DelveRunRecords.Remove(existingRun);
            await db.SaveChangesAsync();

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                BaseStrength = attributeValue,
                BaseDexterity = attributeValue,
                BaseConstitution = attributeValue,
                BaseLuck = attributeValue,
                PremiumDiamonds = 0,
                DelveDiamondsThisWeek = weekDiamonds,
                DelveWeekKey = weekDiamonds > 0 ? DelveEngine.CurrentWeekKey(DateTime.UtcNow) : 0
            });

            var goldRow = await db.CommodityRecords.SingleOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            if (goldRow != null) db.CommodityRecords.Remove(goldRow);
            await db.SaveChangesAsync();

            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = gold });

            var codex = await db.MonsterCodexEntries.SingleOrDefaultAsync(c => c.PlayerId == playerId && c.MonsterId == regionMonsterId);
            if (codex == null)
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = playerId, MonsterId = regionMonsterId, KillCount = 1 });
            }

            await db.SaveChangesAsync();
            return playerId;
        }

        private async Task<long> ReadGoldAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var row = await db.CommodityRecords.AsNoTracking().SingleOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            return row?.Quantity ?? 0;
        }

        private async Task<PlayerRecord> ReadPlayerAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
        }

        [Fact]
        public async Task StartingARunTakesTheEntryFeeAndOpensAFloor()
        {
            const long playerId = 980000201L;
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 150, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            var outcome = await Engine().StartRunAsync(playerId, new ConstantRandom(0.0));

            Assert.Equal(DelveResult.Ok, outcome.Result);
            Assert.True(outcome.View.Active);
            Assert.Equal(1, outcome.View.CurrentFloor);
            Assert.Equal(DelveRegistry.LanternCharges, outcome.View.ChargesRemaining);
            Assert.Equal(DelveRegistry.DoorsPerFloor, outcome.View.DoorDemands.Length);

            long fee = DelveRegistry.EntryFeeForRegion(1);
            Assert.Equal(1_000_000 - fee, await ReadGoldAsync(playerId));
            _output.WriteLine($"region 1 entry took {fee:N0}g");
        }

        [Fact]
        public async Task ASecondRunIsRefusedRatherThanReplacingTheFirst()
        {
            const long playerId = 980000202L;
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 150, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            var engine = Engine();
            await engine.StartRunAsync(playerId, new ConstantRandom(0.0));
            long afterFirst = await ReadGoldAsync(playerId);

            var second = await engine.StartRunAsync(playerId, new ConstantRandom(0.0));

            Assert.Equal(DelveResult.RunAlreadyInProgress, second.Result);
            Assert.Equal(afterFirst, await ReadGoldAsync(playerId));
        }

        [Fact]
        public async Task APlayerWhoCannotAffordTheGateIsToldSoAndChargedNothing()
        {
            const long playerId = 980000203L;
            await SeedPlayerAsync(playerId, gold: 10, attributeValue: 150, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            var outcome = await Engine().StartRunAsync(playerId, new ConstantRandom(0.0));

            Assert.Equal(DelveResult.NotEnoughGold, outcome.Result);
            Assert.Equal(10, await ReadGoldAsync(playerId));

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null(await db.DelveRunRecords.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId));
        }

        [Fact]
        public async Task ClearingEveryFloorReachesTheBottomAndBankingPaysDiamonds()
        {
            const long playerId = 980000204L;
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 400, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            var engine = Engine();
            var pass = new ConstantRandom(0.0);
            await engine.StartRunAsync(playerId, pass);

            DelveActionOutcome last = null!;
            for (int i = 0; i < DelveRegistry.FloorCount; i++)
            {
                last = await engine.ChooseDoorAsync(playerId, 0, pass);
            }

            Assert.Equal(DelveResult.AtTheBottom, last.Result);
            Assert.Equal(DelveRegistry.FloorCount, last.View.FloorsCleared);

            var banked = await engine.BankAsync(playerId);
            Assert.Equal(DelveResult.Ok, banked.Result);
            Assert.Equal(DelveRegistry.DiamondsForBanking(DelveRegistry.FloorCount), banked.DiamondsGranted);

            var player = await ReadPlayerAsync(playerId);
            Assert.Equal(banked.DiamondsGranted, player.PremiumDiamonds);
            Assert.Equal(banked.DiamondsGranted, player.DelveDiamondsThisWeek);
            _output.WriteLine($"a full clear paid {banked.DiamondsGranted} diamonds");

            // Modul: the run ROW must go. Leaving it would lock the player out
            // of every future run behind a RunAlreadyInProgress they could
            // never clear.
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null(await db.DelveRunRecords.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId));
        }

        [Fact]
        public async Task ThreeFailuresEndTheRunWithNothing()
        {
            const long playerId = 980000205L;
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 1, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            var engine = Engine();
            await engine.StartRunAsync(playerId, new ConstantRandom(0.0));

            var fail = new ConstantRandom(0.99);
            var first = await engine.ChooseDoorAsync(playerId, 0, fail);
            var second = await engine.ChooseDoorAsync(playerId, 0, fail);
            var third = await engine.ChooseDoorAsync(playerId, 0, fail);

            Assert.Equal(DelveResult.ChargeLost, first.Result);
            Assert.Equal(DelveRegistry.LanternCharges - 1, first.View.ChargesRemaining);
            Assert.Equal(DelveResult.ChargeLost, second.Result);
            Assert.Equal(DelveResult.RunLost, third.Result);
            Assert.False(third.View.Active);

            var player = await ReadPlayerAsync(playerId);
            Assert.Equal(0, player.PremiumDiamonds);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null(await db.DelveRunRecords.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId));
        }

        [Fact]
        public async Task TheWeeklyCeilingCapsTheDiamondsAndPaysGoldForTheRest()
        {
            const long playerId = 980000206L;

            // One diamond of headroom left this week, against a full clear
            // worth many more.
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 400,
                regionMonsterId: ContentRegistry.FirstCanonicalMonsterId,
                weekDiamonds: DelveRegistry.MaxDiamondsPerWeek - 1);

            var engine = Engine();
            var pass = new ConstantRandom(0.0);
            await engine.StartRunAsync(playerId, pass);
            long goldAfterEntry = await ReadGoldAsync(playerId);

            for (int i = 0; i < DelveRegistry.FloorCount; i++) await engine.ChooseDoorAsync(playerId, 0, pass);

            var banked = await engine.BankAsync(playerId);

            Assert.Equal(DelveResult.Ok, banked.Result);
            Assert.Equal(1, banked.DiamondsGranted);
            Assert.True(banked.GoldReturned > 0,
                "past the ceiling the SINK must keep working - a run that pays nothing at all is a refusal wearing a payout's clothes");
            Assert.Equal(goldAfterEntry + banked.GoldReturned, await ReadGoldAsync(playerId));

            var player = await ReadPlayerAsync(playerId);
            Assert.Equal(DelveRegistry.MaxDiamondsPerWeek, player.DelveDiamondsThisWeek);

            // And the gold returned is still less than the fee - the sink has
            // to destroy gold on net even when the tap is shut.
            Assert.True(banked.GoldReturned < DelveRegistry.EntryFeeForRegion(1),
                "a capped run must not return more than it cost");
            _output.WriteLine($"ceiling reached: 1 diamond + {banked.GoldReturned:N0}g back against a {DelveRegistry.EntryFeeForRegion(1):N0}g fee");
        }

        [Fact]
        public async Task ADoorOutsideTheOfferedRangeIsRefusedAndChangesNothing()
        {
            const long playerId = 980000207L;
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 400, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            var engine = Engine();
            await engine.StartRunAsync(playerId, new ConstantRandom(0.0));

            foreach (int door in new[] { -1, DelveRegistry.DoorsPerFloor, 99 })
            {
                var outcome = await engine.ChooseDoorAsync(playerId, door, new ConstantRandom(0.0));
                Assert.Equal(DelveResult.InvalidDoor, outcome.Result);
            }

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var run = await db.DelveRunRecords.AsNoTracking().SingleAsync(r => r.PlayerId == playerId);
            Assert.Equal(0, run.FloorsCleared);
            Assert.Equal(DelveRegistry.LanternCharges, run.ChargesRemaining);
        }

        [Fact]
        public async Task ADoorThatHasNotShownItselfPublishesNoOdds()
        {
            const long playerId = 980000208L;
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 150, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            // 0.99 fails every reveal roll, so only the guaranteed door shows.
            var outcome = await Engine().StartRunAsync(playerId, new ConstantRandom(0.99));

            int hidden = 0;
            for (int i = 0; i < DelveRegistry.DoorsPerFloor; i++)
            {
                if (outcome.View.DoorDemands[i] < 0)
                {
                    hidden++;
                    // Modul: publishing real odds for a hidden door would give
                    // its demand away by inference and make Fortune worthless.
                    Assert.Equal(-1.0, outcome.View.DoorOdds[i]);
                }
            }

            Assert.Equal(DelveRegistry.DoorsPerFloor - 1, hidden);
        }

        [Fact]
        public async Task BankingWithNothingClearedEndsTheRunAndPaysNothing()
        {
            const long playerId = 980000209L;
            await SeedPlayerAsync(playerId, gold: 1_000_000, attributeValue: 150, regionMonsterId: ContentRegistry.FirstCanonicalMonsterId);

            var engine = Engine();
            await engine.StartRunAsync(playerId, new ConstantRandom(0.0));
            long afterEntry = await ReadGoldAsync(playerId);

            var banked = await engine.BankAsync(playerId);

            Assert.Equal(DelveResult.Ok, banked.Result);
            Assert.Equal(0, banked.DiamondsGranted);
            Assert.Equal(0, banked.GoldReturned);
            Assert.Equal(afterEntry, await ReadGoldAsync(playerId));

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null(await db.DelveRunRecords.AsNoTracking().SingleOrDefaultAsync(r => r.PlayerId == playerId));
        }

        [Fact]
        public async Task TheEntryFeeFollowsHowFarThePlayerHasActuallyTravelled()
        {
            const long playerId = 980000210L;

            // A region-5 monster in the codex - the fee must follow it, not the
            // PlayerRegionCompletions ledger that needs a thousand kills.
            int region5Monster = ContentRegistry.FirstCanonicalMonsterId + 4 * ContentRegistry.MonstersPerRegion;
            await SeedPlayerAsync(playerId, gold: 5_000_000, attributeValue: 150, regionMonsterId: region5Monster);

            var view = await Engine().GetViewAsync(playerId);

            _output.WriteLine($"region {view.HighestRegionReached} prices a run at {view.EntryFeeForNextRun:N0}g");
            Assert.Equal(5, view.HighestRegionReached);
            Assert.Equal(DelveRegistry.EntryFeeForRegion(5), view.EntryFeeForNextRun);
            Assert.True(view.EntryFeeForNextRun > DelveRegistry.EntryFeeForRegion(1) * 10,
                "the fee has to scale with income or it is decoration at the top of the game");
        }
    }
}

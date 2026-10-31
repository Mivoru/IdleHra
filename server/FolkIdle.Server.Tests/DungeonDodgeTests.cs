using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The live run (slice 3) and the party (slice 4): the runner stops at
    /// every telegraph, a dodge's bonus and a miss's penalty change the rest of
    /// the run, the server judges swipes itself, and a second champion joins
    /// once the dungeon's sixth stage is beaten.
    /// </summary>
    public class DungeonDodgeTests
    {
        public DungeonDodgeTests() => ContentRegistry.Initialize();

        private static TickStatePayload Character(int level, int attribute) => new()
        {
            PlayerId = 1,
            CurrentLevel = level,
            STR = attribute, DEX = attribute, CON = attribute, LCK = attribute / 2,
            AutoEatThreshold = 50,
        };

        private static readonly int Fish = FoodRegistry.FirstRawFishOfTier(1);

        [Fact]
        public void WithoutTelegraphsTheRunnerIsTheSimulator()
        {
            var direct = DungeonSimulator.Run(Character(40, 300), DungeonKind.Xp, 3, Fish, 100);
            var runner = new DungeonRunner(Character(40, 300), DungeonKind.Xp, 3, Fish, 100, new[] { 0 }, challenges: false);
            runner.Advance();
            Assert.Equal(direct.Won, runner.Result!.Won);
            Assert.Equal(direct.Seconds, runner.Result.Seconds);
            Assert.Equal(direct.MealsEaten, runner.Result.MealsEaten);
        }

        [Fact]
        public void ALiveRunStopsAtEveryWaveAndTwiceMoreInTheBossFight()
        {
            var runner = new DungeonRunner(Character(60, 2000), DungeonKind.Xp, 1, Fish, 100, new[] { 0 }, challenges: true);
            int pauses = 0;
            runner.Advance();
            while (runner.Result == null)
            {
                Assert.True(runner.Paused);
                Assert.Equal("challenge", runner.TakeNewEvents().Last().Kind);
                pauses++;
                runner.Dodge();
                Assert.True(runner.Choose("fury") || runner.Choose("keen") || runner.Choose("iron"));
                runner.Advance();
                Assert.True(pauses < 20);
            }
            // Waves 2 and 4, the boss's opening, and the half-health one when he lives that long.
            Assert.InRange(pauses, 3, 4);
            Assert.True(runner.Result.Won);
            Assert.Equal(pauses, runner.Result.Dodged);
        }

        [Fact]
        public void ABonusMakesTheRestOfTheRunFasterAndAMissCostsHealthAndAPenalty()
        {
            DungeonRunner Fresh() => new(Character(40, 300), DungeonKind.Xp, 2, Fish, 100, new[] { 0 }, challenges: true);

            var plain = Fresh();
            plain.Advance();
            plain.Resume();
            var furious = Fresh();
            furious.Advance();
            furious.Dodge();
            Assert.True(furious.Choose("fury"));
            Assert.Equal(25, furious.Mods.DamagePct);
            // Play both to the end, answering nothing else.
            foreach (var r in new[] { plain, furious })
            {
                r.Advance();
                while (r.Result == null) { r.Resume(); r.Advance(); }
            }
            Assert.True(furious.Result!.Seconds <= plain.Result!.Seconds);

            var missed = Fresh();
            missed.Advance();
            var hpBefore = missed.TakeNewEvents().Last().PlayerHpPct;
            string? penalty = missed.Miss(new Random(3));
            Assert.NotNull(penalty);
            var after = missed.TakeNewEvents();
            Assert.Contains(after, e => e.Kind == "missed" && e.PlayerHpPct < hpBefore);
            Assert.Contains(after, e => e.Kind == "penalty");
        }

        [Fact]
        public void BonusesStopAtTheirCaps()
        {
            var mods = new DungeonMods();
            for (int i = 0; i < 20; i++) DungeonPerks.Apply(mods, "fury");
            Assert.Equal(100, mods.DamagePct);
            for (int i = 0; i < 20; i++) DungeonPerks.Apply(mods, "staggered");
            Assert.Equal(-60, mods.DamagePct);
            var offer = DungeonPerks.Offer(new DungeonMods(), new Random(1));
            Assert.Equal(2, offer.Count);
            Assert.NotEqual(offer[0].Id, offer[1].Id);
            Assert.All(offer, o => Assert.False(o.Penalty));
        }

        [Fact]
        public void TheServerJudgesTheSwipes()
        {
            string[] want = { "Up", "Left" };
            int window = DungeonPerks.WindowMs(5, boss: false);
            Assert.True(DungeonPerks.Judge(want, new[] { "Up", "Left" }, window, 900));
            Assert.True(DungeonPerks.Judge(want, new[] { "up", "left" }, window, 900));
            Assert.False(DungeonPerks.Judge(want, new[] { "Left", "Up" }, window, 900));
            Assert.False(DungeonPerks.Judge(want, new[] { "Up" }, window, 900));
            Assert.False(DungeonPerks.Judge(want, new[] { "Up", "Left" }, window, window + DungeonPerks.LatencySlackMs + 1));
            Assert.False(DungeonPerks.Judge(want, new[] { "Up", "Left" }, window, 50)); // no hand is that fast
            Assert.False(DungeonPerks.Judge(want, null, window, 900));
        }

        [Fact]
        public void TelegraphsGrowLongerAndFasterWithTheStage()
        {
            Assert.Equal(2, DungeonPerks.SequenceLength(1, boss: false));
            Assert.Equal(3, DungeonPerks.SequenceLength(1, boss: true));
            Assert.Equal(5, DungeonPerks.SequenceLength(15, boss: false));
            Assert.Equal(6, DungeonPerks.SequenceLength(15, boss: true));
            Assert.True(DungeonPerks.MsPerSwipe(15) < DungeonPerks.MsPerSwipe(1));
            Assert.All(DungeonPerks.Telegraph(9, boss: false, new Random(2)), d => Assert.Contains(d, DungeonPerks.Directions));
        }

        [Fact]
        public void TheRosterOpensAtStagesSixAndEleven()
        {
            Assert.Equal(1, DungeonEngine.PartySlotsOpen(0));
            Assert.Equal(1, DungeonEngine.PartySlotsOpen(5));
            Assert.Equal(2, DungeonEngine.PartySlotsOpen(6));
            Assert.Equal(2, DungeonEngine.PartySlotsOpen(10));
            Assert.Equal(3, DungeonEngine.PartySlotsOpen(11));
        }

        [Fact]
        public void ASecondChampionFightsBeside()
        {
            var payload = Character(40, 200);
            payload.Slot2_CharacterId = Guid.NewGuid();
            var solo = new DungeonRunner(payload, DungeonKind.Equipment, 3, Fish, 100, new[] { 0 }, challenges: false);
            var pair = new DungeonRunner(payload, DungeonKind.Equipment, 3, Fish, 100, new[] { 0, 1 }, challenges: false);
            solo.Advance();
            pair.Advance();
            Assert.Equal(2, pair.Result!.PartySize);
            Assert.True(pair.Result.Won || !solo.Result!.Won, "a pair should not do worse than one champion");
            if (pair.Result.Won && solo.Result!.Won) Assert.True(pair.Result.Seconds < solo.Result.Seconds);
        }

        [Fact]
        public void AnAbandonedRunEndsWithEveryTelegraphMissed()
        {
            var runner = new DungeonRunner(Character(60, 2000), DungeonKind.Gold, 1, Fish, 100, new[] { 0 }, challenges: true);
            runner.Advance();
            runner.FinishAbandoned(new Random(1));
            Assert.NotNull(runner.Result);
            Assert.Equal(0, runner.Result!.Dodged);
            Assert.True(runner.Result.Missed >= 1);
        }

        [Fact]
        public void ChestsPayDungeonTicketsAtTheirOddsAndTheTopUpKeepsThem()
        {
            var rng = new Random(7);
            foreach (var (tier, permille) in new[] { (LootChestTier.Bronze, 100), (LootChestTier.Silver, 200), (LootChestTier.Gold, 350), (LootChestTier.Ancient, 600) })
            {
                int hits = 0;
                for (int i = 0; i < 20_000; i++) hits += LootChestRules.Roll(tier, 1, 1, 100, 0, rng, dungeonTickets: true).DungeonTickets;
                Assert.InRange(hits / 20.0, permille - 15, permille + 15);
                Assert.Equal(0, LootChestRules.Roll(tier, 1, 1, 100, 0, rng).DungeonTickets);
            }

            var row = new FolkIdle.Server.Models.DungeonProgress { Tickets = 4, TicketDay = 100 };
            for (int i = 0; i < 6; i++) DungeonEngine.AddBonusTicket(row);
            Assert.Equal(DungeonEngine.BonusTicketCap, row.Tickets);
            Assert.False(DungeonEngine.AddBonusTicket(row));
            DungeonEngine.TopUpTickets(row, new DateTime(1970, 1, 1).AddDays(103));
            Assert.Equal(DungeonEngine.BonusTicketCap, row.Tickets); // never down to the cap
            row.Tickets = 1;
            row.TicketDay = 100;
            DungeonEngine.TopUpTickets(row, new DateTime(1970, 1, 1).AddDays(101));
            Assert.Equal(3, row.Tickets);
        }

        [Fact]
        public void LaterOffersHoldACardOfTheLeadingSchoolAndSometimesARiskyOne()
        {
            int risky = 0;
            for (int seed = 0; seed < 400; seed++)
            {
                var rng = new Random(seed);
                var first = DungeonPerks.Offer(new DungeonMods(), rng);
                Assert.Equal(2, first.Count);
                Assert.NotEqual(first[0].School, first[1].School);

                var mods = new DungeonMods();
                Assert.True(DungeonPerks.Apply(mods, "iron"));
                var offer = DungeonPerks.Offer(mods, rng);
                Assert.Equal(2, offer.Count);
                Assert.Equal("Guardian", offer[0].School);
                Assert.NotEqual("Guardian", offer[1].School);
                Assert.StartsWith("Last Stand", offer[0].Unlocks);
                if (offer[1].Risky) risky++;
            }
            Assert.InRange(risky, 60, 140); // about one offer in four
        }

        [Fact]
        public void TwoCardsOfASchoolUnlockItsSet()
        {
            var runner = new DungeonRunner(Character(60, 2000), DungeonKind.Xp, 1, Fish, 100, new[] { 0 }, challenges: true);
            runner.Advance();
            runner.Dodge();
            Assert.True(runner.Choose("fury"));
            Assert.Empty(runner.Mods.Sets);
            runner.Advance();
            runner.Dodge();
            Assert.True(runner.Choose("glass"));
            Assert.Contains(DungeonPerks.Executioner, runner.Mods.Sets);
            Assert.Contains(runner.AllEvents, e => e.Kind == "perk" && e.Text.StartsWith("Executioner"));
            Assert.Equal(85, runner.Mods.DamagePct);
            Assert.Equal(-30, runner.Mods.MaxHpPct);
            Assert.False(DungeonPerks.Apply(runner.Mods, "glass")); // a risky card once a run
        }

        [Fact]
        public void UnshakableTurnsAMissIntoHalfABlowWithoutACurse()
        {
            var runner = new DungeonRunner(Character(40, 300), DungeonKind.Xp, 2, Fish, 100, new[] { 0 }, challenges: true);
            runner.Advance();
            foreach (var id in new[] { "iron", "vigour", "bastion" }) DungeonPerks.Apply(runner.Mods, id);
            DungeonPerks.UnlockSets(runner.Mods);
            Assert.Contains(DungeonPerks.LastStand, runner.Mods.Sets);
            Assert.Contains(DungeonPerks.Unshakable, runner.Mods.Sets);
            Assert.Null(runner.Miss(new Random(3)));
        }

        [Fact]
        public void QuickstepWidensTheWindowAndBloodlustStopsMeals()
        {
            Assert.True(DungeonPerks.WindowMs(10, false, 50) > DungeonPerks.WindowMs(10, false));
            var mods = new DungeonMods();
            Assert.True(DungeonPerks.Apply(mods, "bloodlust"));
            Assert.True(mods.NoMeals);
            Assert.False(DungeonPerks.Apply(mods, "feast"));
            Assert.False(DungeonPerks.Apply(mods, "wind"));
        }
    }
}

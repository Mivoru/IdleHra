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
            // Five waves and the boss's opening; the 2/3 and 1/3 telegraphs only
            // when the boss lives through them - a 2000-attribute hero may not let it.
            Assert.InRange(pauses, 6, 8);
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
            Assert.Equal(15, furious.Mods.DamagePct);
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
            Assert.Equal(75, mods.DamagePct);
            for (int i = 0; i < 20; i++) DungeonPerks.Apply(mods, "staggered");
            Assert.Equal(-45, mods.DamagePct); // the floor is checked before the step: -40 is still open, -45 is not
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
            Assert.Equal(1, DungeonPerks.SequenceLength(1, boss: false));
            Assert.Equal(2, DungeonPerks.SequenceLength(1, boss: true));
            Assert.Equal(4, DungeonPerks.SequenceLength(15, boss: false));
            Assert.Equal(5, DungeonPerks.SequenceLength(15, boss: true));
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
    }
}

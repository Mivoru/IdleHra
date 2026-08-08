using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Who carries into the next season.
    ///
    /// This is the one decision in the game that DELETES something, and it runs
    /// server-side at a moment nobody is watching - so every rule it uses has
    /// to be provable here rather than discovered three months later, when the
    /// evidence has already been culled.
    /// </summary>
    public class HallOfAncestorsTests
    {
        private readonly ITestOutputHelper _output;

        public HallOfAncestorsTests(ITestOutputHelper output) => _output = output;

        private static HallOfAncestorsRules.Member Member(
            string label,
            bool main = false,
            bool kept = false,
            bool epic = false,
            int total = 16,
            int generation = 0)
        {
            // A deterministic id per label, so a failure names somebody.
            var bytes = new byte[16];
            var source = System.Text.Encoding.UTF8.GetBytes(label);
            Array.Copy(source, bytes, Math.Min(source.Length, 16));

            return new HallOfAncestorsRules.Member(new Guid(bytes), main, kept, epic, total, generation);
        }

        // --- the cap ----------------------------------------------------------

        [Theory]
        [InlineData(0, 10)]
        [InlineData(1, 11)]
        [InlineData(4, 14)]
        // Bought more than exist, which the purchase refuses but the cap must
        // survive anyway - a clamp here is cheaper than trusting every caller.
        [InlineData(9, 14)]
        [InlineData(-3, 10)]
        public void TheCapRunsTenToFourteen(int purchased, int expected)
        {
            Assert.Equal(expected, HallOfAncestorsRules.CapFor(purchased));
        }

        /// <summary>
        /// Zero means "all four bought" and callers must read it as REFUSE,
        /// never as free - the same contract InheritanceRegistry.GetUpgradeCost
        /// has, and the same mistake available if anyone forgets it.
        /// </summary>
        [Fact]
        public void TheFourthSlotIsTheLastOneForSale()
        {
            long previous = 0L;
            for (int bought = 0; bought < HallOfAncestorsRules.MaxPurchases; bought++)
            {
                long cost = HallOfAncestorsRules.NextSlotCostDiamonds(bought);
                Assert.True(cost > previous, $"slot {bought + 1} costs {cost}, not more than {previous}");
                previous = cost;
            }

            Assert.Equal(0L, HallOfAncestorsRules.NextSlotCostDiamonds(HallOfAncestorsRules.MaxPurchases));
            Assert.Equal(0L, HallOfAncestorsRules.NextSlotCostDiamonds(99));

            long all = 0L;
            for (int i = 0; i < HallOfAncestorsRules.MaxPurchases; i++) all += HallOfAncestorsRules.NextSlotCostDiamonds(i);
            _output.WriteLine($"all four slots cost {all} diamonds");
        }

        // --- who carries ------------------------------------------------------

        [Fact]
        public void UnderTheCapNobodyIsLetGo()
        {
            var members = new List<HallOfAncestorsRules.Member>
            {
                Member("main", main: true),
                Member("a"),
                Member("b"),
            };

            var survivors = HallOfAncestorsRules.ChooseSurvivors(members, 10);
            Assert.Equal(3, survivors.Count);
        }

        /// <summary>
        /// The mark outranks the numbers. That is the entire reason the flag
        /// exists: a player who keeps a weak child with an epic grandparent, or
        /// simply one they like, must not have that overruled by an aptitude
        /// total.
        /// </summary>
        [Fact]
        public void AMarkedMemberBeatsAStrongerUnmarkedOne()
        {
            var members = new List<HallOfAncestorsRules.Member>
            {
                Member("main", main: true, total: 4),
                Member("marked-but-weak", kept: true, total: 8),
                Member("strong", total: 48),
                Member("stronger", total: 50),
            };

            var survivors = HallOfAncestorsRules.ChooseSurvivors(members, 3);

            Assert.Contains(Member("main", main: true, total: 4).CharacterId, survivors);
            Assert.Contains(Member("marked-but-weak", kept: true, total: 8).CharacterId, survivors);
            Assert.DoesNotContain(Member("strong", total: 48).CharacterId, survivors);
        }

        /// <summary>
        /// With nobody marked the cull must still be sensible: an absent player
        /// keeps their best, which is what they would have chosen.
        /// </summary>
        [Fact]
        public void WithNoMarksTheStrongestBloodCarries()
        {
            var members = new List<HallOfAncestorsRules.Member>
            {
                Member("main", main: true, total: 4),
                Member("weak", total: 10),
                Member("middling", total: 30),
                Member("best", total: 44),
            };

            var survivors = HallOfAncestorsRules.ChooseSurvivors(members, 2);

            Assert.Equal(2, survivors.Count);
            Assert.Contains(Member("main", main: true, total: 4).CharacterId, survivors);
            Assert.Contains(Member("best", total: 44).CharacterId, survivors);
        }

        /// <summary>
        /// THE INVARIANT THAT IS NOT ABOUT BALANCE. The main character's id IS
        /// the account's PlayerGuid: EquipmentSlotEngine resolves an empty
        /// character id to that row and StateCheckpointManager hydrates it as
        /// slot 1. Culling them does not lose a character, it breaks the
        /// account - so it must hold even when the player has marked more
        /// people than they have slots, which is a legal thing to do.
        /// </summary>
        [Fact]
        public void TheMainCharacterSurvivesEvenWhenEverySlotIsMarkedForSomeoneElse()
        {
            var members = new List<HallOfAncestorsRules.Member> { Member("main", main: true, total: 4) };
            for (int i = 0; i < 12; i++)
            {
                members.Add(Member($"favourite-{i}", kept: true, total: 40 + i));
            }

            var survivors = HallOfAncestorsRules.ChooseSurvivors(members, HallOfAncestorsRules.BaseSlots);

            Assert.Equal(HallOfAncestorsRules.BaseSlots, survivors.Count);
            Assert.Contains(Member("main", main: true, total: 4).CharacterId, survivors);
        }

        /// <summary>
        /// A rollover that ran twice on the same data must delete the same
        /// people. Without a total order the tie-break falls to whatever order
        /// the database returned rows in, which is not an order at all.
        /// </summary>
        [Fact]
        public void TheChoiceIsReproducibleWhenEverythingTies()
        {
            var members = new List<HallOfAncestorsRules.Member>();
            for (int i = 0; i < 20; i++) members.Add(Member($"identical-{i}"));

            var first = HallOfAncestorsRules.ChooseSurvivors(members, 10);

            var shuffled = new List<HallOfAncestorsRules.Member>(members);
            shuffled.Reverse();
            var second = HallOfAncestorsRules.ChooseSurvivors(shuffled, 10);

            Assert.Equal(first, second);
        }

        /// <summary>
        /// Epic and generation break ties under the aptitude total, so a child
        /// that cost a 5% roll to produce is not culled in favour of an
        /// identical sibling.
        /// </summary>
        [Fact]
        public void EpicAndTheLaterGenerationBreakTies()
        {
            var members = new List<HallOfAncestorsRules.Member>
            {
                Member("plain", total: 20, generation: 1),
                Member("epic", total: 20, generation: 1, epic: true),
            };

            var survivors = HallOfAncestorsRules.ChooseSurvivors(members, 1);
            Assert.Equal(Member("epic", total: 20, generation: 1, epic: true).CharacterId, survivors.Single());

            var byGeneration = new List<HallOfAncestorsRules.Member>
            {
                Member("older", total: 20, generation: 1),
                Member("newer", total: 20, generation: 4),
            };

            var kept = HallOfAncestorsRules.ChooseSurvivors(byGeneration, 1);
            Assert.Equal(Member("newer", total: 20, generation: 4).CharacterId, kept.Single());
        }
    }
}

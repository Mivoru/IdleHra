using System;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    public class BreedingTraitsTests
    {
        /// <summary>Returns the given values in order (each taken modulo maxValue), then repeats the last.</summary>
        private sealed class SequenceRandom : Random
        {
            private readonly int[] _values;
            private int _index;
            public SequenceRandom(params int[] values) => _values = values;
            public override int Next(int maxValue)
            {
                int value = _values[Math.Min(_index, _values.Length - 1)];
                _index++;
                return maxValue <= 0 ? 0 : value % maxValue;
            }
        }

        [Fact]
        public void MutationScalesWithTheGrounds()
        {
            Assert.Equal(4, BreedingTraits.MutationPercentFor(0));
            Assert.Equal(9, BreedingTraits.MutationPercentFor(5));
            Assert.Equal(14, BreedingTraits.MutationPercentFor(10));
        }

        [Fact]
        public void NewcomerChanceScalesWithTheInnAndStopsAtFifty()
        {
            Assert.Equal(20, BreedingTraits.NewcomerTraitPercentFor(0));
            Assert.Equal(35, BreedingTraits.NewcomerTraitPercentFor(5));
            Assert.Equal(50, BreedingTraits.NewcomerTraitPercentFor(10));
            Assert.Equal(50, BreedingTraits.NewcomerTraitPercentFor(20));
        }

        [Fact]
        public void OneParentPassesHalfTheTimeAndBothNinetyPercent()
        {
            var rng = new Random(20260913);
            long father = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);
            long mother = TraitRegistry.MaskOf(TraitRegistry.HawkEye);
            int iron = 0, hawk = 0;
            const int runs = 20_000;
            for (int i = 0; i < runs; i++)
            {
                long child = BreedingTraits.Inherit(father, mother, isRelated: false, isEpic: false, groundsLevel: 0, rng);
                if (TraitRegistry.Has(child, TraitRegistry.IronBlood)) iron++;
                if (TraitRegistry.Has(child, TraitRegistry.HawkEye)) hawk++;
            }
            Assert.InRange(iron * 100.0 / runs, 47.0, 53.0);
            Assert.InRange(hawk * 100.0 / runs, 87.0, 93.0);
        }

        [Fact]
        public void NoChildEverCarriesMoreThanThree()
        {
            var rng = new Random(7);
            long everything = TraitRegistry.KnownBitsMask;
            for (int i = 0; i < 5_000; i++)
            {
                long child = BreedingTraits.Inherit(everything, everything, isRelated: true, isEpic: true, groundsLevel: 10, rng);
                Assert.InRange(TraitRegistry.CountOf(child), 0, TraitRegistry.MaxTraitsPerCharacter);
            }
        }

        [Fact]
        public void TheCapKeepsFlawsFirstThenTheRarest()
        {
            // Every roll 0: every trait passes, the mutation fires and picks the
            // first Common not already present (Stout Heart).
            var rng = new SequenceRandom(0);
            long father = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.BloodOfKings, TraitRegistry.WolfsHunger, TraitRegistry.FaeTouched);
            long mother = TraitRegistry.MaskOf(TraitRegistry.ThinBlood);

            long child = BreedingTraits.Inherit(father, mother, isRelated: false, isEpic: false, groundsLevel: 0, rng);

            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.ThinBlood, TraitRegistry.BloodOfKings, TraitRegistry.WolfsHunger), child);
        }

        [Fact]
        public void NothingPassesWhenEveryRollFails()
        {
            var rng = new SequenceRandom(99);
            long parents = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);
            Assert.Equal(0L, BreedingTraits.Inherit(parents, parents, isRelated: false, isEpic: false, groundsLevel: 0, rng));
        }

        [Fact]
        public void ARelatedPairCanGiveAFlawAndAnEpicGivesARareOrBetter()
        {
            // 0 = related flaw fires (first flaw: Thin Blood); mutation roll 99 fails;
            // epic rarity roll 0 of 100 -> Rare pool, index 0 -> Iron Blood.
            var rng = new SequenceRandom(0, 0, 99, 0, 0);
            long child = BreedingTraits.Inherit(0L, 0L, isRelated: true, isEpic: true, groundsLevel: 0, rng);
            Assert.True(TraitRegistry.Has(child, TraitRegistry.ThinBlood));
            Assert.True(TraitRegistry.Has(child, TraitRegistry.IronBlood));

            var real = new Random(11);
            for (int i = 0; i < 2_000; i++)
            {
                long epicChild = BreedingTraits.Inherit(0L, 0L, isRelated: false, isEpic: true, groundsLevel: 0, real);
                Assert.Contains(TraitRegistry.BitsOf(epicChild), bit =>
                    TraitRegistry.TryGet(bit, out var def) && def.Rarity is TraitRarity.Rare or TraitRarity.Legendary);
            }
        }

        [Fact]
        public void NewcomersNeverBringALegendaryBelowInnSix()
        {
            var rng = new Random(3);
            for (int i = 0; i < 20_000; i++)
            {
                long mask = BreedingTraits.RollNewcomerTrait(5, rng);
                Assert.InRange(TraitRegistry.CountOf(mask), 0, 1);
                foreach (int bit in TraitRegistry.BitsOf(mask))
                {
                    Assert.True(TraitRegistry.TryGet(bit, out var def));
                    Assert.NotEqual(TraitRarity.Legendary, def.Rarity);
                }
            }
        }

        [Fact]
        public void NewcomerRollsFollowTheInn()
        {
            // has a trait (0 < 35), not a flaw (50 >= 15), rarity 99 at Inn 5 -> Rare, index 0 -> Iron Blood
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.IronBlood), BreedingTraits.RollNewcomerTrait(5, new SequenceRandom(0, 50, 99, 0)));
            // the same rolls at Inn 6 reach the Legendary band -> Blood of Kings
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.BloodOfKings), BreedingTraits.RollNewcomerTrait(6, new SequenceRandom(0, 50, 99, 0)));
            // flaw branch
            Assert.Equal(TraitRegistry.MaskOf(TraitRegistry.ThinBlood), BreedingTraits.RollNewcomerTrait(0, new SequenceRandom(0, 0, 0)));
            // no trait at all
            Assert.Equal(0L, BreedingTraits.RollNewcomerTrait(0, new SequenceRandom(99)));
        }

        [Fact]
        public void PreviewOddsNameEachTraitAndItsSource()
        {
            long hero = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.HawkEye);
            long partner = TraitRegistry.MaskOf(TraitRegistry.HawkEye, TraitRegistry.ThinBlood);

            var odds = BreedingTraits.PreviewOdds(hero, partner).ToDictionary(o => o.Bit);

            Assert.Equal(3, odds.Count);
            Assert.Equal((50, "hero"), (odds[TraitRegistry.IronBlood].ChancePct, odds[TraitRegistry.IronBlood].Source));
            Assert.Equal((90, "both"), (odds[TraitRegistry.HawkEye].ChancePct, odds[TraitRegistry.HawkEye].Source));
            Assert.Equal((50, "partner"), (odds[TraitRegistry.ThinBlood].ChancePct, odds[TraitRegistry.ThinBlood].Source));
            Assert.Equal(60, BreedingTraits.FlawPercentFor(true));
            Assert.Equal(0, BreedingTraits.FlawPercentFor(false));
        }
    }
}

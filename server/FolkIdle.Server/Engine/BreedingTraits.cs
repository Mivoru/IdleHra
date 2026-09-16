using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Engine
{
    public readonly record struct TraitOdds(int Bit, int ChancePct, string Source);

    /// <summary>
    /// How traits pass from parents to a child, and what a newcomer brings.
    /// See docs/superpowers/specs/2026-09-13-breeding-traits-design.md section 4.
    ///
    /// PURE AND STATIC, and every roll comes from the Random passed in - so each
    /// rule is a millisecond test, and a test can script the dice exactly.
    /// </summary>
    public static class BreedingTraits
    {
        public const int SingleParentPassPercent = 50;
        public const int BothParentsPassPercent = 90;
        public const int RelatedFlawPercent = 60;
        public const int BaseMutationPercent = 4;
        public const int MutationPercentPerGroundsLevel = 1;
        public const int NewcomerBasePercent = 20;
        public const int NewcomerPercentPerInnLevel = 3;
        public const int NewcomerCapPercent = 50;
        public const int NewcomerFlawPercent = 15;
        public const int LegendaryInnLevel = 6;

        public static int MutationPercentFor(int groundsLevel)
            => BaseMutationPercent + Math.Max(0, groundsLevel) * MutationPercentPerGroundsLevel;

        public static int NewcomerTraitPercentFor(int innLevel)
            => Math.Min(NewcomerCapPercent, NewcomerBasePercent + Math.Max(0, innLevel) * NewcomerPercentPerInnLevel);

        public static int FlawPercentFor(bool isRelated) => isRelated ? RelatedFlawPercent : 0;

        /// <summary>
        /// A child's traits: inherit, then a related pair's flaw, then a mutation,
        /// then the epic guarantee, then the cap of three.
        /// </summary>
        public static long Inherit(long fatherMask, long motherMask, bool isRelated, bool isEpic, int groundsLevel, Random rng)
        {
            var candidates = new List<(int Bit, bool IsNew)>();

            foreach (int bit in TraitRegistry.BitsOf(fatherMask | motherMask))
            {
                bool both = TraitRegistry.Has(fatherMask, bit) && TraitRegistry.Has(motherMask, bit);
                if (rng.Next(100) < (both ? BothParentsPassPercent : SingleParentPassPercent))
                {
                    candidates.Add((bit, false));
                }
            }

            if (isRelated && rng.Next(100) < RelatedFlawPercent)
            {
                AddRandom(candidates, TraitRegistry.OfRarity(TraitRarity.Flaw), rng);
            }

            if (rng.Next(100) < MutationPercentFor(groundsLevel))
            {
                AddRandom(candidates, PoolByWeight(rng, common: 70, rare: 25, legendary: 5), rng);
            }

            if (isEpic)
            {
                AddRandom(candidates, PoolByWeight(rng, common: 0, rare: 80, legendary: 20), rng);
            }

            return Cap(candidates, rng);
        }

        /// <summary>Zero or one trait for somebody arriving at the Inn.</summary>
        public static long RollNewcomerTrait(int innLevel, Random rng)
        {
            if (rng.Next(100) >= NewcomerTraitPercentFor(innLevel)) return 0L;

            IReadOnlyList<TraitDefinition> pool = rng.Next(100) < NewcomerFlawPercent
                ? TraitRegistry.OfRarity(TraitRarity.Flaw)
                : innLevel >= LegendaryInnLevel
                    ? PoolByWeight(rng, common: 60, rare: 32, legendary: 8)
                    : PoolByWeight(rng, common: 75, rare: 25, legendary: 0);

            return 1L << pool[rng.Next(pool.Count)].Bit;
        }

        /// <summary>What each trait in the pair passes with - exact, not sampled.</summary>
        public static IReadOnlyList<TraitOdds> PreviewOdds(long heroMask, long partnerMask)
        {
            var odds = new List<TraitOdds>();
            foreach (int bit in TraitRegistry.BitsOf(heroMask | partnerMask))
            {
                bool hero = TraitRegistry.Has(heroMask, bit);
                bool partner = TraitRegistry.Has(partnerMask, bit);
                odds.Add(hero && partner
                    ? new TraitOdds(bit, BothParentsPassPercent, "both")
                    : new TraitOdds(bit, SingleParentPassPercent, hero ? "hero" : "partner"));
            }
            return odds;
        }

        private static IReadOnlyList<TraitDefinition> PoolByWeight(Random rng, int common, int rare, int legendary)
        {
            int roll = rng.Next(common + rare + legendary);
            TraitRarity rarity = roll < common ? TraitRarity.Common
                : roll < common + rare ? TraitRarity.Rare
                : TraitRarity.Legendary;
            return TraitRegistry.OfRarity(rarity);
        }

        private static void AddRandom(List<(int Bit, bool IsNew)> candidates, IReadOnlyList<TraitDefinition> pool, Random rng)
        {
            var open = pool.Where(t => !candidates.Any(c => c.Bit == t.Bit)).ToList();
            if (open.Count == 0) return;
            candidates.Add((open[rng.Next(open.Count)].Bit, true));
        }

        /// <summary>
        /// Flaws always stay - the way out of a flaw is breeding it out, never the
        /// cap. Then rarity; a NEW trait only displaces an inherited one it
        /// outranks, and inherited ties are a coin flip.
        /// </summary>
        private static long Cap(List<(int Bit, bool IsNew)> candidates, Random rng)
        {
            long mask = 0L;
            int count = 0;

            // Modul: every Flaw candidate is added before the cap check below
            // even runs, with no cap of its own on this loop. Safe only because
            // TraitRegistry.OfRarity(Flaw).Count == TraitRegistry.MaxTraitsPerCharacter
            // (3 == 3) - a child can never actually be offered more flaw
            // candidates than the cap allows, since there are only three flaws
            // in existence. Adding a fourth Flaw trait to the registry would
            // silently break that; TraitRegistryTests/NoChildEverCarriesMoreThanThree
            // would catch the resulting overshoot, but this loop would not.
            foreach (var c in candidates.Where(c => TraitRegistry.IsFlaw(c.Bit)))
            {
                mask |= 1L << c.Bit;
                count++;
            }

            var ranked = candidates
                .Where(c => !TraitRegistry.IsFlaw(c.Bit))
                .Select(c =>
                {
                    TraitRegistry.TryGet(c.Bit, out var def);
                    int key = (int)def.Rarity * 4 + (c.IsNew ? 0 : 2) + rng.Next(2);
                    return (c.Bit, Key: key);
                })
                .ToList()
                .OrderByDescending(c => c.Key);

            foreach (var c in ranked)
            {
                if (count >= TraitRegistry.MaxTraitsPerCharacter) break;
                mask |= 1L << c.Bit;
                count++;
            }

            return mask;
        }
    }
}

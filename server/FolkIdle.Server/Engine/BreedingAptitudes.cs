using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What a child inherits, and what husbandry is worth.
    ///
    /// Four values per lineage member - Strength (combat), Skill (gathering and
    /// crafting), Endurance (health and armour), Fortune (luck) - and the rules
    /// that carry them from one generation to the next.
    ///
    /// See docs/architecture/LONG_GAME_SPEC.md part 3. In short: level and gear
    /// are the only axes this game has and the rollover wipes both, so a season
    /// leaves nothing behind. Aptitudes are the thing that survives, which is
    /// why they climb slowly and cap far away.
    ///
    /// PURE AND STATIC on purpose. Every rule below is "given two parents and a
    /// die, what comes out", which needs no database and no session - and the
    /// interesting failures here are all arithmetic, so they belong in a test
    /// that can run in a millisecond rather than behind a Testcontainer.
    /// </summary>
    public static class BreedingAptitudes
    {
        public const int Count = 4;

        public const int Strength = 0;
        public const int Skill = 1;
        public const int Endurance = 2;
        public const int Fortune = 3;

        /// <summary>
        /// The absolute ceiling. Reaching it in even one aptitude is meant to
        /// take on the order of ten seasons of deliberate selection - a cap
        /// should be an asymptote almost nobody touches, not a checklist.
        /// </summary>
        public const int MaxValue = 50;

        /// <summary>
        /// What a brand-new, unbred character starts with. Low enough that the
        /// first few crossings feel like real progress.
        /// </summary>
        public const int StartingValue = 4;

        /// <summary>
        /// Villagers roll in a band set by the Inn, and never above this.
        ///
        /// THE TWO-PHASE CLIMB, and the reason the village matters at all:
        /// 0 to 20 is village-driven - build the Inn, better people arrive,
        /// marry good blood in. 20 to 50 the village cannot reach, so it is
        /// mutation and selection across generations only. That second phase is
        /// the veteran axis; the first is what makes rebuilding the village
        /// each season worth doing.
        /// </summary>
        public const int VillagerCeiling = 20;

        public static string NameOf(int aptitude) => aptitude switch
        {
            Strength => "Strength",
            Skill => "Skill",
            Endurance => "Endurance",
            Fortune => "Fortune",
            _ => "Unknown",
        };

        // --- what a point is worth ------------------------------------------

        // Diminishing, in three bands. Flat 1.5% to a cap of 50 would be +75%
        // in one domain - a veteran roughly twice a newcomer's strength in
        // everything, on a shared seasonal leaderboard. That would make the
        // board a function of account age rather than of how the season was
        // played, which is the exact failure seasons exist to prevent.
        //
        // The bands land at +30% / +40.5% / +45%. Visible, not decisive.
        private const int BandOneEnd = 20;
        private const int BandTwoEnd = 35;
        private const float BandOnePerPoint = 1.5f;
        private const float BandTwoPerPoint = 0.7f;
        private const float BandThreePerPoint = 0.3f;

        /// <summary>
        /// The percentage bonus a given number of points is worth, in its own
        /// domain. Monotonic and clamped - a lineage can never be punished for
        /// gaining a point, and never rewarded past the cap.
        /// </summary>
        public static float BonusPercentFor(int points)
        {
            if (points <= 0) return 0f;
            if (points > MaxValue) points = MaxValue;

            float total = Math.Min(points, BandOneEnd) * BandOnePerPoint;
            if (points > BandOneEnd)
            {
                total += (Math.Min(points, BandTwoEnd) - BandOneEnd) * BandTwoPerPoint;
            }
            if (points > BandTwoEnd)
            {
                total += (points - BandTwoEnd) * BandThreePerPoint;
            }
            return total;
        }

        // --- inheritance ------------------------------------------------------

        /// <summary>
        /// Which parent a single aptitude comes from, weighted by how strong
        /// that parent is in it.
        ///
        ///     P(father) = father / (father + mother)
        ///
        /// A father at 12 against a mother at 4 gives a 75% chance of the 12.
        ///
        /// THE PROPERTY THIS CREATES IS THE WHOLE DESIGN. Cross a fighter
        /// (12,4,4,4) with a gatherer (4,12,4,4) and each aptitude independently
        /// favours whichever parent is better at it, so the child comes out
        /// around (12,12,4,4) - good at both. So the strategy discovers itself
        /// and it is real husbandry: you do not want two similar parents, you
        /// want two different ones.
        ///
        /// It also means a child can never EXCEED the best value already in the
        /// pair, which is why mutation below is not optional - without it a
        /// bloodline freezes after two generations, permanently.
        /// </summary>
        public static int InheritOne(int father, int mother, Random rng)
        {
            father = Math.Clamp(father, 0, MaxValue);
            mother = Math.Clamp(mother, 0, MaxValue);

            int total = father + mother;
            // Two zeroes carry no information to weight by; a coin flip on two
            // identical values is the same answer either way.
            if (total <= 0) return 0;

            return rng.Next(total) < father ? father : mother;
        }

        // --- mutation ---------------------------------------------------------

        /// <summary>Chance of +1 on an ordinary pairing.</summary>
        public const int MutationUpPercent = 25;

        /// <summary>Chance of -1 on an ordinary pairing.</summary>
        public const int MutationDownPercent = 10;

        /// <summary>
        /// Applies the post-inheritance mutation roll.
        ///
        /// INBREEDING INVERTS IT rather than forbidding the pairing. Up to
        /// fourteen lineage members carry across a rollover, so nothing
        /// structural stops a player crossing their own children forever and
        /// never touching the village again - which would make the entire gene
        /// pool pointless. Inverted, a related pairing still works when it is
        /// convenient, but no strategy can be built on it, and fresh village
        /// blood stays valuable permanently.
        /// </summary>
        public static int Mutate(int value, bool isInbred, Random rng, int groundsLevel = 0)
        {
            // Modul: the Breeding Grounds raises the UP chance only, and an
            // inbred pairing swaps the two - so building the Grounds makes a
            // related pairing worse, not better. Without that a player could
            // build their way out of ever needing the village, which is the one
            // strategy the inversion exists to prevent.
            int upPercent = UpMutationPercentFor(groundsLevel);

            int up = isInbred ? MutationDownPercent : upPercent;
            int down = isInbred ? upPercent : MutationDownPercent;

            int roll = rng.Next(100);
            if (roll < up) value++;
            else if (roll < up + down) value--;

            return Math.Clamp(value, 0, MaxValue);
        }

        /// <summary>
        /// The chance of a +1, given the Breeding Grounds level.
        ///
        /// The building used to do NOTHING above level 1 - it was read in four
        /// places and every one tested `&lt;= 0`. This is half of its job; the
        /// other half is selection, below.
        /// </summary>
        public static int UpMutationPercentFor(int groundsLevel)
            => MutationUpPercent + Math.Max(0, groundsLevel);

        /// <summary>Epic mutation chance, as a percentage.</summary>
        public const int EpicChancePercent = 5;

        /// <summary>The same for a related pairing - all but gone.</summary>
        public const int EpicChancePercentInbred = 1;

        public static bool RollEpic(bool isInbred, Random rng)
            => rng.Next(100) < (isInbred ? EpicChancePercentInbred : EpicChancePercent);

        /// <summary>What an epic mutation adds to every aptitude.</summary>
        public const int EpicBonus = 1;

        // --- the whole child ---------------------------------------------------

        /// <summary>
        /// Breeds a full aptitude vector: inherit each value from one parent,
        /// mutate it, then add the epic bonus if one was rolled.
        ///
        /// The caller supplies <paramref name="isEpic"/> rather than this
        /// rolling it, because the same flag is stored on the lineage row and
        /// rolling it twice would let the record and the stats disagree.
        /// </summary>
        public static int[] Breed(
            int[] father,
            int[] mother,
            bool isInbred,
            bool isEpic,
            int selectionMask,
            int groundsLevel,
            Random rng)
        {
            if (father is null || father.Length < Count) throw new ArgumentException("father vector", nameof(father));
            if (mother is null || mother.Length < Count) throw new ArgumentException("mother vector", nameof(mother));

            // Trimmed to what the building actually permits, never refused. The
            // count is a server truth and the client's copy of the table is a
            // hint for drawing checkboxes; a hint that has drifted must not cost
            // a player their gold.
            int selection = ClampSelection(selectionMask, groundsLevel);

            var child = new int[Count];
            for (int i = 0; i < Count; i++)
            {
                bool isSelected = (selection & (1 << i)) != 0;

                // A SELECTED aptitude takes the better parent outright. The
                // weighted coin is what made the climb feel like a slot machine:
                // a 4 against a villager's 6 takes the 6 only 60% of the time,
                // so a bloodline regularly lost ground on the exact stat the
                // player was trying to raise, and had no way to say which one
                // that was.
                int inherited = isSelected
                    ? Math.Max(father[i], mother[i])
                    : InheritOne(father[i], mother[i], rng);

                child[i] = Mutate(inherited, isInbred, rng, groundsLevel);
                if (isEpic) child[i] = Math.Min(MaxValue, child[i] + EpicBonus);
            }
            return child;
        }

        // --- selection ---------------------------------------------------------

        /// <summary>
        /// How many aptitudes the Breeding Grounds lets the player choose.
        ///
        /// NEVER ALL FOUR. Selecting every aptitude would delete inheritance
        /// from the game and replace it with "take the max of both parents",
        /// which makes the choice of partner - the entire point of the village -
        /// irrelevant. Three is the most the maxed building can buy.
        /// </summary>
        public static int SelectableCount(int groundsLevel)
        {
            if (groundsLevel >= 10) return 3;
            if (groundsLevel >= 7) return 2;
            if (groundsLevel >= 4) return 1;
            return 0;
        }

        /// <summary>
        /// Trims a requested selection to what the Grounds permits, keeping the
        /// lowest set bits, and drops anything that is not one of the four
        /// aptitudes - the mask arrives over the wire as a uint and bits above
        /// three are not aptitudes.
        /// </summary>
        public static int ClampSelection(int selectionMask, int groundsLevel)
        {
            int allowed = SelectableCount(groundsLevel);
            if (allowed <= 0) return 0;

            int kept = 0;
            int taken = 0;
            for (int i = 0; i < Count && taken < allowed; i++)
            {
                if ((selectionMask & (1 << i)) == 0) continue;
                kept |= 1 << i;
                taken++;
            }
            return kept;
        }

        /// <summary>A fresh, unbred character's vector.</summary>
        public static int[] Starting()
        {
            var v = new int[Count];
            for (int i = 0; i < Count; i++) v[i] = StartingValue;
            return v;
        }

        /// <summary>
        /// A villager's, rolled against the Inn.
        ///
        /// `2 + random(0..innLevel * 3/2)`, never above the villager ceiling.
        ///
        /// THE MULTIPLIER IS NOT DECORATION. This used to be `rand(0..innLevel)`
        /// and the Inn cannot exceed level 12 - the Town Hall maxes at 5 and
        /// every other building is capped at 2 + TownHallLevel*2 - so villagers
        /// could never roll above 14 and the VillagerCeiling of 20 just below
        /// was a number the game was incapable of producing. The comment on it
        /// promised "0 to 20 is village-driven"; five sixths of that was true.
        ///
        /// Measured against the report that found this: at Inn 5 the old roll
        /// gave 2-7, and the reporting player's ten newcomers held a best
        /// aptitude of exactly 6. He was reading the game correctly when he
        /// said his line could not climb - it could not.
        /// </summary>
        public static int[] RollVillager(int innLevel, Random rng)
        {
            int reach = Math.Max(0, innLevel) * 3 / 2;
            var v = new int[Count];
            for (int i = 0; i < Count; i++)
            {
                v[i] = Math.Min(VillagerCeiling, 2 + rng.Next(reach + 1));
            }
            return v;
        }

        // --- what the player is choosing between --------------------------------

        /// <summary>
        /// The band a single aptitude can land in, given two parents.
        ///
        /// EXACT, not sampled. InheritOne returns one of the two parent values
        /// and Mutate then moves it by at most one in either direction, so the
        /// reachable set is bounded by min(a,b)-1 and max(a,b)+1 - clamped, and
        /// with the two-zeroes case falling out of the same arithmetic.
        ///
        /// This exists because choosing WHICH villager to marry is the whole of
        /// the gene pool as a game, and that decision is unmakeable without a
        /// number. The gene-locus preview answers a different and much less
        /// interesting question.
        ///
        /// The epic roll's +1 is deliberately NOT folded in: it fires 5% of the
        /// time and widening every band by one to describe it would make the
        /// common case a lie.
        /// </summary>
        public static void PreviewOne(int parentA, int parentB, out int min, out int max)
        {
            parentA = Math.Clamp(parentA, 0, MaxValue);
            parentB = Math.Clamp(parentB, 0, MaxValue);

            min = Math.Clamp(Math.Min(parentA, parentB) - 1, 0, MaxValue);
            max = Math.Clamp(Math.Max(parentA, parentB) + 1, 0, MaxValue);
        }

        // --- relatedness -------------------------------------------------------

        /// <summary>
        /// Whether two candidates are close enough to count as inbreeding:
        /// they share a parent, or one is the other's parent, or they share a
        /// grandparent.
        ///
        /// Two levels and no further. A deeper walk would need the whole
        /// pedigree loaded per pairing, and at fourteen roster slots almost
        /// every pair is related somewhere if you look far enough - which would
        /// make the penalty universal and therefore meaningless.
        /// </summary>
        public static bool AreRelated(
            Guid aId, Guid? aFather, Guid? aMother,
            Guid bId, Guid? bFather, Guid? bMother,
            Guid[]? aGrandparents = null,
            Guid[]? bGrandparents = null)
        {
            // Parent and child.
            if (aFather == bId || aMother == bId) return true;
            if (bFather == aId || bMother == aId) return true;

            // Full or half siblings.
            if (SharesA(aFather, bFather, bMother)) return true;
            if (SharesA(aMother, bFather, bMother)) return true;

            // A shared grandparent - cousins, and an aunt or uncle pairing.
            if (aGrandparents is not null && bGrandparents is not null)
            {
                foreach (Guid g in aGrandparents)
                {
                    if (g == Guid.Empty) continue;
                    foreach (Guid h in bGrandparents)
                    {
                        if (g == h) return true;
                    }
                }
            }

            return false;
        }

        private static bool SharesA(Guid? candidate, Guid? otherFather, Guid? otherMother)
        {
            if (candidate is null || candidate == Guid.Empty) return false;
            return candidate == otherFather || candidate == otherMother;
        }
    }
}

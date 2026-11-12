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
        /// The absolute ceiling. Reachable only at the last rebirth - see
        /// CeilingFor - so it is the long goal of the whole bloodline, not a
        /// checklist item.
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

        // --- the bloodline ceiling ---------------------------------------------

        /// <summary>
        /// The highest value breeding can produce for an account that has taken
        /// this many rebirths: the village ceiling at none, rising to MaxValue
        /// at RebirthRules.BloodlineRebirths.
        ///
        /// Modul: THE REBIRTH IS THE CLOCK, NOT THE PLAYTIME (owner 2026-10-14:
        /// "50 should take about twenty rebirths"). A generation costs an hour
        /// of rest and an hour of growing up, and nothing limits how many a run
        /// can hold, so tuning the drift alone made the climb a function of
        /// hours played - measured at 77-119 generations from 25 to 50, which a
        /// dedicated player could do in a single long run. The ceiling makes
        /// each rebirth the thing that opens the next few points, and the drift
        /// (+0.21-0.33 a generation) only decides how quickly a run fills them.
        ///
        /// Derived from the three constants rather than written as 1.5, so the
        /// two ends cannot drift apart from the numbers they name.
        /// </summary>
        public static int CeilingFor(int rebirthCount)
        {
            int rebirths = Math.Clamp(rebirthCount, 0, RebirthRules.BloodlineRebirths);
            int span = MaxValue - VillagerCeiling;
            return VillagerCeiling + span * rebirths / RebirthRules.BloodlineRebirths;
        }

        /// <summary>
        /// What a child may reach in one aptitude: the account's ceiling, or the
        /// better parent's value if a parent is already above it.
        ///
        /// Modul: GRANDFATHERED, NEVER TAKEN AWAY. Lines bred before the ceiling
        /// existed can sit above it. A child may still inherit that number - a
        /// rule that lowered a bloodline would punish the players who invested
        /// most - it just cannot climb past it until the ceiling catches up.
        /// </summary>
        public static int ChildLimit(int father, int mother, int ceiling)
            => Math.Clamp(Math.Max(ceiling, Math.Max(father, mother)), 0, MaxValue);

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
        public static int Mutate(int value, bool isInbred, Random rng, int groundsLevel = 0, int upBonusPercent = 0)
        {
            // Modul: the Breeding Grounds raises the UP chance only, and an
            // inbred pairing swaps the two - so building the Grounds makes a
            // related pairing worse, not better. Without that a player could
            // build their way out of ever needing the village, which is the one
            // strategy the inversion exists to prevent.
            // Modul: Strong Blood (Hearth) raises the UP chance the way the
            // Grounds do, and so it is inverted by an inbred pairing too - the
            // node must not become the way around the village.
            int upPercent = UpMutationPercentFor(groundsLevel) + Math.Max(0, upBonusPercent);

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

        /// <summary>
        /// Bloodline Memory (Hearth keystone): each aptitude's chance of +1.
        /// </summary>
        /// <remarks>
        /// Modul: MEASURED, NOT THE DESIGN'S +10% (owner, 2026-10-30). 40,000
        /// generations at Grounds 12 on the selected aptitude: today +0.33 a
        /// generation (about 92 generations from 20 to 50); "+10% of the parents'
        /// average" was +2.3 to +4.3 (ten generations, breeding solved); a flat +1
        /// was +1.32; a 25% chance of +1 is +0.57, about 1.75x - a strong keystone
        /// that still leaves a climb. The bloodline ceiling caps it like
        /// everything else.
        /// </remarks>
        public const int BloodlineMemoryChancePercent = 25;

        // --- growth (lineage overhaul, 2026-11-13) ------------------------------

        /// <summary>What a SELECTED aptitude always gains, below the limit.</summary>
        public const int GuaranteedGrowth = 1;

        /// <summary>
        /// The distance below the limit at which growth speeds up: a selected
        /// aptitude gains one more point, an unselected one gets a
        /// CatchUpPercent chance of one.
        /// </summary>
        public const int CatchUpGap = 10;

        /// <summary>An unselected aptitude's chance of +1 while CatchUpGap or more below the limit.</summary>
        public const int CatchUpPercent = 50;

        /// <summary>
        /// The points a selected aptitude is sure to gain over the better
        /// parent, given the child's limit. Zero for a related pair.
        /// </summary>
        /// <remarks>
        /// Modul: A SELECTED APTITUDE ALWAYS GROWS (owner brief 2026-11-13:
        /// "50 50 50 50 is almost impossible"). Measured on production: both
        /// 20-rebirth accounts had a ceiling of 50 and a best aptitude of 21-23,
        /// because past the village the climb was +0.2-0.33 a generation - a
        /// 25% coin for +1 against a 10% coin for -1, on the very stat the
        /// player had asked to raise. The ceiling (rebirths) was meant to be
        /// the clock; the dice had become it. Now a selected aptitude gains
        /// +1, or +2 while it is CatchUpGap or more below the limit, and never
        /// falls. Python model, Grounds 12, ceiling 50, from 20: 16-25
        /// generations to 50/50/50/50 with three selected (was 77-119 for one
        /// stat). See docs/design/LINEAGE_REBIRTH_OVERHAUL.md section 2.2.
        /// </remarks>
        public static int SureGrowth(int better, int limit, bool isRelated)
        {
            if (isRelated) return 0;
            int gap = limit - better;
            if (gap <= 0) return 0;
            int growth = GuaranteedGrowth + (gap >= CatchUpGap ? 1 : 0);
            return Math.Min(growth, gap);
        }

        // --- the whole child ---------------------------------------------------

        /// <summary>
        /// Breeds a full aptitude vector: inherit each value from one parent,
        /// grow and mutate it, then add the epic bonus if one was rolled.
        ///
        /// A SELECTED aptitude takes the better parent and gains SureGrowth,
        /// then only a luck roll upward. An unselected one is inherited by the
        /// weighted coin and mutated both ways, with a catch-up chance while
        /// far below the limit. A related pair gets no sure growth and no
        /// catch-up, and its luck is reversed (Mutate).
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
            Random rng,
            int ceiling = MaxValue,
            int upBonusPercent = 0,
            bool bloodlineMemory = false)
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
                int limit = ChildLimit(father[i], mother[i], ceiling);
                int value;
                if (isSelected && !isInbred)
                {
                    int better = Math.Max(father[i], mother[i]);
                    value = better + SureGrowth(better, limit, isInbred);
                    if (rng.Next(100) < UpMutationPercentFor(groundsLevel) + Math.Max(0, upBonusPercent)) value++;
                }
                else
                {
                    int inherited = isSelected
                        ? Math.Max(father[i], mother[i])
                        : InheritOne(father[i], mother[i], rng);
                    value = Mutate(inherited, isInbred, rng, groundsLevel, upBonusPercent);
                    if (!isInbred && limit - inherited >= CatchUpGap && rng.Next(100) < CatchUpPercent) value++;
                }

                child[i] = value;
                if (isEpic) child[i] = child[i] + EpicBonus;
                if (bloodlineMemory && rng.Next(100) < BloodlineMemoryChancePercent) child[i] = child[i] + 1;
                child[i] = Math.Clamp(child[i], 0, limit);
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
        /// <remarks>
        /// Modul: ONE FROM THE FIRST LEVEL (2026-11-13). It was 1/2/3 at
        /// Grounds 4/7/10, so a new Grounds bred by the weighted coin alone and
        /// the rule a player most needs - "pick what to raise" - was hidden
        /// behind three upgrades. Now 1/2/3 at 1/4/7.
        /// </remarks>
        public static int SelectableCount(int groundsLevel)
        {
            if (groundsLevel >= 7) return 3;
            if (groundsLevel >= 4) return 2;
            if (groundsLevel >= 1) return 1;
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
        public static void PreviewOne(int parentA, int parentB, out int min, out int max, int ceiling = MaxValue, bool isRelated = false)
        {
            parentA = Math.Clamp(parentA, 0, MaxValue);
            parentB = Math.Clamp(parentB, 0, MaxValue);
            int limit = ChildLimit(parentA, parentB, ceiling);
            int better = Math.Max(parentA, parentB);

            min = Math.Clamp(Math.Min(parentA, parentB) - 1, 0, MaxValue);
            // The catch-up chance can add one more to the better parent's value
            // while it is far below the limit (never for a related pair).
            int catchUp = !isRelated && limit - better >= CatchUpGap ? 1 : 0;
            max = Math.Min(better + 1 + catchUp, limit);
            min = Math.Min(min, max);
        }

        /// <summary>
        /// The band of a SELECTED aptitude: the better parent plus SureGrowth,
        /// and one more from the luck roll. A related pair has no sure growth
        /// and its luck runs both ways (reversed), so its band is better +/- 1.
        /// Exact, like PreviewOne; the epic and Bloodline Memory extras are not
        /// folded in.
        /// </summary>
        public static void PreviewSelected(int parentA, int parentB, out int min, out int max, int ceiling = MaxValue, bool isRelated = false)
        {
            parentA = Math.Clamp(parentA, 0, MaxValue);
            parentB = Math.Clamp(parentB, 0, MaxValue);
            int limit = ChildLimit(parentA, parentB, ceiling);
            int better = Math.Max(parentA, parentB);

            if (isRelated)
            {
                min = Math.Clamp(better - 1, 0, limit);
                max = Math.Min(better + 1, limit);
                return;
            }

            min = Math.Min(better + SureGrowth(better, limit, false), limit);
            max = Math.Min(min + 1, limit);
        }

        // --- relatedness -------------------------------------------------------

        /// <summary>
        /// Whether two candidates are close enough to count as inbreeding:
        /// anybody within two generations appears on both sides.
        ///
        /// Each side is the person, their parents and their grandparents, and
        /// the pair is related when the two sets overlap. That one rule covers
        /// parent and child, grandparent and grandchild, full and half
        /// siblings, an aunt or uncle with a niece or nephew, and cousins.
        ///
        /// Modul: THE OLD VERSION CLAIMED THE AUNT/UNCLE CASE AND MISSED IT. It
        /// compared grandparents only with grandparents - but an uncle's
        /// PARENTS are his niece's GRANDPARENTS, so the two lists never share an
        /// id and the pair read as strangers. A grandparent and grandchild slipped
        /// through the same gap. And nothing called this at all: the engine and
        /// the preview each carried an inline copy that stopped at siblings, so
        /// cousins bred at full mutation odds. See BreedingRelatedness.
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
            var aSide = Kin(aId, aFather, aMother, aGrandparents);
            var bSide = Kin(bId, bFather, bMother, bGrandparents);
            return aSide.Overlaps(bSide);
        }

        private static System.Collections.Generic.HashSet<Guid> Kin(Guid self, Guid? father, Guid? mother, Guid[]? grandparents)
        {
            var kin = new System.Collections.Generic.HashSet<Guid>();

            // An empty id is an unknown slot in the pedigree, never an ancestor
            // two strangers happen to share.
            void Add(Guid? id)
            {
                if (id is { } value && value != Guid.Empty) kin.Add(value);
            }

            Add(self);
            Add(father);
            Add(mother);
            if (grandparents is not null)
            {
                foreach (Guid g in grandparents) Add(g);
            }
            return kin;
        }
    }
}

using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// How many ancestors carry across a rollover, and who they are.
    ///
    /// THE ROSTER IS THE ONLY THING A SEASON LEAVES ALIVE. Levels, gear, gold
    /// and the village all reset; what survives is a handful of people and the
    /// aptitudes bred into them. A cap is what makes that a choice rather than
    /// an archive - without one, ninety days of breeding accumulates every
    /// child ever born and the last week of a season is worth exactly as much
    /// as the first.
    ///
    /// Ten base, one more per purchase, hard cap fourteen. Four purchases, not
    /// twenty: this is a sink for a currency, not a way to buy out the decision.
    ///
    /// PURE AND STATIC, like VillagerArrivalRules and BreedingAptitudes beside
    /// it. Every rule here is arithmetic over a list, and the interesting
    /// failure - culling the wrong person - is exactly the kind that must be
    /// provable in a millisecond rather than discovered at a rollover three
    /// months later, when the evidence is gone.
    /// </summary>
    public static class HallOfAncestorsRules
    {
        public const int BaseSlots = 10;
        public const int MaxSlots = 14;
        public const int MaxPurchases = MaxSlots - BaseSlots;

        /// <summary>Slots this player has, given how many they have bought.</summary>
        public static int CapFor(int purchasedSlots)
            => Math.Clamp(BaseSlots + Math.Max(0, purchasedSlots), BaseSlots, MaxSlots);

        // 250 diamonds, doubling. Four slots run to 3,750 - the same order as a
        // deep inheritance stat (~6,000 for twenty levels), which is the
        // comparison a player actually makes when deciding what to spend on.
        private const long FirstSlotCostDiamonds = 250L;

        /// <summary>
        /// Diamonds for the NEXT slot, or zero when all four are bought - which
        /// callers must read as "refuse", never as "free". Same contract as
        /// InheritanceRegistry.GetUpgradeCost, deliberately.
        /// </summary>
        public static long NextSlotCostDiamonds(int purchasedSlots)
        {
            if (purchasedSlots < 0) purchasedSlots = 0;
            if (purchasedSlots >= MaxPurchases) return 0L;

            return FirstSlotCostDiamonds << purchasedSlots;
        }

        /// <summary>
        /// One member of the Hall, reduced to what the choice depends on.
        ///
        /// A record rather than the EF entity so the selection can be tested
        /// without a database - the rule is "who carries", and that question has
        /// nothing to do with storage.
        /// </summary>
        public readonly record struct Member(
            Guid CharacterId,
            bool IsMainCharacter,
            bool IsKept,
            bool IsEpicMutation,
            int AptitudeTotal,
            int GenerationIndex);

        /// <summary>
        /// Who survives the rollover, best first.
        ///
        /// The order, and every part of it is load-bearing:
        ///
        /// 1. **The main character, always.** Their id IS the account's
        ///    PlayerGuid - EquipmentSlotEngine resolves an empty character id to
        ///    that row and StateCheckpointManager hydrates it as slot 1 - so
        ///    culling them does not lose a character, it breaks the account.
        ///    They occupy a slot like anyone else; they simply cannot be the one
        ///    let go.
        /// 2. **Whoever the player MARKED.** This is the decision the cap exists
        ///    to create, so nothing outranks it but the invariant above.
        /// 3. **Then the strongest blood** - aptitude total, then epic, then the
        ///    later generation.
        ///
        /// A DEFAULT RATHER THAN A PROMPT, because a rollover runs server-side
        /// with every client disconnected: there is nobody to ask. A player who
        /// marks nobody keeps their best, which is what they would have chosen;
        /// a player who marks somebody keeps them even if the numbers disagree,
        /// which is the whole point of being asked.
        ///
        /// MORE MARKED THAN SLOTS is resolved by the same ranking rather than
        /// refused. Marks are set during a season and the cap can only be
        /// discovered at the end of one, so "you marked twelve for ten slots" is
        /// a sentence with no moment to say it in.
        /// </summary>
        public static List<Guid> ChooseSurvivors(IReadOnlyList<Member> members, int cap)
        {
            var survivors = new List<Guid>(Math.Min(cap, members?.Count ?? 0));
            if (members is null || members.Count == 0) return survivors;
            if (cap <= 0) cap = 1;

            var ranked = new List<Member>(members);
            ranked.Sort(Compare);

            for (int i = 0; i < ranked.Count && survivors.Count < cap; i++)
            {
                survivors.Add(ranked[i].CharacterId);
            }

            // The main character can outrank the cap but never fall outside it.
            // Reached only when a player has bought no slots and marked ten
            // others, which is a legal thing to do and must not cost them their
            // account.
            for (int i = 0; i < ranked.Count; i++)
            {
                if (!ranked[i].IsMainCharacter) continue;
                if (survivors.Contains(ranked[i].CharacterId)) break;

                survivors[survivors.Count - 1] = ranked[i].CharacterId;
                break;
            }

            return survivors;
        }

        private static int Compare(Member a, Member b)
        {
            if (a.IsMainCharacter != b.IsMainCharacter) return a.IsMainCharacter ? -1 : 1;
            if (a.IsKept != b.IsKept) return a.IsKept ? -1 : 1;
            if (a.AptitudeTotal != b.AptitudeTotal) return b.AptitudeTotal - a.AptitudeTotal;
            if (a.IsEpicMutation != b.IsEpicMutation) return a.IsEpicMutation ? -1 : 1;
            if (a.GenerationIndex != b.GenerationIndex) return b.GenerationIndex - a.GenerationIndex;

            // A total order, so a rollover is reproducible and a test that
            // passes today cannot fail tomorrow on the same data.
            return a.CharacterId.CompareTo(b.CharacterId);
        }
    }
}

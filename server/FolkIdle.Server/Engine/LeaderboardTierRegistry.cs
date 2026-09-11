using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What a rank on the global board is WORTH, and what it is CALLED.
    ///
    /// Modul: ONE TABLE, because a rank tier is three things at once - a name,
    /// a colour and a weekly payout - and this codebase's dominant bug class is
    /// two copies of one truth drifting apart. The client mirrors this table
    /// for the colours and `serverMirrors.test.ts` compares the two element by
    /// element, the same guard that caught KNOWN_AFFIX_IDS after ten of its
    /// twelve entries had silently drifted.
    ///
    /// WHY DIAMONDS, AND WHY THESE AMOUNTS.
    ///
    /// The Delve is the game's calibrated diamond tap and it is capped at
    /// <see cref="DelveRegistry.MaxDiamondsPerWeek"/> = 60 a week, deliberately,
    /// so it cannot become the economy. Store packs run 500 / 1,100 / 2,400 /
    /// 5,200. So the whole ladder below is sized so that FIRST PLACE for a
    /// whole week is worth about one good week of Delving, and everything under
    /// it falls away fast. A leaderboard that out-earned the Delve would make
    /// the Delve pointless; one that out-earned the store would make the store
    /// pointless.
    ///
    /// THE POPULATION FLOOR IS THE LOAD-BEARING PART.
    ///
    /// "Top 100" is not an achievement when there are 30 players - it is
    /// everybody. Measured on the live database the day this was written: 29
    /// registered accounts, ONE of them above level 5, and the board itself
    /// carried throwaway accounts named `exercise######` left behind by the
    /// end-to-end suite. Paying by rank against that population would have been
    /// an uncapped diamond tap handed to the only person playing.
    ///
    /// So a tier pays nothing unless the ranked population is at least as large
    /// as the tier itself, AND at least <see cref="MinimumRankedPopulation"/>
    /// overall. With today's numbers that means the ladder pays ZERO, which is
    /// the correct answer rather than a broken one: the feature is built, and it
    /// starts paying when there are people to rank.
    /// </summary>
    public static class LeaderboardTierRegistry
    {
        /// <summary>
        /// Below this many ranked players, no tier pays anything at all.
        ///
        /// Modul: a floor on the WHOLE ladder, not just the wide tiers. Without
        /// it, "#1 of 3" would draw the top prize every week for as long as the
        /// game has three players, which is the shape of every runaway this
        /// project has had to unwind afterwards - see the codex yield that
        /// reached 71.9x. Cheap to lower once there is a real population;
        /// expensive to claw back diamonds already spent.
        /// </summary>
        public const int MinimumRankedPopulation = 20;

        /// <summary>
        /// Only players at or above this level are ranked at all.
        ///
        /// Modul: this is what keeps the board - and therefore the payouts -
        /// free of throwaway accounts. `exercise.mjs` registers a fresh account
        /// on every run and leaves it at level 1, and those were appearing on
        /// the live board under names like `exercise499579`. A level bar is a
        /// better filter than special-casing the test harness by name, because
        /// it also excludes an abandoned real account and cannot be defeated by
        /// renaming anything.
        /// </summary>
        public const int MinimumRankedLevel = 10;

        /// <summary>One rung: everybody from the previous rung's edge up to MaxRank.</summary>
        public sealed class Tier
        {
            public int Id { get; init; }
            public string Name { get; init; } = string.Empty;
            public int MaxRank { get; init; }
            public int WeeklyDiamonds { get; init; }
        }

        /// <summary>
        /// The ladder, narrowest first. Order is meaningful - <see cref="TierForRank"/>
        /// takes the first rung a rank fits inside.
        ///
        /// Modul: EXTEND BY APPENDING. A "top 10,000" rung is the obvious next
        /// one and it goes on the end; nothing here renumbers, because the
        /// client mirrors this list BY INDEX and a reorder would silently
        /// recolour every name on the board.
        /// </summary>
        public static readonly Tier[] Tiers =
        {
            new Tier { Id = 0, Name = "Champion",   MaxRank = 1,    WeeklyDiamonds = 60 },
            new Tier { Id = 1, Name = "Second",     MaxRank = 2,    WeeklyDiamonds = 45 },
            new Tier { Id = 2, Name = "Third",      MaxRank = 3,    WeeklyDiamonds = 35 },
            new Tier { Id = 3, Name = "Top 10",     MaxRank = 10,   WeeklyDiamonds = 25 },
            new Tier { Id = 4, Name = "Top 50",     MaxRank = 50,   WeeklyDiamonds = 15 },
            new Tier { Id = 5, Name = "Top 100",    MaxRank = 100,  WeeklyDiamonds = 10 },
            new Tier { Id = 6, Name = "Top 500",    MaxRank = 500,  WeeklyDiamonds = 5 },
            new Tier { Id = 7, Name = "Top 1000",   MaxRank = 1000, WeeklyDiamonds = 3 },
        };

        /// <summary>The tier a rank sits in, or null for unranked/below the ladder.</summary>
        public static Tier? TierForRank(int rank)
        {
            if (rank <= 0) return null;
            for (int i = 0; i < Tiers.Length; i++)
            {
                if (rank <= Tiers[i].MaxRank) return Tiers[i];
            }
            return null;
        }

        /// <summary>
        /// The tier id for the wire, or -1 for "no tier". Separate from
        /// <see cref="TierForRank"/> so the REST payload never carries a null
        /// the client has to special-case.
        /// </summary>
        public static int TierIdForRank(int rank) => TierForRank(rank)?.Id ?? -1;

        /// <summary>
        /// Diamonds this rank earns for the week, given how many players were
        /// actually ranked.
        ///
        /// Modul: BOTH GATES, and they are different questions. The population
        /// floor asks "is this game big enough for a leaderboard to mean
        /// anything"; the per-tier check asks "does this particular rung
        /// describe an achievement" - being 100th of 40 is being in the bottom
        /// half. Returns 0 rather than throwing, because a payout of nothing is
        /// a normal Sunday on a small server.
        /// </summary>
        public static int WeeklyDiamondsFor(int rank, int rankedPopulation)
        {
            if (rankedPopulation < MinimumRankedPopulation) return 0;

            Tier? tier = TierForRank(rank);
            if (tier is null) return 0;
            if (rankedPopulation < tier.MaxRank) return 0;

            return tier.WeeklyDiamonds;
        }

        // Modul: THERE IS NO WEEK-KEY HELPER HERE, ON PURPOSE.
        //
        // The payout is idempotent because the player row records which week it
        // was last paid for, and that key is already defined exactly once, in
        // `DelveEngine.CurrentWeekKey` (ISOWeek year * 100 + week). Writing a
        // second one here - even an identical one - would be two copies of one
        // truth, which is this codebase's dominant bug class, and the two would
        // only ever be noticed disagreeing on a New Year's Eve.
        //
        // It is a week key on the PLAYER ROW rather than a marker in Redis
        // because production runs Redis with `--save "" --appendonly no`: a
        // Redis marker would be wiped by every deploy and the whole board would
        // be paid again on the next cron tick.
    }
}

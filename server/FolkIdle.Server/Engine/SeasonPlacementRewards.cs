namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What finishing a season in a given place is worth.
    ///
    /// A season already pays out legacy shards for what a player accumulated -
    /// gold, levels, gear - which rewards PLAYING. This rewards placing, which
    /// is a different thing and the only reason a leaderboard is worth looking
    /// at twice.
    ///
    /// PAID IN DIAMONDS, deliberately, and not in shards:
    ///
    ///   - Gold is wiped at the rollover, so it cannot be a prize.
    ///   - Shards are the season's own currency and their only shop was the
    ///     Legacy shop, which was removed - a prize nobody can spend is a
    ///     number, not a reward.
    ///   - Diamonds survive the rollover and buy inheritance, which is the one
    ///     axis that carries across seasons. So placing well makes the NEXT
    ///     season easier, which is what a season-long chase should buy.
    ///
    /// The bands are wide on purpose. A ladder that only pays the top three
    /// tells everyone else their season did not count; paying every ranked
    /// player something, and paying it in steps a player can see themselves
    /// climbing, is what makes the last week of a season worth playing.
    /// </summary>
    public static class SeasonPlacementRewards
    {
        /// <summary>
        /// Diamonds for a final rank, 1-based. Rank 0 or below means unranked
        /// and pays nothing at all - not the participation band. Unranked
        /// means quarantined or never played, and neither is a season.
        /// </summary>
        public static int DiamondsForRank(int rank)
        {
            if (rank <= 0) return 0;

            if (rank == 1) return 2000;
            if (rank <= 3) return 1200;
            if (rank <= 10) return 600;
            if (rank <= 50) return 250;
            if (rank <= 100) return 100;

            // Everyone else who finished the season on the board.
            return 25;
        }

        /// <summary>
        /// The band a rank falls in, for anything that wants to name it -
        /// announcements, mail, a results screen. Kept beside the table so the
        /// wording and the numbers cannot drift apart.
        /// </summary>
        public static string BandNameForRank(int rank)
        {
            if (rank <= 0) return string.Empty;
            if (rank == 1) return "Champion";
            if (rank <= 3) return "Podium";
            if (rank <= 10) return "Top ten";
            if (rank <= 50) return "Top fifty";
            if (rank <= 100) return "Top hundred";
            return "Ranked";
        }
    }
}

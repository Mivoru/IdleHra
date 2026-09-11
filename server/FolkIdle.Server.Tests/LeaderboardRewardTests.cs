using System;
using System.Linq;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// What a place on the board is worth, and - far more importantly - when it
    /// is worth NOTHING.
    ///
    /// Modul: this exists because the honest measurement that preceded it was
    /// alarming. On the live database the day rewards were designed there were
    /// 29 registered accounts, exactly ONE above level 5, and the board itself
    /// carried throwaway accounts left behind by exercise.mjs - `exercise260549`
    /// and friends were visible on it. A payout keyed on rank, shipped against
    /// that population, would have been an uncapped diamond tap handed to the
    /// only person playing.
    ///
    /// Every runaway this project has had to unwind afterwards had the same
    /// shape - the codex yield multiplier reached 71.9x before a player found
    /// it, not CI - so the floor is asserted here rather than trusted to a
    /// comment.
    /// </summary>
    public class LeaderboardRewardTests
    {
        private readonly ITestOutputHelper _output;

        public LeaderboardRewardTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void A_small_server_pays_nothing_at_all()
        {
            // Modul: THE NUMBER THAT MATTERS IS THE RANKED COUNT, NOT THE
            // REGISTERED ONE, and getting that wrong is what this assertion
            // caught on its first run.
            //
            // Measured live the day this was written: 29 registered accounts -
            // but only ONE of them above level 5, and MinimumRankedLevel is 10.
            // So the ZSET the payout reads holds about one player, not 29. The
            // first draft of this test asserted against 29, which sails over
            // the floor of 20 and pays first place its full 60. The registered
            // count is not a population; it is a sign-up log.
            const int TodaysRankedPopulation = 1;

            for (int rank = 1; rank <= 20; rank++)
            {
                Assert.Equal(0, LeaderboardTierRegistry.WeeklyDiamondsFor(rank, TodaysRankedPopulation));
            }

            // And the boundary itself, so the floor cannot be quietly lowered
            // to nothing without this failing.
            Assert.Equal(0, LeaderboardTierRegistry.WeeklyDiamondsFor(
                1, LeaderboardTierRegistry.MinimumRankedPopulation - 1));
            Assert.True(LeaderboardTierRegistry.WeeklyDiamondsFor(
                1, LeaderboardTierRegistry.MinimumRankedPopulation) > 0);

            _output.WriteLine(
                $"{TodaysRankedPopulation} ranked player(s) today; the ladder pays nothing until " +
                $"{LeaderboardTierRegistry.MinimumRankedPopulation} are ranked at level " +
                $"{LeaderboardTierRegistry.MinimumRankedLevel}+.");
        }

        [Fact]
        public void A_tier_wider_than_the_playerbase_pays_nothing()
        {
            // 60 ranked players clears the overall floor, so the narrow tiers
            // pay - but "top 100" of 60 is everybody, and being last is not an
            // achievement.
            const int Population = 60;

            Assert.True(LeaderboardTierRegistry.WeeklyDiamondsFor(1, Population) > 0);
            Assert.True(LeaderboardTierRegistry.WeeklyDiamondsFor(50, Population) > 0);
            Assert.Equal(0, LeaderboardTierRegistry.WeeklyDiamondsFor(100, Population));
            Assert.Equal(0, LeaderboardTierRegistry.WeeklyDiamondsFor(600, Population));
        }

        [Fact]
        public void The_ladder_never_pays_more_for_a_worse_rank()
        {
            const int Population = 5000;
            int previous = int.MaxValue;

            for (int rank = 1; rank <= 1200; rank++)
            {
                int paid = LeaderboardTierRegistry.WeeklyDiamondsFor(rank, Population);
                Assert.True(paid <= previous, $"rank {rank} pays {paid}, better than rank {rank - 1}'s {previous}");
                if (paid > 0) previous = paid;
            }
        }

        [Fact]
        public void First_place_does_not_out_earn_the_delve()
        {
            // Modul: THE CALIBRATION ANCHOR, asserted rather than remembered.
            //
            // The Delve is the game's deliberate diamond tap and it is capped
            // at 60 a week so it cannot become the economy. A leaderboard that
            // paid more than that would quietly demote the Delve to a side
            // activity - and nothing anywhere would have said so.
            int champion = LeaderboardTierRegistry.Tiers.First().WeeklyDiamonds;

            Assert.True(
                champion <= DelveRegistry.MaxDiamondsPerWeek,
                $"first place pays {champion} a week against the Delve's {DelveRegistry.MaxDiamondsPerWeek} ceiling");

            _output.WriteLine($"first place {champion}/week vs Delve ceiling {DelveRegistry.MaxDiamondsPerWeek}/week");
        }

        [Fact]
        public void The_whole_ladder_is_affordable_against_the_store()
        {
            // Every tier paid in full, every week, for a full board. Printed
            // AND checked - a number a test only prints is decoration.
            int weekly = 0;
            int previousMax = 0;
            foreach (var tier in LeaderboardTierRegistry.Tiers)
            {
                int seatsInTier = tier.MaxRank - previousMax;
                weekly += seatsInTier * tier.WeeklyDiamonds;
                previousMax = tier.MaxRank;
            }

            _output.WriteLine($"a full 1000-deep board costs {weekly} diamonds a week across all tiers");

            // The smallest store pack is 500 diamonds. A full board must not
            // hand out more than a handful of packs' worth per week, or the
            // board becomes the cheapest source of diamonds in the game.
            Assert.True(weekly <= 500 * 20, $"{weekly} diamonds a week is more than twenty small packs");
        }

        [Fact]
        public void Tier_ids_are_positional_and_contiguous()
        {
            // The client mirrors this table BY INDEX for its colours, so a gap
            // or a reorder would silently recolour the board.
            for (int i = 0; i < LeaderboardTierRegistry.Tiers.Length; i++)
            {
                Assert.Equal(i, LeaderboardTierRegistry.Tiers[i].Id);
            }

            // And the thresholds must ascend, or TierForRank's first-match scan
            // would return the wrong rung.
            var ranks = LeaderboardTierRegistry.Tiers.Select(t => t.MaxRank).ToArray();
            Assert.Equal(ranks.OrderBy(r => r).ToArray(), ranks);
        }

        [Fact]
        public void An_unranked_player_has_no_tier()
        {
            Assert.Null(LeaderboardTierRegistry.TierForRank(0));
            Assert.Null(LeaderboardTierRegistry.TierForRank(-1));
            Assert.Equal(-1, LeaderboardTierRegistry.TierIdForRank(0));

            // Past the bottom rung is ranked but untiered, not an error.
            Assert.Null(LeaderboardTierRegistry.TierForRank(1001));
            Assert.Equal(0, LeaderboardTierRegistry.WeeklyDiamondsFor(1001, 5000));
        }

        [Fact]
        public void The_payout_week_key_is_the_one_the_delve_already_defines()
        {
            // Modul: there must not be a second week-key definition. The payout
            // is idempotent because the player row records a week key, and that
            // key is DelveEngine.CurrentWeekKey - if a second one were ever
            // added here they would agree until some New Year's Eve and then
            // silently pay a week twice.
            var newYearsEve = new DateTime(2027, 12, 31, 23, 0, 0, DateTimeKind.Utc);
            var newYearsDay = new DateTime(2028, 1, 1, 1, 0, 0, DateTimeKind.Utc);

            // Same ISO week spanning the year boundary - the keys must match,
            // which is the property "year * 100 + week" only has because
            // ISOWeek.GetYear is used rather than DateTime.Year.
            Assert.Equal(
                DelveEngine.CurrentWeekKey(newYearsEve),
                DelveEngine.CurrentWeekKey(newYearsDay));
        }
    }
}

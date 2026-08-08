using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Who turns up in the village, how often, and how many it can hold.
    ///
    /// THE VILLAGE IS THE GENE POOL. Breeding inherits each aptitude from one
    /// parent, so a child can never exceed the best value already in the pair -
    /// mutation alone drifts at +0.15 a generation, which is not a road to
    /// anywhere. Outside blood is what actually moves a bloodline, and the
    /// village is where it comes from.
    ///
    /// That is also the answer to a question the season reset raises: the
    /// village is rebuilt from nothing every ninety days and a permanent
    /// "Foundations" discount was considered and rejected, because a permanent
    /// discount is a head start that cannot be caught. So the rebuild needs a
    /// reason of its own, and this is it - THIS season's Inn decides what blood
    /// you can marry into THIS season's line.
    ///
    /// Pure and static: every rule here is arithmetic on an Inn level and a
    /// clock, so it belongs in a test that runs in a millisecond.
    ///
    /// NOTHING CALLS THIS YET, and that is deliberate rather than an oversight.
    /// Villagers have no table: today's breeding roster is the player's own
    /// characters, so hero-x-villager pairing needs storage, an arrival hook
    /// and a screen before any of it reaches a player. The rules are here first
    /// because they are where every decision lives and they can be verified on
    /// their own - see VillagerArrivalTests, and LONG_GAME_SPEC part 5 for what
    /// remains. Do not delete this as dead code; it is a floor, not a ruin.
    /// </summary>
    public static class VillagerArrivalRules
    {
        // --- how often ---------------------------------------------------------

        public const int BaseIntervalSeconds = 48 * 60 * 60;

        /// <summary>Hours the Inn shaves off the wait, per level.</summary>
        public const int HoursPerInnLevel = 2;

        /// <summary>
        /// However good the Inn, somebody has to walk there. A floor also keeps
        /// the arrival loop from turning into a per-tick spawner at high levels.
        /// </summary>
        public const int MinimumIntervalSeconds = 24 * 60 * 60;

        /// <summary>
        /// Seconds between arrivals at a given Inn level.
        ///
        /// The Inn drives BOTH how many people come and how good they are (see
        /// BreedingAptitudes.RollVillager) - one building, one legible lever.
        /// Over a ninety-day season this is roughly 45 arrivals at level 0 and
        /// 90 at the floor.
        /// </summary>
        public static int IntervalSecondsFor(int innLevel)
        {
            int reduction = Math.Max(0, innLevel) * HoursPerInnLevel * 3600;
            return Math.Max(MinimumIntervalSeconds, BaseIntervalSeconds - reduction);
        }

        // --- how many ----------------------------------------------------------

        public const int BasePopulationCap = 6;
        public const int AbsolutePopulationCap = 16;

        /// <summary>
        /// How many residents the village holds.
        ///
        /// A CAP RATHER THAN A STRETCHING TIMER. Making each arrival wait a day
        /// longer than the last was the other option and is worse: it is
        /// invisible, and it reads as a punishment for playing. A cap is legible
        /// at a glance ("Village 11/14"), it gives the housing buildings a
        /// purpose, and it creates the decision that makes the gene pool a game
        /// - somebody turns up at 4/3/9/2, so do you keep them or turn them away
        /// and wait for a better roll?
        /// </summary>
        public static int PopulationCapFor(int innLevel)
            => Math.Min(AbsolutePopulationCap, BasePopulationCap + Math.Max(0, innLevel));

        /// <summary>
        /// Two at the start of a season, rolled at Inn level 1 - so aptitudes of
        /// 2 or 3. Enough that the first two days are not dead, bad enough to
        /// want better.
        /// </summary>
        public const int SeasonStartVillagers = 2;

        public const int SeasonStartInnLevel = 1;

        // --- the arrival tick ---------------------------------------------------

        /// <summary>
        /// How many villagers should have arrived, and when the clock should
        /// next be considered to have started.
        ///
        /// Returns whole arrivals only and reports the CONSUMED time rather than
        /// the whole elapsed window, so the remainder carries forward - dropping
        /// it would silently lengthen every interval by up to one tick's worth,
        /// which over ninety days is a village several people short.
        ///
        /// A FULL VILLAGE STOPS THE CLOCK ENTIRELY. It does not bank arrivals
        /// against a slot freeing up later, because that would mean dismissing
        /// one villager instantly conjures the six you "missed" while full.
        /// </summary>
        public static (int Arrivals, long ConsumedSeconds) ArrivalsSince(
            long elapsedSeconds, int innLevel, int currentPopulation)
        {
            if (elapsedSeconds <= 0) return (0, 0);

            int cap = PopulationCapFor(innLevel);
            int room = cap - Math.Max(0, currentPopulation);
            if (room <= 0) return (0, elapsedSeconds);

            int interval = IntervalSecondsFor(innLevel);
            long due = elapsedSeconds / interval;
            if (due <= 0) return (0, 0);

            int arrivals = (int)Math.Min(due, room);
            return (arrivals, (long)arrivals * interval);
        }

        // --- paying for one now ---------------------------------------------------

        public const int RecruitBaseGold = 2_500;

        /// <summary>
        /// Multiplied in per recruitment already made this season, as a
        /// percentage - 160 means each costs 1.6x the last.
        /// </summary>
        public const int RecruitGrowthPercent = 160;

        /// <summary>
        /// Gold to attract somebody immediately, rather than waiting out the
        /// interval.
        ///
        /// Escalating so it cannot be spammed into a slot machine for a
        /// twenty-roll, and priced in gold because the economy needs a sink at
        /// the top end that is not the Forge.
        ///
        /// Clamped rather than allowed to overflow: 1.6^n passes long.MaxValue
        /// around the ninetieth recruitment, and a wrapped price would be free.
        /// </summary>
        public static long RecruitCostGold(int recruitmentsThisSeason)
        {
            if (recruitmentsThisSeason <= 0) return RecruitBaseGold;

            double cost = RecruitBaseGold;
            for (int i = 0; i < recruitmentsThisSeason; i++)
            {
                cost *= RecruitGrowthPercent / 100.0;
                if (cost >= long.MaxValue / 2) return long.MaxValue / 2;
            }
            return (long)cost;
        }

        /// <summary>
        /// Why a recruitment cannot happen, or null if it can. A reason rather
        /// than a bool, because both of these are things the player can act on.
        /// </summary>
        public static string? RecruitBlockedReason(
            int innLevel, int currentPopulation, long currentGold, int recruitmentsThisSeason)
        {
            int cap = PopulationCapFor(innLevel);
            if (currentPopulation >= cap)
            {
                return $"The village is full ({currentPopulation}/{cap}). Upgrade the Inn or send somebody on their way.";
            }

            long cost = RecruitCostGold(recruitmentsThisSeason);
            if (currentGold < cost)
            {
                return $"A feast that size costs {cost:N0} gold.";
            }

            return null;
        }
    }
}

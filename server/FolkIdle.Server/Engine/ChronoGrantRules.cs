using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What puts time in the chrono bank, and how much.
    ///
    /// NOTHING DID, BEFORE THIS. The only producer was
    /// ChronoCoreEngine.ConsumeChronoCoreAsync, which spends a "chrono core"
    /// item - and no such item is authored in items.json, a fact the engine's
    /// own comment already admitted. So `BankedChronoSeconds` was zero for
    /// every player who has ever played, the acceleration and time-warp
    /// buttons were permanently disabled, and the Boosts screen told players it
    /// "fills on its own" and that login rewards and the season pass grant it.
    /// None of that was true.
    ///
    /// TIME IS A REWARD, NOT A CLOCK. It is granted for doing things worth
    /// rewarding and never for merely being away - banking idle time would make
    /// the bank a second, hidden version of offline progress, and offline
    /// progress already pays in full (see OfflineSimulationEngine).
    ///
    /// THE NUMBERS, against the seven-day cap and the drain rate. Acceleration
    /// spends `(multiplier - 1)` seconds of bank per real second, so at 2x an
    /// hour of bank buys an hour of doubled play. The three sources together
    /// pay roughly a day and a half across a whole season, which is a treat
    /// rather than a second economy.
    /// </summary>
    public static class ChronoGrantRules
    {
        /// <summary>
        /// The seventh day of a login streak - the same day that already pays
        /// the diamond bonus, because that is the day the streak exists to
        /// reach.
        /// </summary>
        public const double LoginStreakDaySevenSeconds = 3600.0;

        /// <summary>
        /// A completed chapter of the Book of Deeds. Four hours, matching what
        /// a chrono core was always written to be worth - five Seals is twenty
        /// hours, earned across a season by exploring the whole game.
        /// </summary>
        public const double SealSeconds = 14400.0;

        /// <summary>
        /// Putting a region boss down for the FIRST time. Five bosses, ten
        /// hours, spread over the run that reaches region 5 - and paid at
        /// exactly the moments a player has just done something hard.
        /// </summary>
        public const double FirstBossClearSeconds = 7200.0;

        /// <summary>
        /// Everything the three sources can pay in one season, for judging
        /// whether the cap is in the right place.
        /// </summary>
        public static double MaximumSeasonalGrant(int loginStreakSevens, int seals, int bosses)
            => (Math.Max(0, loginStreakSevens) * LoginStreakDaySevenSeconds)
             + (Math.Max(0, seals) * SealSeconds)
             + (Math.Max(0, bosses) * FirstBossClearSeconds);

        /// <summary>
        /// Adds to a balance without letting it pass the cap. Written here
        /// rather than at each call site because there are now four of them
        /// and the clamp is the part that is easy to forget.
        /// </summary>
        public static double AddCapped(double currentSeconds, double grantSeconds)
        {
            if (grantSeconds <= 0.0) return currentSeconds;

            double total = currentSeconds + grantSeconds;
            return total > ChronoBufferEngine.MaxBankedChronoSeconds
                ? ChronoBufferEngine.MaxBankedChronoSeconds
                : total;
        }
    }
}

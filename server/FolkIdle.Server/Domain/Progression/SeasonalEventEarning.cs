using System;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// How the seasonal event currency is earned and kept in step with the
    /// calendar. Pure arithmetic over the payload - no database - so the tick,
    /// the offline catch-up and the tests all go through the same lines.
    ///
    /// Modul: NO DAILY CAP (owner, 2026-10-09: "a player who plays more earns
    /// more"). EarnedToday is still counted, for the screen and so a cap can
    /// come back as one line if the economy needs it.
    /// </summary>
    public static class SeasonalEventEarning
    {
        public enum Source : byte
        {
            Kill = 0,
            Gather = 1,
        }

        public static int DayKey(long nowEpochSeconds) => (int)(nowEpochSeconds / 86_400L);

        /// <summary>
        /// Brings the payload's balance in line with the calendar: zeroes it
        /// when it belongs to another event or the shop has closed, and resets
        /// the daily counter at UTC midnight. Called before every earn and
        /// spend, and at hydration.
        /// </summary>
        public static void Normalise(ref TickStatePayload payload, long nowEpochSeconds)
        {
            var (current, phase) = SeasonalEventRegistry.Current(nowEpochSeconds);
            if (phase == SeasonalEventPhase.None || current == null)
            {
                payload.EventCurrency = 0;
                payload.EventCurrencyEventId = 0;
                payload.EventCurrencyEarnedToday = 0;
                return;
            }
            if (payload.EventCurrencyEventId != current.Id)
            {
                payload.EventCurrency = 0;
                payload.EventCurrencyEventId = current.Id;
                payload.EventCurrencyEarnedToday = 0;
            }
            int today = DayKey(nowEpochSeconds);
            if (payload.EventCurrencyDayKey != today)
            {
                payload.EventCurrencyDayKey = today;
                payload.EventCurrencyEarnedToday = 0;
            }
        }

        /// <summary>
        /// Rolls <paramref name="actions"/> chances of one currency each.
        /// Returns what was paid. <paramref name="factor"/> scales the chance
        /// (offline pays at the event's OfflineFactor).
        /// </summary>
        public static int Roll(ref TickStatePayload payload, Source source, int actions, long nowEpochSeconds, Random random, double factor = 1.0)
        {
            if (actions <= 0) return 0;
            var (current, phase) = SeasonalEventRegistry.Current(nowEpochSeconds);
            if (phase != SeasonalEventPhase.Live || current == null) return 0;
            Normalise(ref payload, nowEpochSeconds);

            double chance = (source == Source.Kill ? current.KillChance : current.GatherChance) * factor;
            return Credit(ref payload, Draw(actions, chance, random));
        }

        /// <summary>
        /// A fixed grant (a boss win).
        /// </summary>
        public static int Grant(ref TickStatePayload payload, int amount, long nowEpochSeconds)
        {
            if (amount <= 0) return 0;
            var (current, phase) = SeasonalEventRegistry.Current(nowEpochSeconds);
            if (phase != SeasonalEventPhase.Live || current == null) return 0;
            Normalise(ref payload, nowEpochSeconds);
            return Credit(ref payload, amount);
        }

        private static int Credit(ref TickStatePayload payload, int amount)
        {
            if (amount <= 0) return 0;
            payload.EventCurrency = (int)Math.Min(int.MaxValue, (long)payload.EventCurrency + amount);
            payload.EventCurrencyEarnedToday += amount;
            payload.IsDirty = true;
            return amount;
        }

        /// <summary>
        /// Successes in <paramref name="trials"/> Bernoulli draws. Exact for a
        /// tick's handful of kills; for an offline catch-up of tens of
        /// thousands it uses the expectation with a random fractional part, so
        /// the cost stays constant and the mean stays exact.
        /// </summary>
        internal static int Draw(int trials, double chance, Random random)
        {
            if (trials <= 0 || chance <= 0) return 0;
            if (chance >= 1) return trials;
            if (trials <= 64)
            {
                int hits = 0;
                for (int i = 0; i < trials; i++)
                {
                    if (random.NextDouble() < chance) hits++;
                }
                return hits;
            }
            double expected = trials * chance;
            int whole = (int)Math.Floor(expected);
            return whole + (random.NextDouble() < expected - whole ? 1 : 0);
        }

        /// <summary>Spends <paramref name="price"/> if the balance covers it and the shop is open.</summary>
        public static bool TrySpend(ref TickStatePayload payload, int price, long nowEpochSeconds)
        {
            Normalise(ref payload, nowEpochSeconds);
            if (price <= 0 || payload.EventCurrency < price) return false;
            payload.EventCurrency -= price;
            payload.IsDirty = true;
            return true;
        }

        public static void Refund(ref TickStatePayload payload, int eventId, int amount)
        {
            // A refund for an event that has since rolled over is dropped: the
            // currency it would restore no longer exists.
            if (amount <= 0 || payload.EventCurrencyEventId != eventId) return;
            payload.EventCurrency = (int)Math.Min(int.MaxValue, (long)payload.EventCurrency + amount);
            payload.IsDirty = true;
        }
    }
}

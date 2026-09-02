using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Whether the daily login streak can actually be climbed.
    ///
    /// Every account in the live database sits at LoginStreakDays = 1,
    /// including one played across several weeks. That is consistent with
    /// legitimate resets - a missed day sets it back to 1, and that account
    /// was quarantined for most of a month - but it is also exactly what a
    /// streak that never advances would look like, and this one pays real
    /// premium currency on day 7. Evidence is not proof, so the ambiguity is
    /// settled here by driving the engine across simulated day boundaries
    /// instead of by reading it.
    ///
    /// The clock is injected (see the internal overload of
    /// TryGrantLoginRewardAsync); without that the only way to observe day 2
    /// was to wait until tomorrow.
    /// </summary>
    [Collection("Postgres collection")]
    public class DailyLoginStreakTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public DailyLoginStreakTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const long SecondsPerDay = 86400L;

        /// <summary>
        /// A UTC midnight whose date key is a multiple of seven, so a full
        /// seven-day streak sits inside a single rotating gold matrix rather
        /// than straddling two of them.
        /// </summary>
        private static long WeekAlignedMidnightEpoch()
        {
            long someKey = DailyLoginRewardEngine.ToDateKey(
                new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
            return (someKey - (someKey % 7L)) * SecondsPerDay;
        }

        private async Task<(Guid AccountId, long PlayerId)> SeedAccountAsync()
        {
            string tag = Guid.NewGuid().ToString("N");
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Email = $"streak_{tag}@example.com",
                Username = "Streak" + tag[..8],
                DeviceId = "streak-device-" + tag,
            };

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return (player.PlayerGuid, player.Id);
        }

        private async Task<(int StreakDays, long Gold, int Diamonds)> ReadStateAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            long gold = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity)
                .SingleOrDefaultAsync();
            return (player.LoginStreakDays, gold, player.PremiumDiamonds);
        }

        /// <summary>
        /// The date-key rule itself, asserted rather than assumed: a day is
        /// the UTC day number, and the boundary is midnight UTC to the
        /// second. This is the assertion that would fail if anyone ever
        /// keyed the streak on the server's local calendar, which is the
        /// classic way a streak becomes unwinnable for entire timezones
        /// while looking correct to whoever is sitting in UTC.
        /// </summary>
        [Fact]
        public void TheDateKeyIsTheUtcDayAndTurnsOverAtUtcMidnight()
        {
            var midnight = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
            long midnightEpoch = midnight.ToUnixTimeSeconds();
            long dayKey = DailyLoginRewardEngine.ToDateKey(midnightEpoch);

            // Every second of that UTC day, sampled hourly, is the same day.
            for (int hour = 0; hour < 24; hour++)
            {
                Assert.Equal(dayKey, DailyLoginRewardEngine.ToDateKey(midnightEpoch + (hour * 3600L)));
            }

            // The last second of the day is still that day; the next one is not.
            Assert.Equal(dayKey, DailyLoginRewardEngine.ToDateKey(midnightEpoch + SecondsPerDay - 1L));
            Assert.Equal(dayKey + 1L, DailyLoginRewardEngine.ToDateKey(midnightEpoch + SecondsPerDay));

            // And the boundary is UTC, not this machine's zone: the same
            // instant expressed in a non-zero offset lands on the same key,
            // even where that offset puts it on a different local calendar
            // date. 2026-03-01T23:30Z is already the 2nd in UTC+2.
            long sameInstantElsewhere =
                new DateTimeOffset(2026, 3, 2, 1, 30, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds();
            Assert.Equal(midnightEpoch + (23L * 3600L) + 1800L, sameInstantElsewhere);
            Assert.Equal(dayKey, DailyLoginRewardEngine.ToDateKey(sameInstantElsewhere));
        }

        /// <summary>
        /// The core question: 1 -> 2 -> 3 across consecutive UTC days, with
        /// the gold actually landing in the commodity row each time. This
        /// project's worst defects have all been an output side that was
        /// never wired, so the balance is read back from the database rather
        /// than trusted from the result struct.
        /// </summary>
        [Fact]
        public async Task StreakAdvancesOnConsecutiveDaysAndTheGoldIsReallyCredited()
        {
            var (accountId, playerId) = await SeedAccountAsync();
            long midnight = WeekAlignedMidnightEpoch();
            long expectedGold = 0L;

            for (int day = 1; day <= 3; day++)
            {
                long now = midnight + ((day - 1) * SecondsPerDay) + (9L * 3600L);
                var result = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                    _fixture.RetryingOptions, accountId, now);

                Assert.True(result.Granted, $"day {day} granted nothing");
                Assert.Equal(day, result.StreakDay);
                expectedGold += result.GoldGranted;

                var state = await ReadStateAsync(playerId);
                Assert.Equal(day, state.StreakDays);
                Assert.Equal(expectedGold, state.Gold);
                _output.WriteLine($"day {day}: streak {state.StreakDays}, +{result.GoldGranted} gold, {state.Gold} total");
            }
        }

        /// <summary>
        /// One skipped day is the whole reset rule, and the boundary case
        /// that matters is the near miss: a login 48 hours after the last
        /// one is two keys later, so it starts again at 1 even though the
        /// player only missed a single calendar day.
        /// </summary>
        [Fact]
        public async Task ASkippedDayResetsTheStreakToOne()
        {
            var (accountId, playerId) = await SeedAccountAsync();
            long midnight = WeekAlignedMidnightEpoch();

            var first = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + (10L * 3600L));
            Assert.Equal(1, first.StreakDay);

            var second = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + SecondsPerDay + (10L * 3600L));
            Assert.Equal(2, second.StreakDay);

            // Now miss a day entirely.
            var afterGap = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + (3L * SecondsPerDay) + (10L * 3600L));

            Assert.True(afterGap.Granted);
            Assert.Equal(1, afterGap.StreakDay);
            Assert.Equal(0, afterGap.PremiumDiamondsGranted);

            var state = await ReadStateAsync(playerId);
            Assert.Equal(1, state.StreakDays);
        }

        /// <summary>
        /// Day 7 pays 100 diamonds, once. This is the only part of the
        /// feature that spends real premium currency, so it is asserted from
        /// both ends: nothing before day 7, exactly one payment on it, and
        /// the eighth day starting a fresh streak without paying again.
        /// </summary>
        [Fact]
        public async Task TheSeventhConsecutiveDayPaysTheDiamondsExactlyOnce()
        {
            var (accountId, playerId) = await SeedAccountAsync();
            long midnight = WeekAlignedMidnightEpoch();
            long weeklyGold = 0L;

            for (int day = 1; day <= 7; day++)
            {
                var result = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                    _fixture.RetryingOptions, accountId, midnight + ((day - 1) * SecondsPerDay) + (8L * 3600L));

                Assert.True(result.Granted);
                Assert.Equal(day, result.StreakDay);
                weeklyGold += result.GoldGranted;

                int expectedDiamonds = day == 7 ? DailyLoginRewardEngine.PremiumDiamondsOnDay7Completion : 0;
                Assert.Equal(expectedDiamonds, result.PremiumDiamondsGranted);
            }

            var afterWeek = await ReadStateAsync(playerId);
            Assert.Equal(7, afterWeek.StreakDays);
            Assert.Equal(DailyLoginRewardEngine.PremiumDiamondsOnDay7Completion, afterWeek.Diamonds);

            // A week-aligned streak sits inside one rotating matrix, and every
            // matrix is worth the same 25,500 gold - rotation changes the
            // shape of the week, never its earning power.
            Assert.Equal(25_500L, weeklyGold);
            _output.WriteLine($"seven days paid {weeklyGold} gold and {afterWeek.Diamonds} diamonds");

            // The eighth consecutive day wraps to a new streak and must not
            // pay the day-7 bonus a second time.
            var dayEight = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + (7L * SecondsPerDay) + (8L * 3600L));

            Assert.True(dayEight.Granted);
            Assert.Equal(1, dayEight.StreakDay);
            Assert.Equal(0, dayEight.PremiumDiamondsGranted);

            var afterWrap = await ReadStateAsync(playerId);
            Assert.Equal(DailyLoginRewardEngine.PremiumDiamondsOnDay7Completion, afterWrap.Diamonds);
        }

        /// <summary>
        /// Signing in twice on one UTC day pays once. The web client
        /// re-authenticates on every page load and on remembered-device
        /// auto-relogin, so this is the ordinary case, not an attack - but it
        /// is also the replay defence, since the grant is keyed on stored
        /// server state and not on anything the client sends.
        /// </summary>
        [Fact]
        public async Task TwoSignInsOnTheSameUtcDayPayExactlyOnce()
        {
            var (accountId, playerId) = await SeedAccountAsync();
            long midnight = WeekAlignedMidnightEpoch();

            var first = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + 60L);
            Assert.True(first.Granted);

            var afterFirst = await ReadStateAsync(playerId);

            // Nearly a full day later - but the same UTC day, so nothing.
            var second = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + SecondsPerDay - 60L);

            Assert.False(second.Granted);
            Assert.Equal(0L, second.GoldGranted);
            Assert.Equal(0, second.PremiumDiamondsGranted);

            var afterSecond = await ReadStateAsync(playerId);
            Assert.Equal(afterFirst.StreakDays, afterSecond.StreakDays);
            Assert.Equal(afterFirst.Gold, afterSecond.Gold);
            Assert.Equal(afterFirst.Diamonds, afterSecond.Diamonds);
        }

        /// <summary>
        /// The sharpest form of the boundary rule, driven through the whole
        /// transaction: 23:59:00 and 00:01:00 are two minutes apart and are
        /// two different days, so the streak advances. The mirror case in
        /// TwoSignInsOnTheSameUtcDayPayExactlyOnce is nearly twenty-four
        /// hours apart and is one day, so it does not. Elapsed time is not
        /// the rule; the UTC calendar boundary is.
        /// </summary>
        [Fact]
        public async Task CrossingMidnightUtcAdvancesTheStreakEvenTwoMinutesApart()
        {
            var (accountId, playerId) = await SeedAccountAsync();
            long midnight = WeekAlignedMidnightEpoch();

            var lateNight = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + SecondsPerDay - 60L);
            Assert.True(lateNight.Granted);
            Assert.Equal(1, lateNight.StreakDay);

            var justAfterMidnight = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, midnight + SecondsPerDay + 60L);

            Assert.True(justAfterMidnight.Granted);
            Assert.Equal(2, justAfterMidnight.StreakDay);

            var state = await ReadStateAsync(playerId);
            Assert.Equal(2, state.StreakDays);
        }

        /// <summary>
        /// A brand new account is day 1, not day 0 and not a skipped day -
        /// LastLoginTimestamp of 0 is "never seen", which is a distinct case
        /// from a stale timestamp and would otherwise be read as an enormous
        /// gap from 1970.
        /// </summary>
        [Fact]
        public async Task AFirstEverSignInStartsAtDayOne()
        {
            var (accountId, playerId) = await SeedAccountAsync();

            var before = await ReadStateAsync(playerId);
            Assert.Equal(0, before.StreakDays);

            var result = await DailyLoginRewardEngine.TryGrantLoginRewardAsync(
                _fixture.RetryingOptions, accountId, WeekAlignedMidnightEpoch() + (12L * 3600L));

            Assert.True(result.Granted);
            Assert.Equal(1, result.StreakDay);
            Assert.True(result.GoldGranted > 0L);
        }
    }
}

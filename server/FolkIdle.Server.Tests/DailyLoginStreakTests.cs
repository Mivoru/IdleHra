using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The login week, claimed by hand (owner, 2026-11-15).
    ///
    /// Every reward is read back from the database - gold row, diamond column,
    /// chest rows, dungeon tickets - rather than trusted from the result,
    /// because this project's worst defects were all an output side that was
    /// never wired. The clock is an argument to ClaimAsync, so a week is
    /// driven in a loop instead of waited for.
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

        private static readonly DateTime Monday = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

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

        private async Task<DailyLoginRewardEngine.ClaimResult> ClaimAsync(long playerId, DateTime at)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await DailyLoginRewardEngine.ClaimAsync(db, playerId, at);
        }

        private async Task<(int Day, long Gold, int Diamonds, int Weeks)> ReadStateAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            long gold = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity)
                .SingleOrDefaultAsync();
            return (player.LoginStreakDays, gold, player.PremiumDiamonds, player.LoginCyclesCompleted);
        }

        /// <summary>
        /// A day is the UTC day number, and the boundary is midnight UTC to the
        /// second - the assertion that fails if anyone keys the week on the
        /// server's local calendar, which makes a streak unwinnable for whole
        /// timezones while looking right to whoever sits in UTC.
        /// </summary>
        [Fact]
        public void TheDateKeyIsTheUtcDayAndTurnsOverAtUtcMidnight()
        {
            var midnight = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
            long midnightEpoch = midnight.ToUnixTimeSeconds();
            long dayKey = DailyLoginRewardEngine.ToDateKey(midnightEpoch);

            for (int hour = 0; hour < 24; hour++)
            {
                Assert.Equal(dayKey, DailyLoginRewardEngine.ToDateKey(midnightEpoch + (hour * 3600L)));
            }

            Assert.Equal(dayKey, DailyLoginRewardEngine.ToDateKey(midnightEpoch + SecondsPerDay - 1L));
            Assert.Equal(dayKey + 1L, DailyLoginRewardEngine.ToDateKey(midnightEpoch + SecondsPerDay));

            long sameInstantElsewhere =
                new DateTimeOffset(2026, 3, 2, 1, 30, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds();
            Assert.Equal(dayKey, DailyLoginRewardEngine.ToDateKey(sameInstantElsewhere));
        }

        [Fact]
        public void TheWeekHasSevenDaysAndDaySevenIsTheBest()
        {
            Assert.Equal(DailyLoginRewardEngine.DaysInWeek, DailyLoginRewardEngine.Week.Count);
            for (int i = 0; i < DailyLoginRewardEngine.Week.Count; i++)
            {
                Assert.Equal(i + 1, DailyLoginRewardEngine.Week[i].Day);
            }

            var seventh = DailyLoginRewardEngine.RewardFor(7);
            Assert.Equal(DailyLoginRewardEngine.Week.Max(d => d.Diamonds), seventh.Diamonds);
            Assert.Equal(DailyLoginRewardEngine.Week.Max(d => d.GoldMinutes), seventh.GoldMinutes);
            Assert.Equal(LootChestTier.Ancient, seventh.LootChest);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 2)]
        [InlineData(6, 7)]
        [InlineData(7, 1)]
        public void TheNextDayFollowsTheLastClaimedAndWrapsAfterSeven(int last, int next)
        {
            Assert.Equal(next, DailyLoginRewardEngine.NextDay(last));
        }

        /// <summary>
        /// Gold is minutes of the player's own earned income. Measured on
        /// production 2026-11-15: ~10M gold an hour, against a flat old reward
        /// of at most 30k - so the claim must actually read the ledger.
        /// </summary>
        [Fact]
        public async Task GoldIsMinutesOfTheIncomeTheLedgerRecords()
        {
            var (_, playerId) = await SeedAccountAsync();
            const long hourly = 10_000_000L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.GoldIncomeDaily.Add(new GoldIncomeDaily
                {
                    PlayerId = playerId,
                    Day = DateOnly.FromDateTime(Monday).AddDays(-1),
                    Source = (short)GoldIncomeSource.Combat,
                    Amount = hourly * 24L * GoldRewardScale.WindowDays,
                });
                await db.SaveChangesAsync();
            }

            var result = await ClaimAsync(playerId, Monday);

            Assert.Equal(DailyLoginRewardEngine.ClaimOutcome.Ok, result.Outcome);
            Assert.Equal(1, result.Day);
            Assert.Equal(GoldRewardScale.ForMinutes(hourly, DailyLoginRewardEngine.RewardFor(1).GoldMinutes), result.Gold);
            Assert.Equal(result.Gold, (await ReadStateAsync(playerId)).Gold);
            _output.WriteLine($"day 1 at {hourly:N0}/h pays {result.Gold:N0} gold");
        }

        [Fact]
        public async Task AWholeWeekPaysEveryDaysRewardIntoTheDatabase()
        {
            var (_, playerId) = await SeedAccountAsync();
            long gold = 0;
            int diamonds = 0;

            for (int day = 1; day <= 7; day++)
            {
                var result = await ClaimAsync(playerId, Monday.AddDays(day - 1));
                Assert.Equal(DailyLoginRewardEngine.ClaimOutcome.Ok, result.Outcome);
                Assert.Equal(day, result.Day);
                gold += result.Gold;
                diamonds += result.Diamonds;

                var state = await ReadStateAsync(playerId);
                Assert.Equal(day, state.Day);
                Assert.Equal(gold, state.Gold);
                Assert.Equal(diamonds, state.Diamonds);
            }

            Assert.Equal(DailyLoginRewardEngine.Week.Sum(d => d.Diamonds), diamonds);
            Assert.Equal(1, (await ReadStateAsync(playerId)).Weeks);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var lootChests = await db.LootChests.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync();
            Assert.Equal(
                DailyLoginRewardEngine.Week.Where(d => d.LootChest != null).Select(d => (byte)d.LootChest!.Value).OrderBy(t => t),
                lootChests.Select(c => c.Tier).OrderBy(t => t));
            Assert.All(lootChests, c => Assert.Equal((byte)LootChestSource.LoginReward, c.Source));

            int wardrobeChests = await db.CosmeticItems.AsNoTracking()
                .CountAsync(c => c.PlayerId == playerId && c.Source == (byte)CosmeticSource.LoginReward);
            Assert.Equal(DailyLoginRewardEngine.Week.Count(d => d.WardrobeChestRarity > 0), wardrobeChests);
        }

        [Fact]
        public async Task TheDayAfterDaySevenIsDayOneAgain()
        {
            var (_, playerId) = await SeedAccountAsync();
            for (int day = 1; day <= 7; day++)
            {
                await ClaimAsync(playerId, Monday.AddDays(day - 1));
            }

            var eighth = await ClaimAsync(playerId, Monday.AddDays(7));
            Assert.Equal(1, eighth.Day);
        }

        [Fact]
        public async Task ASecondClaimOnTheSameUtcDayPaysNothing()
        {
            var (_, playerId) = await SeedAccountAsync();
            var first = await ClaimAsync(playerId, Monday);
            var before = await ReadStateAsync(playerId);

            var second = await ClaimAsync(playerId, Monday.AddHours(14));

            Assert.Equal(DailyLoginRewardEngine.ClaimOutcome.Ok, first.Outcome);
            Assert.Equal(DailyLoginRewardEngine.ClaimOutcome.AlreadyClaimed, second.Outcome);
            Assert.Equal(before, await ReadStateAsync(playerId));
        }

        /// <summary>
        /// Owner, 2026-11-15: a missed day PAUSES the week. Three days away
        /// after claiming day 2 resumes at day 3, not at day 1.
        /// </summary>
        [Fact]
        public async Task MissedDaysPauseTheWeekInsteadOfResettingIt()
        {
            var (_, playerId) = await SeedAccountAsync();
            await ClaimAsync(playerId, Monday);
            await ClaimAsync(playerId, Monday.AddDays(1));

            var afterGap = await ClaimAsync(playerId, Monday.AddDays(5));

            Assert.Equal(3, afterGap.Day);
        }

        [Fact]
        public async Task MidnightUtcOpensTheNextDayEvenMinutesLater()
        {
            var (_, playerId) = await SeedAccountAsync();
            var lateNight = new DateTime(2026, 3, 2, 23, 59, 0, DateTimeKind.Utc);

            var first = await ClaimAsync(playerId, lateNight);
            var second = await ClaimAsync(playerId, lateNight.AddMinutes(2));

            Assert.Equal(1, first.Day);
            Assert.Equal(2, second.Day);
        }

        [Fact]
        public async Task DayTwoAddsATicketToEveryDungeonGate()
        {
            var (_, playerId) = await SeedAccountAsync();
            await ClaimAsync(playerId, Monday);

            var result = await ClaimAsync(playerId, Monday.AddDays(1));

            Assert.Equal(2, result.Day);
            Assert.Equal(DungeonBoss.All.Count(), result.TicketsGranted);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var gates = await db.DungeonProgress.AsNoTracking().Where(d => d.PlayerId == playerId).ToListAsync();
            Assert.Equal(DungeonBoss.All.Count(), gates.Count);
            // The day's top-up fills a gate to the cap; the claim's ticket sits above it.
            Assert.All(gates, g => Assert.Equal(DungeonEngine.TicketCap + 1, g.Tickets));
        }

        /// <summary>
        /// A password sign-in only stamps the account as active (guild
        /// succession reads it). It pays nothing - and it does not use up the
        /// day's claim either.
        /// </summary>
        [Fact]
        public async Task SigningInPaysNothingAndLeavesTheClaimOpen()
        {
            var (accountId, playerId) = await SeedAccountAsync();
            long now = new DateTimeOffset(Monday).ToUnixTimeSeconds();

            await DailyLoginRewardEngine.NoteSignInAsync(_fixture.RetryingOptions, accountId, now);

            var state = await ReadStateAsync(playerId);
            Assert.Equal((0, 0L, 0, 0), state);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(now, await db.PlayerRecords.Where(p => p.Id == playerId).Select(p => p.LastLoginTimestamp).SingleAsync());
            }

            Assert.Equal(DailyLoginRewardEngine.ClaimOutcome.Ok, (await ClaimAsync(playerId, Monday)).Outcome);
        }
    }
}

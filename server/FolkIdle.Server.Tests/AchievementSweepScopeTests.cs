using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: AchievementEngine sweep scope, 2026-08-02.
    //
    // The engine had no test of any kind, and its "active players" filter was:
    //
    //   Environment.TickCount64 - p.LastLogoutTimestamp < 60000
    //
    // milliseconds-since-boot minus unix SECONDS. The difference is hugely
    // negative, so it matched every account in the database, every 15 seconds,
    // each in its own Serializable transaction.
    //
    // The trap this file exists to spring is the OBVIOUS repair. Fixing only
    // the units - comparing unix seconds to unix seconds over a 60-second
    // window - looks correct and is strictly worse than the bug: because
    // LastLogoutTimestamp is written at LOGIN as well as logout, a real
    // 60-second window silently EXCLUDES anyone who has been playing for more
    // than a minute, which is exactly the population earning achievements.
    // The second test below fails against that repair and passes against the
    // real one.
    [Collection("Postgres collection")]
    public class AchievementSweepScopeTests
    {
        private readonly PostgresTestFixture _fixture;

        public AchievementSweepScopeTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<PlayerRecord> CreatePlayerAsync(long lastLogoutTimestamp)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"sweep_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                LastLogoutTimestamp = lastLogoutTimestamp,
            };

            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player;
        }

        // The Treasury achievement (bit 0) fires at 100k gold and is the
        // cheapest observable side effect of having been swept.
        private async Task GiveTreasuryQualifyingGoldAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.CommodityRecords.Add(new CommodityRecord
            {
                PlayerId = playerId,
                ItemId = "gold",
                Quantity = 250_000L,
            });
            await db.SaveChangesAsync();
        }

        private async Task<int> ClaimedFlagsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var record = await db.PlayerAchievements.AsNoTracking().FirstOrDefaultAsync(a => a.PlayerId == playerId);
            return record?.ClaimedAchievementFlags ?? 0;
        }

        [Fact]
        public async Task Sweep_TouchesOnlyPlayersWhoAreActuallyOnline()
        {
            var online = await CreatePlayerAsync(lastLogoutTimestamp: 0);
            var offline = await CreatePlayerAsync(lastLogoutTimestamp: 0);

            await GiveTreasuryQualifyingGoldAsync(online.Id);
            await GiveTreasuryQualifyingGoldAsync(offline.Id);

            var registry = new PlayerSessionRegistry();
            registry.RegisterPlayer(online.Id);

            var engine = new AchievementEngine(_fixture.ServiceProvider, registry);
            int swept = await engine.SweepOnceAsync(CancellationToken.None);

            Assert.Equal(1, swept);
            Assert.Equal(1, await ClaimedFlagsAsync(online.Id) & 1);

            // The whole scalability point: an offline account with a
            // qualifying balance is simply not loaded. Before this fix every
            // row in the table was, on every 15-second cycle.
            Assert.Equal(0, await ClaimedFlagsAsync(offline.Id) & 1);
        }

        // The regression guard against "just fix the units".
        [Fact]
        public async Task Sweep_StillCoversAPlayerWhoseSessionStartedHoursAgo()
        {
            // LastLogoutTimestamp is set at LOGIN too, so a player who has
            // been happily online for three hours carries a three-hour-old
            // value here. Any repair that filters on a short window over this
            // column drops them, and they stop earning achievements entirely.
            long threeHoursAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3 * 60 * 60;

            var longSession = await CreatePlayerAsync(threeHoursAgo);
            await GiveTreasuryQualifyingGoldAsync(longSession.Id);

            var registry = new PlayerSessionRegistry();
            registry.RegisterPlayer(longSession.Id);

            var engine = new AchievementEngine(_fixture.ServiceProvider, registry);
            int swept = await engine.SweepOnceAsync(CancellationToken.None);

            Assert.Equal(1, swept);
            Assert.Equal(1, await ClaimedFlagsAsync(longSession.Id) & 1);
        }

        [Fact]
        public async Task Sweep_WithNobodyOnline_TouchesTheDatabaseNotAtAll()
        {
            // Nine idle accounts used to mean nine Serializable transactions
            // every fifteen seconds forever. Zero online must mean zero work.
            var engine = new AchievementEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            Assert.Equal(0, await engine.SweepOnceAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Sweep_IsIdempotent_AndDoesNotPayTheRewardTwice()
        {
            var player = await CreatePlayerAsync(lastLogoutTimestamp: 0);
            await GiveTreasuryQualifyingGoldAsync(player.Id);

            var registry = new PlayerSessionRegistry();
            registry.RegisterPlayer(player.Id);
            var engine = new AchievementEngine(_fixture.ServiceProvider, registry);

            await engine.SweepOnceAsync(CancellationToken.None);
            int afterFirst = await DiamondsAsync(player.Id);

            await engine.SweepOnceAsync(CancellationToken.None);
            int afterSecond = await DiamondsAsync(player.Id);

            // The sweep runs every 15 seconds for the whole of a session, so a
            // condition that stays true must not keep paying out.
            Assert.Equal(afterFirst, afterSecond);
            Assert.True(afterFirst >= 100, $"expected the Treasury reward to be paid once, saw {afterFirst} diamonds");
        }

        private async Task<int> DiamondsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId)
                .Select(p => p.PremiumDiamonds).FirstAsync();
        }
    }
}

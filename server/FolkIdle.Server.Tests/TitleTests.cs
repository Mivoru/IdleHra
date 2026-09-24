using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>Titles (task 37 spec §4): the registry, the idempotent grant, and wearing one.</summary>
    public class TitleRegistryTests
    {
        [Fact]
        public void SlugsAreUniqueAndShortLowercaseIdentifiers()
        {
            var slugs = TitleRegistry.All.Select(t => t.Slug).ToList();
            Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.Ordinal).Count());
            Assert.All(slugs, s => Assert.Matches(new Regex("^[a-z0-9_]{1,32}$"), s));
            Assert.All(TitleRegistry.All, t => Assert.False(string.IsNullOrWhiteSpace(t.DisplayName)));
        }

        [Fact]
        public void TheDeepTitlesAreTheOwnersSixAtTheirFloors()
        {
            var expected = new[]
            {
                ("deep_10", "Lamplighter", 10),
                ("deep_15", "Deepwalker", 15),
                ("deep_20", "Of the Dark Water", 20),
                ("deep_30", "Lantern-Eater", 30),
                ("deep_40", "Where No Bell Rings", 40),
                ("deep_50", "The Bottomless", 50),
            };
            Assert.Equal(expected, TitleRegistry.All.Select(t => (t.Slug, t.DisplayName, t.DeepFloor)).ToArray());

            Assert.Empty(TitleRegistry.ForDeepFloor(9));
            Assert.Equal(new[] { "deep_10", "deep_15" }, TitleRegistry.ForDeepFloor(19).Select(t => t.Slug));
            Assert.Equal("deep_20", TitleRegistry.NextDeepTitle(15)!.Slug);
            Assert.Null(TitleRegistry.NextDeepTitle(50));
        }
    }

    [Collection("Postgres collection")]
    public class TitleTests
    {
        private readonly PostgresTestFixture _fixture;
        public TitleTests(PostgresTestFixture fixture) => _fixture = fixture;

        private static readonly Random Pass = new ConstantRandom(0.0);

        private async Task SeedAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM player_titles WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DelveRunRecords\" WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerRecords\" WHERE \"Id\" = {0}", playerId);
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"titled{playerId}",
                CurrentLevel = 40,
                BaseStrength = 400, BaseDexterity = 400, BaseConstitution = 400, BaseLuck = 400,
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 500_000_000 });
            await db.SaveChangesAsync();
        }

        private async Task<string[]> SlugsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerTitles.AsNoTracking().Where(t => t.PlayerId == playerId).Select(t => t.TitleSlug).OrderBy(s => s).ToArrayAsync();
        }

        [Fact]
        public async Task CrossingFloorTenGrantsLamplighterExactlyOnceEvenWhenReplayed()
        {
            const long playerId = 983000001L;
            await SeedAsync(playerId);
            var engine = new DelveEngine(_fixture.DbContextFactory, new DelveDeepSettings { Enabled = true });

            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            await engine.ChooseDoorAsync(playerId, 0, Pass);             // clears 9
            Assert.Empty(await SlugsAsync(playerId));

            var landing = await engine.GetViewAsync(playerId);
            Assert.Equal("deep_10", landing.NextTitle!.Slug);
            await engine.DescendAsync(playerId, landing.StakeGold, Pass);
            var cleared = await engine.ChooseDoorAsync(playerId, 0, Pass); // clears 10
            Assert.Equal(DelveResult.FloorCleared, cleared.Result);
            Assert.Equal(new[] { "deep_10" }, await SlugsAsync(playerId));
            Assert.Equal("deep_15", cleared.View.NextTitle!.Slug);

            // Replayed: a second run to floor 10, and a direct re-grant.
            await engine.BankAsync(playerId);
            view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            await engine.ChooseDoorAsync(playerId, 0, Pass);
            landing = await engine.GetViewAsync(playerId);
            await engine.DescendAsync(playerId, landing.StakeGold, Pass);
            await engine.ChooseDoorAsync(playerId, 0, Pass);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.False(await TitleEngine.GrantAsync(db, playerId, "deep_10", DateTime.UtcNow));
            }
            Assert.Equal(new[] { "deep_10" }, await SlugsAsync(playerId));
        }

        [Fact]
        public async Task AnUnearnedTitleIsRefusedAndChangesNothing()
        {
            const long playerId = 983000002L;
            await SeedAsync(playerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            Assert.Equal(TitleResult.NotEarned, await TitleEngine.SetActiveAsync(db, playerId, "deep_50"));
            Assert.Equal(TitleResult.UnknownTitle, await TitleEngine.SetActiveAsync(db, playerId, "not_a_title"));

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null((await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId)).ActiveTitleSlug);
        }

        [Fact]
        public async Task AnEarnedTitleCanBeWornAndCleared()
        {
            const long playerId = 983000003L;
            await SeedAsync(playerId);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(await TitleEngine.GrantAsync(db, playerId, "deep_15", DateTime.UtcNow));
                Assert.Equal(TitleResult.Ok, await TitleEngine.SetActiveAsync(db, playerId, "deep_15"));
            }

            var engine = new DelveEngine(_fixture.DbContextFactory, new DelveDeepSettings { Enabled = true });
            Assert.Equal("Deepwalker", (await engine.GetViewAsync(playerId)).ActiveTitle);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(TitleResult.Ok, await TitleEngine.SetActiveAsync(db, playerId, null));
                var titles = await TitleEngine.ListAsync(db, playerId);
                Assert.Single(titles);
                Assert.Equal("Deepwalker", titles[0].Name);
            }

            Assert.Null((await engine.GetViewAsync(playerId)).ActiveTitle);
        }

        /// <summary>
        /// What /api/v1/players/profile sends as ActiveTitle - the display name,
        /// resolved on the server from the stored slug.
        /// </summary>
        [Fact]
        public async Task TheProfileCarriesTheActiveTitlesDisplayName()
        {
            const long playerId = 983000004L;
            await SeedAsync(playerId);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await TitleEngine.GrantAsync(db, playerId, "deep_20", DateTime.UtcNow);
                await TitleEngine.SetActiveAsync(db, playerId, "deep_20");
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            Assert.Equal("Of the Dark Water", TitleRegistry.DisplayNameFor(player.ActiveTitleSlug));

            // And the board row shows it too.
            var engine = new DelveEngine(_fixture.DbContextFactory, new DelveDeepSettings { Enabled = true });
            var view = await engine.DevPlaceRunAtBottomAsync(playerId);
            await engine.DescendAsync(playerId, view.StakeGold, Pass);
            var board = await DeepestBoard.TopAsync(verify, DateTime.UtcNow);
            Assert.Equal("Of the Dark Water", board.Single(r => r.PlayerId == playerId).Title);
        }
    }
}

using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: GUILD BUFFS DID NOTHING, 2026-09-25.
    ///
    /// The tick reads GuildBonusesCache, and nothing ever wrote to it: the
    /// only loader was called by nobody. A purchased buff showed on the guild
    /// screen, which reads the table, and changed no number in combat. These
    /// tests cover the cache's writers: the purchase itself, the start-up
    /// load, and the expiry that stands in for a timer.
    /// </summary>
    [Collection("Postgres collection")]
    public class GuildBuffCacheTests
    {
        private readonly PostgresTestFixture _fixture;

        public GuildBuffCacheTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task APurchasedBuffReachesTheTickImmediately()
        {
            const long guildId = 950_051_001L;
            const long officerId = 950_051_002L;
            GuildBonusesCache.Clear(guildId);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId("birch_log", out var wood));
                Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId("copper_ore", out var ore));
                db.GuildRecords.Add(new GuildRecord { Id = guildId, Name = "BuffCacheGuild950051001" });
                db.PlayerRecords.Add(new PlayerRecord { Id = officerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = guildId });
                db.GuildMembers.Add(new GuildMember { GuildId = guildId, PlayerId = officerId, Role = 2 });
                db.GuildDepotBalances.Add(new GuildDepotBalance { GuildId = guildId, ItemDefinitionId = wood.Id, Quantity = 25_000 });
                db.GuildDepotBalances.Add(new GuildDepotBalance { GuildId = guildId, ItemDefinitionId = ore.Id, Quantity = 25_000 });
                await db.SaveChangesAsync();
            }

            try
            {
                Assert.Equal(0, GuildBonusesCache.GetBuffTier(guildId, "Gold"));

                var engine = new GuildContributionEngine(_fixture.ServiceProvider);
                Assert.True(await engine.ActivateGuildBuffAsync(officerId, guildId, "Gold", 1, "common"));

                Assert.Equal(1, GuildBonusesCache.GetBuffTier(guildId, "Gold"));
            }
            finally
            {
                GuildBonusesCache.Clear(guildId);
            }
        }

        [Fact]
        public async Task StartUpLoadsEveryUnexpiredBuffAndNoExpiredOne()
        {
            const long liveGuild = 950_051_011L;
            const long lapsedGuild = 950_051_012L;
            GuildBonusesCache.Clear(liveGuild);
            GuildBonusesCache.Clear(lapsedGuild);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.GuildActiveBuffs.Add(new GuildActiveBuff { GuildId = liveGuild, BuffType = "Damage", Tier = 3, ExpiresAt = DateTime.UtcNow.AddHours(2) });
                db.GuildActiveBuffs.Add(new GuildActiveBuff { GuildId = lapsedGuild, BuffType = "Damage", Tier = 4, ExpiresAt = DateTime.UtcNow.AddHours(-1) });
                await db.SaveChangesAsync();
            }

            try
            {
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    await GuildBonusesCache.LoadAllAsync(db);
                }

                Assert.Equal(3, GuildBonusesCache.GetBuffTier(liveGuild, "Damage"));
                Assert.Equal(0, GuildBonusesCache.GetBuffTier(lapsedGuild, "Damage"));
            }
            finally
            {
                GuildBonusesCache.Clear(liveGuild);
                GuildBonusesCache.Clear(lapsedGuild);
            }
        }

        [Fact]
        public void ABuffLapsesAtItsExpiryWithoutAReload()
        {
            const long guildId = 950_051_021L;
            try
            {
                GuildBonusesCache.Apply(guildId, "Exp", 2, DateTime.UtcNow.AddMinutes(5));
                Assert.Equal(2, GuildBonusesCache.GetBuffTier(guildId, "Exp"));

                GuildBonusesCache.Apply(guildId, "Exp", 2, DateTime.UtcNow.AddSeconds(-1));
                Assert.Equal(0, GuildBonusesCache.GetBuffTier(guildId, "Exp"));
            }
            finally
            {
                GuildBonusesCache.Clear(guildId);
            }
        }
    }
}

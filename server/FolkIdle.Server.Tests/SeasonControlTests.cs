using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: THE OWNER DECIDES WHEN A SEASON ENDS (2026-09-28).
    //
    // A paused season must survive its own end date, an unpaused one must still
    // roll over as before, and the admin's "end now" must roll over even a
    // paused one - and hand the pause on to the season it starts.
    //
    // Own container, not the shared fixture: the rollover wipes EVERY player's
    // gold and gear (see Test_SeasonalRotation_ClearsEquippedItemIds...), which
    // would corrupt other tests' data in the shared collection.
    public class SeasonControlTests
    {
        [Fact]
        public async Task PauseHoldsTheSeason_ResumeLetsItEnd_EndNowOverridesAPause()
        {
            await using var container = new PostgreSqlBuilder("postgres:16")
                .WithDatabase("folkidle_test_season_control")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await container.StartAsync();

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options => options.UseNpgsql(container.GetConnectionString()));
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                db.SeasonalEraRecords.Add(new SeasonalEraRecord
                {
                    // Already over - an unpaused season would roll over now.
                    EndTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60,
                    IsActive = true,
                    IsRolloverPaused = true,
                });
                await db.SaveChangesAsync();
            }

            var engine = new SeasonalRotationEngine(provider);
            SeasonalRotationEngine.ClearEndNowForTests();
            try
            {
                // 1. Paused: the end date passes and nothing happens.
                await engine.ExecuteEraCheckAsync(CancellationToken.None);
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var eras = await db.SeasonalEraRecords.AsNoTracking().ToListAsync();
                    Assert.Single(eras);
                    Assert.True(eras[0].IsActive);
                }

                // 2. Resumed: the overdue season rolls over, and the new one is
                //    not paused, because the old one was not.
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var era = await db.SeasonalEraRecords.SingleAsync(e => e.IsActive);
                    era.IsRolloverPaused = false;
                    await db.SaveChangesAsync();
                }
                await engine.ExecuteEraCheckAsync(CancellationToken.None);
                int secondEraId;
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var active = await db.SeasonalEraRecords.AsNoTracking().SingleAsync(e => e.IsActive);
                    Assert.Equal(2, await db.SeasonalEraRecords.CountAsync());
                    Assert.False(active.IsRolloverPaused);
                    Assert.True(active.EndTimestamp > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    secondEraId = active.EraId;
                }

                // 3. Paused again, far from its end - and the admin ends it now.
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var era = await db.SeasonalEraRecords.SingleAsync(e => e.IsActive);
                    era.IsRolloverPaused = true;
                    await db.SaveChangesAsync();
                }
                SeasonalRotationEngine.RequestEndNow();
                Assert.True(SeasonalRotationEngine.EndNowPending);
                await engine.ExecuteEraCheckAsync(CancellationToken.None);
                Assert.False(SeasonalRotationEngine.EndNowPending);
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var active = await db.SeasonalEraRecords.AsNoTracking().SingleAsync(e => e.IsActive);
                    Assert.NotEqual(secondEraId, active.EraId);
                    Assert.True(active.IsRolloverPaused, "a hand-ended paused season must start a paused one");
                }
            }
            finally
            {
                SeasonalRotationEngine.ClearEndNowForTests();
            }
        }
    }
}

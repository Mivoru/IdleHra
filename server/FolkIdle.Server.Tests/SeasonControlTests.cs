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
    // A season must survive its own end date, paused OR NOT - since task 88 the
    // calendar ends nobody's run - and the admin's "end now" must still roll
    // over even a paused one, and hand the pause on to the season it starts.
    //
    // Own container, not the shared fixture: the rollover wipes EVERY player's
    // gold and gear (see Test_SeasonalRotation_ClearsEquippedItemIds...), which
    // would corrupt other tests' data in the shared collection.
    public class SeasonControlTests
    {
        [Fact]
        public async Task TheCalendarNeverEndsASeason_EndNowStillDoes()
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

                // 2. Resumed, and still overdue: NOTHING happens either (task
                //    88, owner decision 2026-09-30). The calendar no longer ends
                //    a season - a player ends their own run with a rebirth - so
                //    an unpaused era past its date must survive too. This is
                //    the half that protects the live era, due 2026-11-02.
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
                    Assert.Equal(1, await db.SeasonalEraRecords.CountAsync());
                    Assert.True(active.EndTimestamp < DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "the era is overdue and still standing");
                    secondEraId = active.EraId;
                }

                // 3. Paused again - and the admin ends it now, by hand.
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

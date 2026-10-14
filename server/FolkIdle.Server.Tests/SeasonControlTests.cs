using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    // Modul: THERE ARE NO SEASONS (owner 2026-10-14).
    //
    // The calendar stopped ending a season on 2026-09-30 (task 88), and the
    // admin's typed "END SEASON" - the last way to roll every account over at
    // once - was removed with the placement rewards only it paid. A run ends
    // with its own player's rebirth. What is left of the era is one record the
    // shard ledgers are keyed on, and these tests pin both halves: the loop
    // keeps exactly one era alive and never ends it, and no code path can
    // reset more than one player.
    public class SeasonControlTests
    {
        [Fact]
        public async Task AnOverdueEraIsNeverEnded_AndAMissingOneIsCreated()
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
            }

            var engine = new SeasonalRotationEngine(provider);

            // 1. No era: one is created, so a rebirth has a ledger to pay into.
            await engine.ExecuteEraCheckAsync(CancellationToken.None);
            int eraId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var era = await db.SeasonalEraRecords.AsNoTracking().SingleAsync();
                Assert.True(era.IsActive);
                eraId = era.EraId;
            }

            // 2. Overdue and unpaused - the state that used to wipe every
            //    account - and nothing happens.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var era = await db.SeasonalEraRecords.SingleAsync();
                era.EndTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
                era.IsRolloverPaused = false;
                await db.SaveChangesAsync();
            }
            await engine.ExecuteEraCheckAsync(CancellationToken.None);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var era = await db.SeasonalEraRecords.AsNoTracking().SingleAsync();
                Assert.Equal(eraId, era.EraId);
                Assert.True(era.IsActive, "an overdue era must stand - nothing ends one any more");
            }
        }

        [Fact]
        public void NoCodePathCanResetTheWholeRoster()
        {
            string engine = File.ReadAllText(Path.Combine(ServerRoot(), "Engine", "SeasonalRotationEngine.cs"));
            Assert.DoesNotContain("TRUNCATE TABLE", engine);
            Assert.Matches(new Regex(@"ResetPlayersAsync\(\s*FolkIdleDbContext db,\s*long onlyPlayerId"), engine);
            Assert.Matches(new Regex(@"AwardLegacyShardsAsync\(\s*FolkIdleDbContext db,\s*int eraId,\s*long onlyPlayerId"), engine);

            string network = File.ReadAllText(Path.Combine(ServerRoot(), "Network", "NetworkBroadcastSystem.cs"));
            Assert.DoesNotContain("\"/api/v1/admin/season", network);
        }

        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "could not locate server/FolkIdle.Server from the test binary");
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server");
        }
    }
}

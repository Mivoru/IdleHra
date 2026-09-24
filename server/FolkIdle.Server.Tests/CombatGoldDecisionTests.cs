using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: COMBAT GOLD IS AN OWNER DECISION, NOT AN AUDIT OUTPUT, 2026-09-24.
    ///
    /// GlobalEngineState.GlobalGoldDropMultiplier started every process at 100
    /// and only EcoTelemetryEngine's first out-of-band audit pass dropped it to
    /// 75 - so every restart paid full combat gold for a while, and a database
    /// that happened to balance would have raised it back to 100. The owner
    /// kept 75% on purpose; it is EconomyDecisions.CombatGoldPercent now, and
    /// these facts keep it there. See EconomyDecisions and
    /// docs/superpowers/plans/2026-09-24-task-37-the-deep.md Phase 0 Task 0.1.
    /// </summary>
    [Collection("Postgres collection")]
    public class CombatGoldDecisionTests
    {
        // Canon region-1 regular: any monster works, this one is real content.
        private static int FieldMouseBaseGold()
        {
            ContentRegistry.Initialize();
            var mouse = ContentRegistry.Monsters.ToArray().Single(m => m.Id == ContentRegistry.FirstCanonicalMonsterId);
            Assert.True(mouse.BaseGoldReward > 0);
            return mouse.BaseGoldReward;
        }

        private readonly PostgresTestFixture _fixture;

        public CombatGoldDecisionTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void AFreshProcessPaysSeventyFivePercentBeforeAnyAudit()
        {
            // No audit has run in this assertion's path: the value is a const.
            Assert.Equal(75, EconomyDecisions.CombatGoldPercent);

            int baseGold = FieldMouseBaseGold();
            Assert.Equal(baseGold * 75L / 100L, EconomyDecisions.BaseCombatGold(baseGold));
            Assert.True(EconomyDecisions.BaseCombatGold(baseGold) < baseGold,
                "a fresh process paid full combat gold - the pre-2026-09-24 restart defect");
        }

        [Fact]
        public async Task NoAuditOutcomeMovesCombatGold()
        {
            int baseGold = FieldMouseBaseGold();
            long expected = baseGold * 75L / 100L;
            var engine = new EcoTelemetryEngine(_fixture.ServiceProvider);

            const long playerId = 950_031_001L;
            long guildId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var guild = new GuildRecord { Name = "GoldDecision" + Guid.NewGuid().ToString("N")[..8] };
                db.GuildRecords.Add(guild);
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 1 });
                await db.SaveChangesAsync();
                guildId = guild.Id;
            }

            try
            {
                // (a) INSIDE the parity band: a sink so large that everything
                // else in the shared database is noise, so minted/consumed ~ 1.
                // The old code set the multiplier to 100 here.
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    db.GuildMaterialSinkLedgers.Add(new GuildMaterialSinkLedger { GuildId = guildId, CommodityId = "gold", TotalAmountContributed = 1_000_000_000_000_000L });
                    await db.SaveChangesAsync();
                }
                await engine.ExecuteAuditAsync(CancellationToken.None);
                double insideRatio = await LastRatioAsync();
                Assert.InRange(insideRatio, 0.85, 1.15);
                Assert.Equal(expected, EconomyDecisions.BaseCombatGold(baseGold));

                // (b) OUTSIDE the band: ten times that sink held as gold.
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 10_000_000_000_000_000L });
                    await db.SaveChangesAsync();
                }
                await engine.ExecuteAuditAsync(CancellationToken.None);
                double outsideRatio = await LastRatioAsync();
                Assert.True(outsideRatio > 1.15, $"expected an out-of-band ratio, got {outsideRatio}");
                Assert.Equal(expected, EconomyDecisions.BaseCombatGold(baseGold));
            }
            finally
            {
                // The shared database is summed by every later audit; leave it
                // as it was.
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                db.CommodityRecords.RemoveRange(db.CommodityRecords.Where(c => c.PlayerId == playerId));
                db.GuildMaterialSinkLedgers.RemoveRange(db.GuildMaterialSinkLedgers.Where(s => s.GuildId == guildId));
                db.GuildRecords.RemoveRange(db.GuildRecords.Where(g => g.Id == guildId));
                db.PlayerRecords.RemoveRange(db.PlayerRecords.Where(p => p.Id == playerId));
                await db.SaveChangesAsync();
            }
        }

        [Fact]
        public void NothingAssignsCombatGoldAndBothKillPathsReadTheDecision()
        {
            string root = ServerSourceRoot();
            var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Select(p => (Path: p, Text: File.ReadAllText(p)))
                .ToList();

            var offenders = sources
                .Where(s => s.Text.Contains("GlobalGoldDropMultiplier") || Regex.IsMatch(s.Text, @"GoldDropMultiplier\s*="))
                .Select(s => Path.GetFileName(s.Path))
                .ToList();
            Assert.True(offenders.Count == 0, "combat gold must not be a mutable global again: " + string.Join(", ", offenders));

            string live = sources.Single(s => Path.GetFileName(s.Path) == "SimulationEngine.cs").Text;
            string offline = sources.Single(s => Path.GetFileName(s.Path) == "OfflineSimulationEngine.cs").Text;
            Assert.Contains("EconomyDecisions.BaseCombatGold(activeMonster.BaseGoldReward)", live);
            Assert.Contains("EconomyDecisions.BaseCombatGold(activeMonster.BaseGoldReward)", offline);
        }

        private async Task<double> LastRatioAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.EcoTelemetryLedgers.AsNoTracking()
                .OrderByDescending(l => l.LogId)
                .Select(l => l.CalculatedRatio)
                .FirstAsync();
        }

        private static string ServerSourceRoot()
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

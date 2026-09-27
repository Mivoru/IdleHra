using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 44: (PlayerId, ItemId) is unique on CommodityRecords, and every add
    /// to a stack goes through <see cref="CommodityLedger"/>'s upsert.
    ///
    /// Modul: about thirty sites used to do "SELECT ... FOR UPDATE; if null,
    /// INSERT". FOR UPDATE locks nothing when the row does not exist, so two
    /// transactions could each insert a gold row, and the index did not refuse
    /// the second. The guard below keeps a new check-then-insert from being
    /// written: `new CommodityRecord` and `CommodityRecords.Add(` are allowed
    /// only in the ledger and the two seeders.
    /// </summary>
    [Collection("Postgres collection")]
    public class CommodityLedgerTests
    {
        private readonly PostgresTestFixture _fixture;

        public CommodityLedgerTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private static readonly HashSet<string> AllowedWriters = new(StringComparer.OrdinalIgnoreCase)
        {
            "CommodityLedger.cs",
            "DbSeeder.cs",
            "DevFixtureSeeder.cs",
        };

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

        [Fact]
        public void OnlyTheLedgerAndTheSeedersCreateCommodityRows()
        {
            var construct = new Regex(@"new\s+(?:[\w.]+\.)?CommodityRecord\s*[({]");
            var add = new Regex(@"CommodityRecords\s*\.\s*(?:Add|AddAsync|AddRange|AddRangeAsync)\s*\(");

            var offenders = new List<string>();
            int scanned = 0;
            foreach (string path in Directory.EnumerateFiles(ServerRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/Migrations/")) continue;
                scanned++;
                if (AllowedWriters.Contains(Path.GetFileName(path))) continue;

                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i];
                    int comment = code.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) code = code.Substring(0, comment);
                    if (construct.IsMatch(code) || add.IsMatch(code))
                    {
                        offenders.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }

            Assert.True(scanned > 100, $"scanned only {scanned} files - the source walk is broken");
            Assert.True(offenders.Count == 0,
                "CommodityRecords rows may only be created by CommodityLedger.AddAsync/AddManyAsync " +
                "(or the two seeders). Offenders:\n" + string.Join("\n", offenders));
        }

        private async Task<long> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"ledger_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        [Fact]
        public async Task TwentyParallelAddsForANewItemMakeOneRowWithTheWholeSum()
        {
            long playerId = await CreatePlayerAsync();
            const string itemId = "ledger_race_item";

            // Half of them inside an explicit Read Committed transaction, the
            // way most engines call it; half autocommitted, the way the market
            // settlement rescue does.
            var tasks = Enumerable.Range(1, 20).Select(async i =>
            {
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                if (i % 2 == 0)
                {
                    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                    await CommodityLedger.AddAsync(db, playerId, itemId, i);
                    await tx.CommitAsync();
                }
                else
                {
                    await CommodityLedger.AddAsync(db, playerId, itemId, i);
                }
            }).ToArray();
            await Task.WhenAll(tasks);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await verify.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == itemId).ToListAsync();
            Assert.Single(rows);
            Assert.Equal(Enumerable.Range(1, 20).Sum(), rows[0].Quantity);
        }

        [Fact]
        public async Task TheUniqueIndexRefusesASecondRow()
        {
            long playerId = await CreatePlayerAsync();
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await CommodityLedger.AddAsync(db, playerId, "gold", 5);

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"CommodityRecords\" (\"PlayerId\", \"ItemId\", \"Quantity\") VALUES ({playerId}, 'gold', 7)"));
            Assert.Contains("IX_CommodityRecords_PlayerId_ItemId", ex.ToString());
        }

        [Fact]
        public async Task AddRebasesARowTheContextAlreadyTracks()
        {
            // Modul: the change tracker does not see raw SQL. A caller that read
            // the row, decremented it, and then added to it in the same unit of
            // work must end with BOTH moves, not with SaveChanges writing the
            // stale absolute value over the upsert.
            long playerId = await CreatePlayerAsync();
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await CommodityLedger.AddAsync(seed, playerId, "gold", 100);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var row = await db.CommodityRecords
                    .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = 'gold' FOR UPDATE")
                    .SingleAsync();
                row.Quantity -= 30;

                long after = await CommodityLedger.AddAsync(db, playerId, "gold", 50);
                Assert.Equal(150, after);
                Assert.Equal(120, row.Quantity);

                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var final = await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            Assert.Equal(120, final.Quantity);
        }

        [Fact]
        public async Task AddManyUpsertsABatchAndMergesRepeatedIds()
        {
            long playerId = await CreatePlayerAsync();
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await CommodityLedger.AddAsync(seed, playerId, "ledger_b", 10);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await CommodityLedger.AddManyAsync(db, playerId, new[]
                {
                    new KeyValuePair<string, long>("ledger_a", 3),
                    new KeyValuePair<string, long>("ledger_b", 4),
                    new KeyValuePair<string, long>("ledger_a", 2),
                    new KeyValuePair<string, long>("ledger_skip", 0),
                    new KeyValuePair<string, long>("", 9),
                });
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await verify.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId).ToDictionaryAsync(c => c.ItemId, c => c.Quantity);
            Assert.Equal(2, rows.Count);
            Assert.Equal(5, rows["ledger_a"]);
            Assert.Equal(14, rows["ledger_b"]);
        }
    }
}

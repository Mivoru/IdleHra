using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A Serializable transaction that loses at COMMIT is already complete, and a
    /// plain RollbackAsync in the catch then throws "This NpgsqlTransaction has
    /// completed" - replacing the 40001, so nothing retries or refuses and the
    /// player is kicked (20-bot lockstep run, 2026-11-03).
    /// </summary>
    [Collection("Postgres collection")]
    public class SafeRollbackTests
    {
        private readonly PostgresTestFixture _fixture;

        public SafeRollbackTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task ARollbackAfterACompletedTransactionKeepsTheOriginalException()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await transaction.CommitAsync(); // complete, as a commit that threw 40001 leaves it

            // The defect: the bare rollback throws and would replace the real error.
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());

            var original = new TimeoutException("stands in for the 40001 that failed the commit");
            var thrown = await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                try
                {
                    throw original;
                }
                catch
                {
                    await transaction.SafeRollbackAsync();
                    throw;
                }
            });
            Assert.Same(original, thrown);
        }

        [Fact]
        public async Task ASafeRollbackOfALiveTransactionStillRollsBack()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE safe_rollback_probe (x int)");
            await transaction.SafeRollbackAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("SELECT * FROM safe_rollback_probe"));
        }

        /// <summary>The guard: no bare RollbackAsync creeps back into the server.</summary>
        [Fact]
        public void TheServerRollsBackOnlyThroughSafeRollbackAsync()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FolkIdle.Server"));
            Assert.True(Directory.Exists(root), root);
            var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.EndsWith("TransactionRollbackExtensions.cs", StringComparison.Ordinal))
                .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
                .Where(t => t.line.Contains(".RollbackAsync(", StringComparison.Ordinal) && !t.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Select(t => $"{Path.GetRelativePath(root, t.f)}:{t.i + 1}")
                .ToList();
            Assert.True(offenders.Count == 0, "Use SafeRollbackAsync: " + string.Join(", ", offenders));
        }
    }
}

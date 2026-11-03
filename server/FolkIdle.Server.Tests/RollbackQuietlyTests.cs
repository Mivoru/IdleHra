using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A Serializable transaction that loses at COMMIT is already complete, and
    /// a plain RollbackAsync in the catch then throws "This NpgsqlTransaction has
    /// completed" - replacing the 40001, so nothing retries or refuses and the
    /// player is kicked (20-bot lockstep smoke, 2026-11-03).
    /// </summary>
    [Collection("Postgres collection")]
    public class RollbackQuietlyTests
    {
        private readonly PostgresTestFixture _fixture;

        public RollbackQuietlyTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task ARollbackAfterACompletedTransactionKeepsTheOriginalException()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await transaction.CommitAsync(); // complete, as a commit that threw 40001 leaves it

            // The bug: the bare rollback throws and would replace the real error.
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());

            // The fix: the catch block's rollback is quiet, so the caller's
            // `throw;` rethrows the 40001 and TransientDbConflict sees a conflict.
            var original = new TimeoutException("stands in for the 40001 that failed the commit");
            var thrown = await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                try
                {
                    throw original;
                }
                catch
                {
                    await TransientDbConflict.RollbackQuietlyAsync(transaction);
                    throw;
                }
            });
            Assert.Same(original, thrown);
        }

        [Fact]
        public async Task AQuietRollbackOfALiveTransactionStillRollsBack()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE quiet_rollback_probe (x int)");
            await TransientDbConflict.RollbackQuietlyAsync(transaction);
            // The temp table was created inside the rolled-back transaction.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("SELECT * FROM quiet_rollback_probe"));
        }
    }
}

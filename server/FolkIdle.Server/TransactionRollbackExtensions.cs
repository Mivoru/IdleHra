using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Storage;

namespace FolkIdle.Server
{
    /// <summary>
    /// The one way this codebase rolls a transaction back.
    ///
    /// Modul: A ROLLBACK MUST NEVER REPLACE THE EXCEPTION THAT CAUSED IT
    /// (found by the 20-bot lockstep run, 2026-11-03). A Serializable
    /// transaction that loses at COMMIT throws 40001 from CommitAsync and is then
    /// complete; the catch block's `await transaction.RollbackAsync(); throw;`
    /// threw InvalidOperationException ("This NpgsqlTransaction has completed")
    /// from inside the catch, so the 40001 was gone: TransientDbConflict did not
    /// retry or refuse, and SafeDispatchAsync kicked the player. Thirteen kicks
    /// in one run, and the same path in production. A rollback that fails
    /// because the connection died is just as bad a replacement for the real
    /// error, and the transaction is discarded server-side anyway.
    ///
    /// So: a completed transaction is left alone; a database error while
    /// rolling back is logged and swallowed; cancellation still propagates. On
    /// a live transaction this is exactly RollbackAsync.
    /// </summary>
    public static class TransactionRollbackExtensions
    {
        public static async Task SafeRollbackAsync(this IDbContextTransaction transaction, CancellationToken cancellationToken = default)
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already completed (a commit that failed): nothing to undo.
            }
            catch (DbException ex)
            {
                Console.WriteLine($"SafeRollbackAsync: rollback failed and was swallowed so the original error survives: {ex.Message}");
            }
        }

        public static async Task SafeRollbackAsync(this DbTransaction transaction, CancellationToken cancellationToken = default)
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (DbException ex)
            {
                Console.WriteLine($"SafeRollbackAsync: rollback failed and was swallowed so the original error survives: {ex.Message}");
            }
        }
    }
}

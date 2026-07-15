using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    // Modul: shared retry-configured DbContextOptions (EnableRetryOnFailure,
    // Serializable-conflict-aware) used by every engine that opens its own
    // explicit Database.BeginTransactionAsync and needs that transaction to
    // survive a Postgres serialization_failure (SQLSTATE 40001) or
    // deadlock_detected (40P01) instead of throwing it straight at the
    // caller. Deliberately a separate options instance from the one most
    // engines resolve via IDbContextFactory<FolkIdleDbContext> - EF Core
    // requires any explicit transaction to be wrapped in
    // Database.CreateExecutionStrategy().ExecuteAsync(...) once a retrying
    // execution strategy is registered on a context's options, and the
    // engines that do not need Serializable-conflict retry (the majority of
    // the codebase) are left on the plain shared factory so this change
    // does not alter their behavior at all.
    //
    // Originally scoped to AuthenticationEngine.LoginOrProvisionAsync only;
    // now shared by StateCheckpointManager, ColdRecoveryCoordinator,
    // AchievementEngine, and CraftingEngine as well, since all of them open
    // Serializable transactions on the write/checkpoint hot path and were
    // found to have the exact same unretried-conflict exposure the
    // auto-provisioning path had before this class existed.
    public sealed class RetryingDbContextOptions
    {
        public DbContextOptions<FolkIdleDbContext> Options { get; }

        public RetryingDbContextOptions(DbContextOptions<FolkIdleDbContext> options)
        {
            Options = options;
        }
    }
}

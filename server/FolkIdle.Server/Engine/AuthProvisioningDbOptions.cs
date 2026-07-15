using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    // Modul: retry-configured DbContextOptions used only by
    // AuthenticationEngine.LoginOrProvisionAsync's auto-provisioning
    // transaction - deliberately a separate options instance from the one
    // every other engine resolves via IDbContextFactory<FolkIdleDbContext>.
    // EF Core requires any explicit Database.BeginTransactionAsync call to
    // be wrapped in Database.CreateExecutionStrategy().ExecuteAsync(...)
    // once a retrying execution strategy is registered on a context's
    // options - dozens of other engines in this codebase open their own
    // explicit transactions and rewriting all of them is out of scope for
    // this task. Scoping the retry strategy to this dedicated options
    // instance gives the auto-provisioning path Serializable-failure retry
    // without touching how any other engine's transactions behave.
    public sealed class AuthProvisioningDbOptions
    {
        public DbContextOptions<FolkIdleDbContext> Options { get; }

        public AuthProvisioningDbOptions(DbContextOptions<FolkIdleDbContext> options)
        {
            Options = options;
        }
    }
}

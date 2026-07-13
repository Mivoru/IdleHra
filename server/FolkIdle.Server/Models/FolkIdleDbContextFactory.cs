using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FolkIdle.Server.Models
{
    public class FolkIdleDbContextFactory : IDesignTimeDbContextFactory<FolkIdleDbContext>
    {
        public FolkIdleDbContext CreateDbContext(string[] args)
        {
            string connectionString = Environment.GetEnvironmentVariable("FOLKIDLE_DB_CONN")
                ?? ConnectionStringDefaults.LocalDevelopmentFallback;

            var optionsBuilder = new DbContextOptionsBuilder<FolkIdleDbContext>();
            optionsBuilder.UseNpgsql(connectionString);

            return new FolkIdleDbContext(optionsBuilder.Options);
        }
    }
}

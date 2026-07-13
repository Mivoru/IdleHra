using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FolkIdle.Server.Models
{
    public class FolkIdleDbContextFactory : IDesignTimeDbContextFactory<FolkIdleDbContext>
    {
        public FolkIdleDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<FolkIdleDbContext>();
            optionsBuilder.UseNpgsql("Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres");

            return new FolkIdleDbContext(optionsBuilder.Options);
        }
    }
}

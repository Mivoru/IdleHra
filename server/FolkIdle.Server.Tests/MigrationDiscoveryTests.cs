using System;
using System.Linq;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: migration discovery, 2026-08-05.
    //
    // A MIGRATION CLASS THAT COMPILES IS NOT A MIGRATION THAT RUNS. EF finds
    // them by scanning the assembly for MigrationAttribute and keeping the ones
    // whose DbContextAttribute names this context - both of which live in the
    // generated .Designer.cs, so a hand-written migration without that file is
    // invisible. Nothing complains: the type is there, the SQL is there, the
    // server logs "Database migrations applied successfully" and applies none
    // of it.
    //
    // That is exactly what happened to FoldStashIntoCommodities. It shipped,
    // deployed, and never moved a single row; the live database's history table
    // simply had no entry for it. It was caught by reading that table by hand,
    // which is not a thing anyone does on schedule.
    //
    // Needs no database - GetMigrations reads the assembly, not the connection.
    public class MigrationDiscoveryTests
    {
        [Fact]
        public void Test_Migrations_EveryMigrationTypeIsDiscoverableByEf()
        {
            var options = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql("Host=discovery-check-does-not-connect;Database=none")
                .Options;

            using var db = new FolkIdleDbContext(options);
            var discovered = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);

            var declared = typeof(FolkIdleDbContext).Assembly
                .GetTypes()
                .Where(t => !t.IsAbstract && typeof(Migration).IsAssignableFrom(t))
                .Select(t => t.Name)
                .ToList();

            Assert.NotEmpty(declared);

            // Discovered ids are timestamp-prefixed ("20260805190000_Name"); the
            // class carries only the name, so match on the suffix.
            var missing = declared
                .Where(name => !discovered.Any(id =>
                    id.EndsWith("_" + name, StringComparison.Ordinal)))
                .ToList();

            Assert.True(
                missing.Count == 0,
                "Migration classes EF cannot see, so they will never run: " +
                string.Join(", ", missing) +
                ". Each needs [DbContext(typeof(FolkIdleDbContext))] and " +
                "[Migration(\"<id>_<Name>\")], normally supplied by its " +
                "generated .Designer.cs.");
        }
    }
}

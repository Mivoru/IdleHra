using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Modul: THE DIAMOND REWORK (owner M1, I1, A2 - 2026-10-31). NOT ADDITIVE:
    /// it rewrites live rows, so a database backup is taken before the deploy
    /// that carries it. No schema change - only data:
    ///   - Treasury and Master Smith's stored tiers move to their thresholds'
    ///     indexes on the new, longer ladders (nothing paid twice, nothing lost);
    ///   - every inheritance stat becomes ceil(old / 2) of the new +4% levels.
    /// See Engine.DiamondReworkSql, which DiamondReworkTests runs.
    /// Down cannot restore the old levels exactly; the backup is the way back.
    /// </summary>
    [DbContext(typeof(FolkIdleDbContext))]
    [Migration("20261031030000_DiamondRework")]
    public partial class DiamondRework : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Engine.DiamondReworkSql.RemapTreasury);
            migrationBuilder.Sql(Engine.DiamondReworkSql.RemapForging);
            migrationBuilder.Sql(Engine.DiamondReworkSql.HalveInheritance);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

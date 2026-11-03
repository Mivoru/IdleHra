using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Modul: FOUR GUILD RANKS (owner, 2026-11-03). NOT ADDITIVE - it rewrites
    /// GuildMembers.Role, so take a backup before the deploy that carries it.
    /// The ranks were 0 Member, 1 Officer, 2 Leader; they are 0 Member,
    /// 1 Warden, 2 Elder, 3 Chieftain (GuildManagementEngine). Officer keeps its
    /// number as Warden, so the only rows that move are the Leaders, 2 -> 3:
    /// nobody loses a right, and Elder starts empty.
    /// Down folds Elder back into Officer (2 -> 1) and Chieftain into Leader
    /// (3 -> 2) - the old model had no deputy rank to keep.
    /// </summary>
    [DbContext(typeof(FolkIdleDbContext))]
    [Migration("20261103120000_GuildFourRanks")]
    public partial class GuildFourRanks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"GuildMembers\" SET \"Role\" = 3 WHERE \"Role\" = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"GuildMembers\" SET \"Role\" = 1 WHERE \"Role\" = 2;");
            migrationBuilder.Sql("UPDATE \"GuildMembers\" SET \"Role\" = 2 WHERE \"Role\" = 3;");
        }
    }
}

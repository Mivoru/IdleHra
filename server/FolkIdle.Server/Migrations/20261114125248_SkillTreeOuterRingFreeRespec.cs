using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class SkillTreeOuterRingFreeRespec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Modul: THE OUTER RING CHANGED EVERYONE'S TREE (skill tree v3 step D,
            // owner 2026-11-14: a free respec for everyone at the switch), so
            // everyone gets a free choice of the new one - as the War switch did
            // on its load path (ReconcileWarSwitchAsync). Data only; no schema.
            migrationBuilder.Sql("UPDATE \"PlayerRecords\" SET \"FreeRespecUsed\" = false;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: who had already used their free respec is not kept.
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class DropMentorship : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Modul: RAW SQL WITH IF EXISTS for every one of these, not
            // DropTable.
            //
            // The scaffolded DropTable("MentorshipContract") failed outright on
            // a database that never had the table - and both spellings are in
            // circulation, because the entity's inferred name and the [Table]
            // attribute disagreed at different points in this project's
            // history. A migration whose whole purpose is to make something
            // absent has no business failing because it is already absent.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MentorshipContract\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MentorshipContracts\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MentorshipAcademyAssignments\";");
        }

        /// <inheritdoc />
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Re-creating the tables would restore two
            // empty shells for a feature whose code is deleted, which is not a
            // rollback of anything - it is litter.
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_grants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: false),
                    PayloadKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtEpochMs = table.Column<long>(type: "bigint", nullable: false),
                    NextAttemptAtEpochMs = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DeadLetteredAtEpochMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_grants", x => x.Id);
                });

            // Backs the idempotency key (PlayerId, SourceType, SourceSequence)
            // - PendingGrantOutbox's ON CONFLICT DO NOTHING needs this exact
            // unique constraint to target.
            migrationBuilder.CreateIndex(
                name: "IX_pending_grants_idempotency",
                table: "pending_grants",
                columns: new[] { "PlayerId", "SourceType", "SourceSequence" },
                unique: true);

            // Partial index: only rows the drain worker will ever query for
            // are indexed, so a table full of permanent dead-letters (rare,
            // but the whole point of keeping them) never slows the query
            // that matters.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_pending_grants_eligible\" ON pending_grants (\"NextAttemptAtEpochMs\") " +
                "WHERE \"DeadLetteredAtEpochMs\" IS NULL;");

            migrationBuilder.Sql("CREATE SEQUENCE pending_grant_source_seq;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS pending_grant_source_seq;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_pending_grants_eligible\";");
            migrationBuilder.DropTable(
                name: "pending_grants");
        }
    }
}

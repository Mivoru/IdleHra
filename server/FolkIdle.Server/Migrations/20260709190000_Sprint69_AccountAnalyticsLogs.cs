using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint69_AccountAnalyticsLogs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FencingToken",
                table: "GuildMatchmakingSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "AccountAnalyticsLogs",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<System.Guid>(type: "uuid", nullable: false),
                    EventTypeHash = table.Column<long>(type: "bigint", nullable: false),
                    TimestampEpoch = table.Column<long>(type: "bigint", nullable: false),
                    PayloadMetric = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAnalyticsLogs", x => x.LogId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalyticsLogs_AccountId_TimestampEpoch",
                table: "AccountAnalyticsLogs",
                columns: new[] { "AccountId", "TimestampEpoch" })
                .Annotation("Npgsql:IndexMethod", "btree");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AccountAnalyticsLogs");
            migrationBuilder.DropColumn(name: "FencingToken", table: "GuildMatchmakingSnapshots");
        }
    }
}

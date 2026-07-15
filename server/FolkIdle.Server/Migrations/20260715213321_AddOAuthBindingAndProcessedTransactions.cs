using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthBindingAndProcessedTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalProviderId",
                table: "PlayerRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProviderType",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ProcessedTransactions",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PremiumDiamondsGranted = table.Column<int>(type: "integer", nullable: false),
                    ProcessedAtEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedTransactions", x => x.TransactionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRecords_ProviderType_ExternalProviderId",
                table: "PlayerRecords",
                columns: new[] { "ProviderType", "ExternalProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedTransactions_PlayerId",
                table: "ProcessedTransactions",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedTransactions");

            migrationBuilder.DropIndex(
                name: "IX_PlayerRecords_ProviderType_ExternalProviderId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ExternalProviderId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ProviderType",
                table: "PlayerRecords");
        }
    }
}

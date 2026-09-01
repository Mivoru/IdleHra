using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LowPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    HighPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    SenderPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    RecipientPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    MessageText = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SentAtEpochMs = table.Column<long>(type: "bigint", nullable: false),
                    ReadAtEpochMs = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_LowPlayerId_HighPlayerId_SentAtEpochMs",
                table: "conversation_messages",
                columns: new[] { "LowPlayerId", "HighPlayerId", "SentAtEpochMs" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_RecipientPlayerId_ReadAtEpochMs",
                table: "conversation_messages",
                columns: new[] { "RecipientPlayerId", "ReadAtEpochMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_messages");
        }
    }
}

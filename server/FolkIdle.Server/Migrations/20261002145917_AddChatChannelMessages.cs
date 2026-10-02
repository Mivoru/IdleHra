using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddChatChannelMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_channel_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelType = table.Column<byte>(type: "smallint", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    SenderPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    MessageText = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SentAtEpochMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_channel_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_messages_ChannelType_GuildId_SentAtEpochMs",
                table: "chat_channel_messages",
                columns: new[] { "ChannelType", "GuildId", "SentAtEpochMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_channel_messages");
        }
    }
}

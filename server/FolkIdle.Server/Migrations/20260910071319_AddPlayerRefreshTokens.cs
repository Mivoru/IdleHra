using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    IssuedEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAtEpoch = table.Column<long>(type: "bigint", nullable: false),
                    RevokedEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRefreshTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRefreshTokens_AccountId",
                table: "PlayerRefreshTokens",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRefreshTokens_TokenHash",
                table: "PlayerRefreshTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerRefreshTokens");
        }
    }
}

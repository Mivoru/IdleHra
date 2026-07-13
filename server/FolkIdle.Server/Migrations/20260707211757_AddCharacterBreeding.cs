using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterBreeding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankEquipmentInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    QualityTier = table.Column<int>(type: "integer", nullable: false),
                    AffixPayload = table.Column<string>(type: "jsonb", nullable: false),
                    IsAffixLocked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankEquipmentInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EquipmentInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    QualityTier = table.Column<int>(type: "integer", nullable: false),
                    AffixPayload = table.Column<string>(type: "jsonb", nullable: false),
                    IsAffixLocked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "characters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    AgePhase = table.Column<int>(type: "integer", nullable: false),
                    IsLockedInEscrow = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_characters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailboxInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    BaseItemId = table.Column<string>(type: "text", nullable: false),
                    QualityTier = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    IsClaimed = table.Column<bool>(type: "boolean", nullable: false),
                    IsPending = table.Column<bool>(type: "boolean", nullable: false),
                    GoldAttachment = table.Column<long>(type: "bigint", nullable: false),
                    AttachedEquipmentId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "character_lineage_registry",
                columns: table => new
                {
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentPaternalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentMaternalId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationIndex = table.Column<int>(type: "integer", nullable: false),
                    GeneticVector = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_lineage_registry", x => x.CharacterId);
                    table.ForeignKey(
                        name: "FK_character_lineage_registry_characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankEquipmentInstances");

            migrationBuilder.DropTable(
                name: "EquipmentInstances");

            migrationBuilder.DropTable(
                name: "character_lineage_registry");

            migrationBuilder.DropTable(
                name: "MailboxInstances");

            migrationBuilder.DropTable(
                name: "characters");
        }
    }
}

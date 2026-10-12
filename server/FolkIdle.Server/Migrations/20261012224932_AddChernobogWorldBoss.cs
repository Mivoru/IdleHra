using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddChernobogWorldBoss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DefeatedAtEpoch",
                table: "WorldBossSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<double>(
                name: "HpFactor",
                table: "WorldBossSnapshots",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<byte>(
                name: "HpRevealed",
                table: "WorldBossSnapshots",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<long>(
                name: "RevealEpoch",
                table: "WorldBossSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "LoginCyclesCompleted",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsRegistered",
                table: "player_world_boss_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LaterPlayed",
                table: "player_world_boss_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "LaterSkillSum",
                table: "player_world_boss_attempts",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "MilestonesPaid",
                table: "player_world_boss_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RegistrationDamage",
                table: "player_world_boss_attempts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "RegistrationPlayed",
                table: "player_world_boss_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "RegistrationSkillSum",
                table: "player_world_boss_attempts",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "RegistrationStrikes",
                table: "player_world_boss_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "world_boss_profiles",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    ProvenSkillAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_world_boss_profiles", x => x.PlayerId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "world_boss_profiles");

            migrationBuilder.DropColumn(
                name: "DefeatedAtEpoch",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "HpFactor",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "HpRevealed",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "RevealEpoch",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "LoginCyclesCompleted",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "IsRegistered",
                table: "player_world_boss_attempts");

            migrationBuilder.DropColumn(
                name: "LaterPlayed",
                table: "player_world_boss_attempts");

            migrationBuilder.DropColumn(
                name: "LaterSkillSum",
                table: "player_world_boss_attempts");

            migrationBuilder.DropColumn(
                name: "MilestonesPaid",
                table: "player_world_boss_attempts");

            migrationBuilder.DropColumn(
                name: "RegistrationDamage",
                table: "player_world_boss_attempts");

            migrationBuilder.DropColumn(
                name: "RegistrationPlayed",
                table: "player_world_boss_attempts");

            migrationBuilder.DropColumn(
                name: "RegistrationSkillSum",
                table: "player_world_boss_attempts");

            migrationBuilder.DropColumn(
                name: "RegistrationStrikes",
                table: "player_world_boss_attempts");
        }
    }
}

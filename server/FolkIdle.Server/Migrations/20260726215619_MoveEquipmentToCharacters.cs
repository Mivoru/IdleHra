using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class MoveEquipmentToCharacters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Modul: per-character equipment. EF scaffolded the three
            // DropColumn calls FIRST, which would have destroyed every existing
            // player's equipped gear before there was anywhere to put it. The
            // order here is deliberate and load-bearing: add the new columns,
            // carry the data across, and only then drop the old ones.

            migrationBuilder.AddColumn<long>(
                name: "EquippedBootsId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedChestId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedGlovesId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedHelmetId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedLeggingsId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedWeaponId",
                table: "characters",
                type: "bigint",
                nullable: true);

            // Carry each player's existing gear onto their MAIN character - the
            // one whose "Id" equals "PlayerRecords"."PlayerGuid", which is the
            // character StateCheckpointManager hydrates into slot 1 and the one
            // the player was effectively wearing this gear on all along.
            //
            // The old single "EquippedArmorId" slot becomes "EquippedChestId":
            // it was the generic armour slot and chest is where the generic
            // "_armor_slot_" fallback still resolves (see
            // EquipmentSlotEngine.ResolveSlotIndex), so nobody's breastplate
            // moves or vanishes. Helmet, gloves and boots start empty because
            // no player could ever have equipped one - there was no slot.
            migrationBuilder.Sql(@"
                UPDATE ""characters"" AS c
                SET ""EquippedWeaponId"" = p.""EquippedWeaponId"",
                    ""EquippedChestId"" = p.""EquippedArmorId"",
                    ""EquippedLeggingsId"" = p.""EquippedLeggingsId""
                FROM ""PlayerRecords"" AS p
                WHERE c.""Id"" = p.""PlayerGuid"";
            ");

            migrationBuilder.DropColumn(
                name: "EquippedArmorId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EquippedLeggingsId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EquippedWeaponId",
                table: "PlayerRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse carry-over, so a rollback does not strand every
            // player's gear on a column that is about to be dropped.
            migrationBuilder.Sql(@"
                UPDATE ""PlayerRecords"" AS p
                SET ""EquippedWeaponId"" = c.""EquippedWeaponId"",
                    ""EquippedArmorId"" = c.""EquippedChestId"",
                    ""EquippedLeggingsId"" = c.""EquippedLeggingsId""
                FROM ""characters"" AS c
                WHERE c.""Id"" = p.""PlayerGuid"";
            ");

            migrationBuilder.DropColumn(
                name: "EquippedBootsId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedChestId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedGlovesId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedHelmetId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedLeggingsId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedWeaponId",
                table: "characters");

            migrationBuilder.AddColumn<long>(
                name: "EquippedArmorId",
                table: "PlayerRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedLeggingsId",
                table: "PlayerRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedWeaponId",
                table: "PlayerRecords",
                type: "bigint",
                nullable: true);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// The offhand slot goes; Amulet and Ring arrive.
    ///
    /// See EquipmentSlotEngine for why: an offhand was never part of the design
    /// - the slots are weapon, five armour pieces, an amulet and a ring - and
    /// the five helper items were invented to fill a slot that was itself
    /// invented. Amulet and Ring were the two genuinely missing: one of each per
    /// tier has been in the catalogue all along, resolving to no slot.
    ///
    /// EF SCAFFOLDED THIS AS A RENAME of EquippedOffhandId to EquippedRingId,
    /// and that is hand-written away deliberately. A rename keeps the column's
    /// data, so every character wearing a buckler would come back wearing it as
    /// a RING - a shield on a finger, holding the ring slot against the
    /// jewellery the player is meant to go and find. Worse, the helper items are
    /// deleted from the catalogue in the same pass, so the id would point at an
    /// item that no longer exists.
    ///
    /// Dropping is the honest operation. Nobody keeps a slot that no longer
    /// exists, and the pieces themselves stay in EquipmentInstances until the
    /// catalogue cleanup removes them, so nothing is silently destroyed here.
    /// </summary>
    public partial class ReplaceOffhandWithAmuletAndRing : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EquippedOffhandId",
                table: "characters");

            migrationBuilder.AddColumn<long>(
                name: "EquippedAmuletId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedRingId",
                table: "characters",
                type: "bigint",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EquippedAmuletId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedRingId",
                table: "characters");

            // Comes back empty, which is the truthful inverse: the Up dropped
            // that data and this cannot invent it.
            migrationBuilder.AddColumn<long>(
                name: "EquippedOffhandId",
                table: "characters",
                type: "bigint",
                nullable: true);
        }
    }
}

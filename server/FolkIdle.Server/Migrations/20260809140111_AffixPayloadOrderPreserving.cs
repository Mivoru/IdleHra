using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Modul: jsonb DOES NOT PRESERVE KEY ORDER, and this payload is addressed
    /// BY POSITION.
    ///
    /// The reroll command names an affix by its index, and AffixRerollEngine
    /// rebuilds the object with the new key substituted at the old key's place
    /// so that index still means the same affix afterwards. jsonb discarded
    /// that on every write - it stores object keys sorted by length, then
    /// bytes. Rerolling changes an affix's key, which changes its length,
    /// which moved it somewhere else in the list; a short new key landed it in
    /// the FIRST slot. Reported as "the affixes jump to the first slot".
    ///
    /// jsonb -> json is a plain type change and loses nothing that is still
    /// there. It cannot RESTORE anything either: every payload written before
    /// this was already canonicalised on the way in, so existing items keep
    /// their length-sorted order. From here the order is whatever wrote it.
    ///
    /// Nothing queries inside this column in SQL - the one `Contains` check
    /// runs on a materialised entity - so the indexing jsonb buys is worth
    /// nothing here and the ordering it costs is load-bearing.
    /// </summary>
    public partial class AffixPayloadOrderPreserving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AffixPayload",
                table: "MarketEquipmentInstances",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "AffixPayload",
                table: "EquipmentInstances",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "AffixPayload",
                table: "MarketEquipmentInstances",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json");

            migrationBuilder.AlterColumn<string>(
                name: "AffixPayload",
                table: "EquipmentInstances",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json");
        }
    }
}

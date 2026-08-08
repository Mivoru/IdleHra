using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Folds the Bank back into EquipmentInstances and drops it.
    ///
    /// THE BANK WAS A CURE FOR A DISEASE THE GAME NO LONGER HAS. It is a
    /// hundred-slot equipment store, and it existed to relieve a backpack cap -
    /// but that cap is gone (see CombatLootEngine: "this table is the chest's
    /// equipment half. It is unbounded now"), so the Bank had stopped solving
    /// anything.
    ///
    /// Worse, it actively hurt. EquipmentSlotEngine, forge fusion, affix reroll
    /// and every market listing all read EquipmentInstances, so an item sitting
    /// in the Bank could not be worn, upgraded, rerolled or sold. Depositing
    /// was a way to make your own gear inert, and nothing on any screen said
    /// so.
    ///
    /// No player could have put anything there through the web client - the
    /// deposit and withdraw commands had no UI at all - but a stale bundle
    /// could, and any row that predates the web client is real gear. So the
    /// rows MOVE rather than going with the table: a migration that silently
    /// deletes somebody's equipment is the one kind of data loss nobody
    /// forgives.
    /// </summary>
    public partial class RetireTheBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ids are identity columns on both sides, so the destination
            // assigns its own - and nothing references a Bank row id once the
            // withdraw command is gone.
            migrationBuilder.Sql(@"
                INSERT INTO ""EquipmentInstances""
                    (""PlayerId"", ""BaseItemId"", ""QualityTier"", ""AffixPayload"", ""IsAffixLocked"")
                SELECT ""PlayerId"", ""BaseItemId"", ""QualityTier"", ""AffixPayload"", ""IsAffixLocked""
                FROM ""BankEquipmentInstances"";");

            migrationBuilder.DropTable(
                name: "BankEquipmentInstances");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankEquipmentInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AffixPayload = table.Column<string>(type: "jsonb", nullable: false),
                    BaseItemId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsAffixLocked = table.Column<bool>(type: "boolean", nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    QualityTier = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankEquipmentInstances", x => x.Id);
                });
        }
    }
}

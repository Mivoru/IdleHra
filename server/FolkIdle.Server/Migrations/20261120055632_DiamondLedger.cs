using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class DiamondLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "diamond_daily",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Gained = table.Column<long>(type: "bigint", nullable: false),
                    Spent = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diamond_daily", x => new { x.PlayerId, x.Day, x.Reason });
                });

            // Modul: the ledger is written by the database, not by the writers
            // (Models.DiamondDaily). Every change of PlayerRecords.PremiumDiamonds
            // lands here, labelled by Engine.DiamondLedger or "unlabelled".
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION folkidle_record_diamond_change() RETURNS trigger AS $$
DECLARE
    delta bigint := NEW.""PremiumDiamonds""::bigint - OLD.""PremiumDiamonds""::bigint;
    reason varchar(32) := left(coalesce(nullif(current_setting('folkidle.diamond_reason', true), ''), 'unlabelled'), 32);
BEGIN
    INSERT INTO diamond_daily (""PlayerId"", ""Day"", ""Reason"", ""Gained"", ""Spent"")
    VALUES (NEW.""Id"", (now() AT TIME ZONE 'utc')::date, reason, greatest(delta, 0), greatest(-delta, 0))
    ON CONFLICT (""PlayerId"", ""Day"", ""Reason"")
    DO UPDATE SET ""Gained"" = diamond_daily.""Gained"" + EXCLUDED.""Gained"",
                  ""Spent"" = diamond_daily.""Spent"" + EXCLUDED.""Spent"";
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_player_diamond_change
AFTER UPDATE OF ""PremiumDiamonds"" ON ""PlayerRecords""
FOR EACH ROW
WHEN (OLD.""PremiumDiamonds"" IS DISTINCT FROM NEW.""PremiumDiamonds"")
EXECUTE FUNCTION folkidle_record_diamond_change();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS trg_player_diamond_change ON ""PlayerRecords"";
DROP FUNCTION IF EXISTS folkidle_record_diamond_change();");

            migrationBuilder.DropTable(
                name: "diamond_daily");
        }
    }
}

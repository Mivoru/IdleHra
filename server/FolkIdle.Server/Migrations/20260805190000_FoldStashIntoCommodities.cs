using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Folds VillageStashInstances into CommodityRecords.
    ///
    /// Two tables held the same thing - a player id, an item id and a quantity -
    /// and nothing downstream distinguished them: every spend went through
    /// TryConsumeUnifiedAsync, which draws from both and refuses only when the
    /// SUM is short, and the inventory endpoint added them together before
    /// answering. The split was never a feature. It leaked into the client as
    /// two numbers and produced the same bug three times, each found separately:
    /// the larder, the boosts panel and the guild deposit all filtered on one
    /// half and hid stock the server would happily have taken.
    ///
    /// DATA IS MOVED, NOT DROPPED. Quantities are added onto the matching
    /// CommodityRecords row where one exists and inserted where it does not, so
    /// no player loses a material. The table itself stays behind, empty:
    /// TryConsumeUnifiedAsync still reads it, so anything a race or an
    /// unmigrated code path writes later is spent rather than stranded, and
    /// dropping it is a separate decision once the logs are quiet.
    /// </summary>
    public partial class FoldStashIntoCommodities : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add to the rows that already exist.
            migrationBuilder.Sql(@"
                UPDATE ""CommodityRecords"" c
                SET ""Quantity"" = c.""Quantity"" + s.total
                FROM (
                    SELECT ""PlayerId"", ""ItemId"", SUM(""Quantity"") AS total
                    FROM ""VillageStashInstances""
                    WHERE ""Quantity"" > 0
                    GROUP BY ""PlayerId"", ""ItemId""
                ) s
                WHERE c.""PlayerId"" = s.""PlayerId"" AND c.""ItemId"" = s.""ItemId"";
            ");

            // Insert the ones with no commodity row yet.
            migrationBuilder.Sql(@"
                INSERT INTO ""CommodityRecords"" (""PlayerId"", ""ItemId"", ""Quantity"")
                SELECT s.""PlayerId"", s.""ItemId"", SUM(s.""Quantity"")
                FROM ""VillageStashInstances"" s
                WHERE s.""Quantity"" > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM ""CommodityRecords"" c
                      WHERE c.""PlayerId"" = s.""PlayerId"" AND c.""ItemId"" = s.""ItemId""
                  )
                GROUP BY s.""PlayerId"", s.""ItemId"";
            ");

            migrationBuilder.Sql(@"DELETE FROM ""VillageStashInstances"";");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. The Up merged two quantities into one number
            // and there is no record of which half each unit came from, so an
            // honest inverse does not exist. Splitting them back arbitrarily
            // would invent a distinction that no longer means anything.
        }
    }
}

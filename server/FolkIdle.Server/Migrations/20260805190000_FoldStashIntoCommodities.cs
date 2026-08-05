using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    /// <remarks>
    /// THE TWO ATTRIBUTES ARE WHAT MAKE EF SEE THIS FILE AT ALL. Migrations are
    /// discovered by scanning the assembly for MigrationAttribute and then kept
    /// only if DbContextAttribute names this context; every other migration here
    /// carries both in its generated .Designer.cs. This one was hand-written
    /// without that file, so it was skipped in silence - the server logged
    /// "Database migrations applied successfully" on a run that applied none of
    /// it, and the live database still had the row missing from its history.
    /// A data-only migration needs no target-model snapshot, so the attributes
    /// are the whole fix; `dotnet ef migrations list` is the check.
    /// </remarks>
    [DbContext(typeof(FolkIdleDbContext))]
    [Migration("20260805190000_FoldStashIntoCommodities")]
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

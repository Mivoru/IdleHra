using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class MakeCommodityRecordsPlayerItemUnique : Migration
    {
        // Modul: NOT ADDITIVE (task 44, 2026-09-27). Before the unique index can
        // exist, any player holding two rows for one ItemId has them MERGED:
        // the lowest Id keeps the summed Quantity, the rest are deleted. Down
        // restores the plain index but cannot un-merge.
        //
        // Production had no duplicates when this was written (the GROUP BY ...
        // HAVING count(*) > 1 query returned 0 rows on 2026-09-27); the merge is
        // here for dev databases and for whatever lands between that query and
        // the deploy.
        //
        // MarketOrderRecords.CommodityId is a foreign key to this table (NO
        // ACTION), so an order that points at a row about to be deleted is
        // repointed at the survivor first, or the DELETE would fail.
        // historical_market_archives.CommodityId (snake_case table, no FK) is
        // repointed too so the history stays joinable.
        //
        // Raw SQL, so it runs inside the migration's own transaction: the
        // merge, the drop and the create all land or none do.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS commodity_dupe_merge;
CREATE TEMP TABLE commodity_dupe_merge AS
SELECT ""Id"",
       min(""Id"")       OVER (PARTITION BY ""PlayerId"", ""ItemId"") AS keep_id,
       sum(""Quantity"") OVER (PARTITION BY ""PlayerId"", ""ItemId"") AS total,
       count(*)        OVER (PARTITION BY ""PlayerId"", ""ItemId"") AS n
FROM ""CommodityRecords"";
DELETE FROM commodity_dupe_merge WHERE n < 2;

UPDATE ""CommodityRecords"" c SET ""Quantity"" = m.total
FROM commodity_dupe_merge m
WHERE c.""Id"" = m.""Id"" AND m.""Id"" = m.keep_id;

UPDATE ""MarketOrderRecords"" o SET ""CommodityId"" = m.keep_id
FROM commodity_dupe_merge m
WHERE o.""CommodityId"" = m.""Id"" AND m.""Id"" <> m.keep_id;

UPDATE historical_market_archives h SET ""CommodityId"" = m.keep_id
FROM commodity_dupe_merge m
WHERE h.""CommodityId"" = m.""Id"" AND m.""Id"" <> m.keep_id;

DELETE FROM ""CommodityRecords"" c
USING commodity_dupe_merge m
WHERE c.""Id"" = m.""Id"" AND m.""Id"" <> m.keep_id;

DROP TABLE commodity_dupe_merge;

DROP INDEX IF EXISTS ""IX_CommodityRecords_PlayerId_ItemId"";
CREATE UNIQUE INDEX ""IX_CommodityRecords_PlayerId_ItemId"" ON ""CommodityRecords"" (""PlayerId"", ""ItemId"");
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CommodityRecords_PlayerId_ItemId",
                table: "CommodityRecords");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityRecords_PlayerId_ItemId",
                table: "CommodityRecords",
                columns: new[] { "PlayerId", "ItemId" });
        }
    }
}

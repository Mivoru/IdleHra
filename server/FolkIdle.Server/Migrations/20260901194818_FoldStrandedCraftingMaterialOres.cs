using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Folds mining output that was filed under the wrong name back into the ore
    /// it was always meant to be.
    /// </summary>
    /// <remarks>
    /// Modul: THIS IS A DATA MIGRATION AND IT DELETES ROWS. Take a Supabase
    /// backup before the release that carries it - see ops/oracle/README.md,
    /// which says the same about every destructive migration and has been right
    /// twice already.
    ///
    /// WHY. The mining loot tables paid out `copper_ore_crafting_material`,
    /// `iron_ore_crafting_material`, `obsidian_ore_crafting_material` and
    /// `silver_ore_crafting_material`, because when those tables were written
    /// the four plain ores had no items.json entry to point at and the
    /// *_crafting_material variants were used as stand-ins. Everything that
    /// SPENDS ore - the village, tool recipes, guild buffs - asked for the plain
    /// name. So a player's mining accumulated in rows nothing could ever spend.
    ///
    /// Measured on the live database before this ran: one account holding 5,017
    /// `copper_ore_crafting_material` and ZERO `copper_ore`, unable to afford a
    /// 100-ore Town Hall while sitting on 152,968 birch logs. Woodcutting was
    /// wired correctly the whole time, which is exactly why the logs were fine
    /// and the ore was not.
    ///
    /// The loot tables were corrected in the same release, so this is a one-off
    /// reconciliation of what was earned before that, not an ongoing bridge.
    ///
    /// NOT REVERSIBLE, and Down says so rather than pretending. Once the
    /// balances are summed there is nothing recording which part came from
    /// which name, and inventing a split would be worse than refusing.
    /// </remarks>
    public partial class FoldStrandedCraftingMaterialOres : Migration
    {
        // Modul: written without ON CONFLICT on purpose. CommodityRecords has
        // NO unique constraint on (PlayerId, ItemId) - only a non-unique index
        // on ItemId - so an upsert would have failed outright, and duplicate
        // rows for one player and item are possible in principle. Everything
        // below aggregates the source with SUM and targets exactly ONE
        // destination row, so a duplicate cannot double-count.
        private const string StrandedOres =
            "('copper_ore_crafting_material','iron_ore_crafting_material'," +
            "'obsidian_ore_crafting_material','silver_ore_crafting_material')";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add the stranded totals to the plain-ore row where one exists.
            //    DISTINCT ON picks the lowest Id per (player, item) so that if a
            //    player somehow has two rows for the same ore, the sum lands in
            //    one of them rather than in both.
            migrationBuilder.Sql($@"
                WITH src AS (
                    SELECT ""PlayerId"",
                           replace(""ItemId"", '_crafting_material', '') AS base_id,
                           SUM(""Quantity"") AS qty
                      FROM ""CommodityRecords""
                     WHERE ""ItemId"" IN {StrandedOres}
                     GROUP BY 1, 2
                ),
                target AS (
                    SELECT DISTINCT ON (c.""PlayerId"", c.""ItemId"")
                           c.""Id"", c.""PlayerId"", c.""ItemId""
                      FROM ""CommodityRecords"" c
                      JOIN src ON src.""PlayerId"" = c.""PlayerId""
                                AND src.base_id     = c.""ItemId""
                     ORDER BY c.""PlayerId"", c.""ItemId"", c.""Id""
                )
                UPDATE ""CommodityRecords"" c
                   SET ""Quantity"" = c.""Quantity"" + src.qty
                  FROM src
                  JOIN target t ON t.""PlayerId"" = src.""PlayerId""
                                AND t.""ItemId""   = src.base_id
                 WHERE c.""Id"" = t.""Id"";
            ");

            // 2. Create the row for players who hold the stranded name and have
            //    no plain-ore row at all - which is the common case, since the
            //    plain ores were unobtainable until this release.
            migrationBuilder.Sql($@"
                INSERT INTO ""CommodityRecords"" (""ItemId"", ""PlayerId"", ""Quantity"")
                SELECT src.base_id, src.""PlayerId"", src.qty
                  FROM (
                        SELECT ""PlayerId"",
                               replace(""ItemId"", '_crafting_material', '') AS base_id,
                               SUM(""Quantity"") AS qty
                          FROM ""CommodityRecords""
                         WHERE ""ItemId"" IN {StrandedOres}
                         GROUP BY 1, 2
                       ) src
                 WHERE NOT EXISTS (
                        SELECT 1 FROM ""CommodityRecords"" c
                         WHERE c.""PlayerId"" = src.""PlayerId""
                           AND c.""ItemId""   = src.base_id
                       );
            ");

            // 3. Retire the stranded rows. Nothing reads these names any more -
            //    the loot tables, the recipes and the village all moved to the
            //    plain ores in this same release - so leaving them would only
            //    show players a stack they can never spend.
            migrationBuilder.Sql($@"
                DELETE FROM ""CommodityRecords"" WHERE ""ItemId"" IN {StrandedOres};
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. The Up sums two balances into one and deletes
            // the source, so nothing records how much of a player's ore came
            // from which name. A Down that guessed a split would hand players
            // back a number that was never true; refusing is the honest answer.
            // Restore from the backup the remarks above tell you to take.
        }
    }
}

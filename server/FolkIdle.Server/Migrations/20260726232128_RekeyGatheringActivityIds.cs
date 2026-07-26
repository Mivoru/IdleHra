using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class RekeyGatheringActivityIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Modul: activity id bands. Re-keys every persisted gathering
            // assignment into its new band. Without this, a character logged out
            // while chopping wood on node 101 comes back pointing at an activity
            // id that no longer resolves to anything - the tick would treat it
            // as a monster id (101 was Region 3's Desert Crab), which is the
            // exact confusion this whole change exists to remove.
            //
            // The mapping is profession band + index, mirroring
            // ActivityIdBands.MapLegacyGatheringId: 101 to 1001, 205 to 2005,
            // 412 to 4012. Only ids in the old 101-499 gathering range move;
            // combat assignments (1-115) and the World Boss sentinel (9999) are
            // left exactly as they are.
            migrationBuilder.Sql(@"
                UPDATE ""characters""
                SET ""ActiveActivityId"" =
                    (""ActiveActivityId"" / 100 - 1) * 1000 + 1000 + (""ActiveActivityId"" % 100)
                WHERE ""ActiveActivityId"" BETWEEN 101 AND 499;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the re-key so a rollback does not strand every gathering
            // character on an id the old content file has never heard of.
            migrationBuilder.Sql(@"
                UPDATE ""characters""
                SET ""ActiveActivityId"" =
                    (""ActiveActivityId"" / 1000) * 100 + (""ActiveActivityId"" % 1000)
                WHERE ""ActiveActivityId"" BETWEEN 1001 AND 4999;
            ");
        }
    }
}

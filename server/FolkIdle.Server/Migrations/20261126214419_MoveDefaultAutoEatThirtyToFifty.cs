using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Modul: THE OLD DEFAULT, WRITTEN BACK AS IF CHOSEN, 2026-11-26.
    ///
    /// 1.121.0 moved the auto-eat default from 30% to 50% and retuned the
    /// region 4 and 5 boss walls for it. But LoadPlayerState resolved an
    /// untouched column (0) to the default and every checkpoint then wrote
    /// that 30 back, so in production 17 of 21 accounts held 30 - not a choice
    /// anyone made, the old default frozen into the row. They would have kept
    /// eating at 30% against walls sized for 50%.
    ///
    /// This moves exactly 30 to 50. Any other value (0 untouched, -1 off,
    /// 25, 80 ...) is a real choice or already resolves to the new default,
    /// and is left alone. It runs on the container entrypoint, before the
    /// engine loads a single player, so no live checkpoint can write a 30
    /// back over it. The affected ids were saved off the box before deploy.
    ///
    /// Down is deliberately empty: after Up, a player who really chose 30 is
    /// indistinguishable from one who never touched the slider, so there is
    /// no honest inverse. They can set 30 again in the Larder.
    /// </summary>
    public partial class MoveDefaultAutoEatThirtyToFifty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"PlayerRecords\" SET \"AutoEatThresholdPct\" = 50 WHERE \"AutoEatThresholdPct\" = 30;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

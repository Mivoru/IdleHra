using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthMethod",
                table: "PlayerRefreshTokens",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CurrentSessionNonce",
                table: "PlayerRecords",
                type: "text",
                nullable: true);

            // Modul: final-review Finding 2 - every row that existed before
            // this migration backfills to AuthMethod = '' (the C# default
            // above), and RequiresPasswordStepUpAsync only gates a session
            // whose AuthMethod is literally "dev". An empty string is not
            // "dev", so a pre-existing device-bearer refresh token would
            // silently and PERMANENTLY escape the step-up gate this whole
            // feature exists to add - refresh-token rotation copies
            // AuthMethod onto each successor row unchanged, so nothing ever
            // corrects it later. Backfilling to "dev" is the safe direction:
            // a legacy token on a password-bearing account now gets one
            // step-up prompt it CAN satisfy, rather than the check being
            // skipped forever. A legacy token on a passwordless account is
            // unaffected either way, because RequiresPasswordStepUpAsync
            // also checks whether the account has a password set.
            migrationBuilder.Sql(@"
                UPDATE ""PlayerRefreshTokens""
                SET ""AuthMethod"" = 'dev'
                WHERE ""AuthMethod"" = '';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthMethod",
                table: "PlayerRefreshTokens");

            migrationBuilder.DropColumn(
                name: "CurrentSessionNonce",
                table: "PlayerRecords");
        }
    }
}

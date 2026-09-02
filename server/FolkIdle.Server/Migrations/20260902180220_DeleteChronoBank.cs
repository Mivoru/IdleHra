using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Deletes the chrono bank, and pays out what players were holding first.
    ///
    /// Modul: A MIGRATION THAT SILENTLY DELETES SOMEBODY'S DATA IS THE ONE KIND
    /// OF LOSS NOBODY FORGIVES. Thirteen accounts held 3,834,089 banked seconds
    /// when this was written - about 1,065 hours - and the scaffolded version of
    /// this migration dropped the table without paying any of it.
    ///
    /// THE CONVERSION IS 1:1, AND THAT IS NOT A ROUNDING. A banked chrono second
    /// bought exactly one extra simulated second: acceleration ran the sub-tick
    /// body (multiplier-1) extra times and charged (multiplier-1) * interval, so
    /// at 2x one banked second bought one extra second of simulation. The speed
    /// toggle that SURVIVES this deletion pays for each extra tick out of
    /// AccumulatedTimeBankMs at 100ms, i.e. also one extra simulated second per
    /// banked second. So this moves the balance into the mechanic that replaced
    /// it at par. There is no exchange rate to argue about, and - unlike paying
    /// it out in gold - it cannot inflate an economy whose entire diamond supply
    /// is 49 and whose richest account holds 22M gold.
    /// </summary>
    public partial class DeleteChronoBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (a) The rows that join normally, on PlayerGuid.
            migrationBuilder.Sql(@"
                UPDATE ""PlayerRecords"" p
                SET ""AccumulatedTimeBankSeconds"" =
                    ""AccumulatedTimeBankSeconds"" + a.""BankedChronoSeconds""
                FROM account_chrono_registry a
                WHERE p.""PlayerGuid"" = a.""AccountId""
                  AND a.""BankedChronoSeconds"" > 0;
            ");

            // (b) The synthesised-id rows. StateCheckpointManager.ResolveAccountId
            // built a Guid from the long player id with byte 15 forced to 0x67
            // whenever PlayerGuid was empty, so those rows join nothing above and
            // would have been written off by accident rather than on purpose.
            // Reverse it: the first eight hex digits are the player id. Only rows
            // whose player still exists are paid.
            migrationBuilder.Sql(@"
                UPDATE ""PlayerRecords"" p
                SET ""AccumulatedTimeBankSeconds"" =
                    ""AccumulatedTimeBankSeconds"" + a.""BankedChronoSeconds""
                FROM account_chrono_registry a
                WHERE a.""AccountId""::text LIKE '________-0000-0000-0000-000000000067'
                  AND p.""Id"" = ('x' || substring(a.""AccountId""::text, 1, 8))::bit(32)::int
                  AND a.""BankedChronoSeconds"" > 0;
            ");

            // Whatever is left is unattributable: an AccountId matching no player
            // and not a reversible synthesised id. Measured on 2026-09-02 that was
            // four rows totalling 1,823 seconds - roughly thirty minutes across all
            // four. Written off deliberately, and recorded here rather than
            // vanishing, so the decision is auditable if anyone ever asks.

            migrationBuilder.DropTable(
                name: "account_chrono_registry");

            // The two columns on PlayerRecords were a second, divergent copy of the
            // same balance - the registry was authoritative and overwrote them at
            // every login, and the two disagreed by 278,121 seconds in production.
            // Compensation above reads the registry for exactly that reason.
            migrationBuilder.DropColumn(
                name: "BankedChronoSeconds",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "IsChronoAccelerating",
                table: "PlayerRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Modul: Down() restores the SHAPE, never the balances. The seconds
            // were converted into AccumulatedTimeBankSeconds by Up(), and there is
            // no way to tell a converted second from one the player earned
            // normally. Reversing the payout would take time players legitimately
            // hold; leaving it means a down-then-up pays twice. Down is a schema
            // escape hatch here, not a rollback - do not run it against a database
            // that has already served Up() to real players.
            migrationBuilder.AddColumn<double>(
                name: "BankedChronoSeconds",
                table: "PlayerRecords",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "IsChronoAccelerating",
                table: "PlayerRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "account_chrono_registry",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccelerationTerminationEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ActiveSpeedMultiplier = table.Column<double>(type: "double precision", nullable: false),
                    BankedChronoSeconds = table.Column<int>(type: "integer", nullable: false),
                    LastClockSyncEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_chrono_registry", x => x.AccountId);
                    table.CheckConstraint("CK_account_chrono_registry_ActiveSpeedMultiplier", "\"ActiveSpeedMultiplier\" IN (1.0, 2.0, 4.0)");
                    table.CheckConstraint("CK_account_chrono_registry_BankedChronoSeconds", "\"BankedChronoSeconds\" >= 0 AND \"BankedChronoSeconds\" <= 604800");
                });
        }
    }
}

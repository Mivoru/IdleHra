using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// DELIBERATELY EMPTY, and it has to exist anyway.
    ///
    /// Modul: `PlayerDeviceRegistrations.DeviceTokenRaw` was declared
    /// `HasMaxLength(64)` - the width of the fixed wire field behind opcode 33,
    /// not the width of a device token. An FCM registration token is around 160
    /// characters, so that number was a claim about the model that no real
    /// token satisfied. It is now the engine's own `MaxDeviceTokenBytes` (512).
    ///
    /// Postgres `bytea` carries no length, so the SCHEMA does not move and
    /// there is nothing for Up/Down to do. What moved is the model snapshot,
    /// and that is why this file is here: without it the change would sit
    /// un-migrated and silently ride along inside whatever unrelated migration
    /// somebody generated next.
    /// </summary>
    public partial class WidenPushDeviceTokenColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No SQL: see the class comment. bytea has no length in Postgres.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo.
        }
    }
}

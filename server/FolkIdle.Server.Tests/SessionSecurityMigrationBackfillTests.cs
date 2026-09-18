using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A LEGACY REFRESH TOKEN MUST NOT ESCAPE THE STEP-UP GATE FOREVER.
    ///
    /// AddSessionSecurity's Up() backfills AuthMethod to the C# default of ""
    /// (empty string) for every PlayerRefreshTokens row that existed before
    /// it ran. RequiresPasswordStepUpAsync only gates a session whose
    /// AuthMethod is literally "dev" - an empty string is not "dev", and
    /// refresh-token rotation copies AuthMethod onto each successor row
    /// unchanged, so a pre-existing device-bearer token would silently and
    /// PERMANENTLY skip the step-up check this whole feature exists to add.
    ///
    /// The fix is a data backfill inside the same migration (it has not
    /// shipped anywhere yet, so editing it directly rather than adding a
    /// second migration is safe): every row backfilled to "" is rewritten to
    /// "dev" in the same Up(). This test proves that SQL is correct by
    /// running the actual migration against a real Postgres - not by reading
    /// it and trusting it compiles - the same way MigrationDiscoveryTests
    /// proves a migration is reachable at all rather than merely written.
    /// </summary>
    [Collection("Postgres collection")]
    public class SessionSecurityMigrationBackfillTests : IAsyncLifetime
    {
        private readonly PostgresTestFixture _fixture;
        private string _databaseName = string.Empty;
        private DbContextOptions<FolkIdleDbContext> _options = null!;

        public SessionSecurityMigrationBackfillTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        public async Task InitializeAsync()
        {
            _databaseName = $"sessmigrate_{Guid.NewGuid():N}";

            await using (var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
            {
                Database = _databaseName,
            };
            _options = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
        }

        public async Task DisposeAsync()
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }

        // Modul: the migration immediately before AddSessionSecurity in this
        // tree, at the time this test was written. If a later migration is
        // inserted between them, this literal needs updating - EF's
        // migration ids are strings assigned by the generator, not an enum
        // this test can reach mechanically.
        private const string MigrationBeforeSessionSecurity = "20260916113501_AddBreedingTraits";

        [Fact]
        public async Task PreExistingRefreshTokenRowsBackfillToDevNotEmptyString()
        {
            await using var db = new FolkIdleDbContext(_options);
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

            // 1. Bring the schema up to exactly the state it was in the
            // moment before this migration ran - PlayerRefreshTokens exists,
            // but its AuthMethod column does not yet.
            await migrator.MigrateAsync(MigrationBeforeSessionSecurity);

            // 2. Insert a row shaped like a real pre-migration row: nothing
            // in it could possibly know about AuthMethod, because the column
            // does not exist in the schema yet.
            var legacyAccountId = Guid.NewGuid();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using (var conn = new Npgsql.NpgsqlConnection(db.Database.GetConnectionString()))
            {
                await conn.OpenAsync();
                await using var insert = conn.CreateCommand();
                insert.CommandText = @"
                    INSERT INTO ""PlayerRefreshTokens"" (""AccountId"", ""TokenHash"", ""IssuedEpoch"", ""ExpiresAtEpoch"", ""RevokedEpoch"")
                    VALUES (@accountId, @tokenHash, @issued, @expires, 0)";
                insert.Parameters.AddWithValue("accountId", legacyAccountId);
                insert.Parameters.AddWithValue("tokenHash", new byte[32]);
                insert.Parameters.AddWithValue("issued", now);
                insert.Parameters.AddWithValue("expires", now + 1_000_000L);
                await insert.ExecuteNonQueryAsync();
            }

            // 3. Run the rest of the migrations, including AddSessionSecurity
            // itself, whose Up() both adds the AuthMethod column (defaulting
            // every existing row to "") and then backfills those defaulted
            // rows to "dev".
            await migrator.MigrateAsync();

            // 4. The legacy row must read "dev", NOT the raw C# default "" -
            // that is the entire behaviour under test.
            await using (var conn = new Npgsql.NpgsqlConnection(db.Database.GetConnectionString()))
            {
                await conn.OpenAsync();
                await using var select = conn.CreateCommand();
                select.CommandText = @"SELECT ""AuthMethod"" FROM ""PlayerRefreshTokens"" WHERE ""AccountId"" = @accountId";
                select.Parameters.AddWithValue("accountId", legacyAccountId);
                var authMethod = (string?)await select.ExecuteScalarAsync();

                Assert.Equal("dev", authMethod);
            }
        }
    }
}

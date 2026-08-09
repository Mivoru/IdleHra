using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Getting back into an account you still own - and nobody else's.
    ///
    /// A reset flow is a deliberately built back door, so every property that
    /// keeps it from being a real one is asserted here rather than reasoned
    /// about: single use, expiry, superseding, the password policy, and the
    /// silence that stops it being an account enumeration oracle.
    ///
    /// Its own database, like DevFixtureInvariantTests: these write PlayerRecords
    /// keyed on email addresses and read them back by address, and the shared
    /// fixture has other tests' accounts in it.
    /// </summary>
    [Collection("Postgres collection")]
    public class PasswordResetTests : IAsyncLifetime
    {
        private readonly PostgresTestFixture _fixture;
        private string _databaseName = string.Empty;
        private DbContextOptions<FolkIdleDbContext> _options = null!;

        public PasswordResetTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        public async Task InitializeAsync()
        {
            _databaseName = $"pwreset_{Guid.NewGuid():N}";

            var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString);
            await using (var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            builder.Database = _databaseName;
            _options = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;

            await using var db = new FolkIdleDbContext(_options);
            await db.Database.MigrateAsync();
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

        private FolkIdleDbContext NewContext() => new(_options);

        private const string Email = "reset_subject@example.com";
        private const string OldPassword = "the old password";
        private const string NewPassword = "the new password";

        private async Task<long> SeedAccountAsync(string email = Email, string? password = OldPassword)
        {
            await using var db = NewContext();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Email = email,
                Username = "ResetSubject" + Guid.NewGuid().ToString("N")[..8],
                PasswordHash = password == null ? null : PasswordHasher.Hash(password),
                DeviceId = "remembered-device-" + Guid.NewGuid().ToString("N"),
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // --- the happy path ---------------------------------------------------

        [Fact]
        public async Task AResetLetsTheOwnerBackInAndTheOldPasswordStops()
        {
            long playerId = await SeedAccountAsync();

            string? token;
            await using (var db = NewContext())
            {
                token = await PasswordResetEngine.BeginResetAsync(db, Email, Now());
            }
            Assert.NotNull(token);

            await using (var db = NewContext())
            {
                var outcome = await PasswordResetEngine.CompleteResetAsync(db, token!, NewPassword, Now());
                Assert.Equal(PasswordResetOutcome.Success, outcome);
            }

            await using (var verify = NewContext())
            {
                var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);

                Assert.True(PasswordHasher.Verify(NewPassword, player.PasswordHash));
                Assert.False(PasswordHasher.Verify(OldPassword, player.PasswordHash));

                // Modul: THE REMEMBERED DEVICE IS CUT LOOSE. DeviceId signs
                // somebody in with no password at all, and a reset is often
                // being done precisely because another person has the machine.
                Assert.Null(player.DeviceId);
            }
        }

        /// <summary>
        /// THE TOKEN IS NOT IN THE DATABASE. It is a bearer credential -
        /// whoever holds it owns the account - so a leaked table must not hand
        /// the accounts over with it.
        /// </summary>
        [Fact]
        public async Task TheStoredRowNeverContainsTheTokenItself()
        {
            await SeedAccountAsync();

            string? token;
            await using (var db = NewContext())
            {
                token = await PasswordResetEngine.BeginResetAsync(db, Email, Now());
            }

            await using var verify = NewContext();
            var row = await verify.PasswordResetTokens.AsNoTracking().SingleAsync();

            Assert.NotEqual(token, row.TokenHash);
            Assert.Equal(PasswordResetEngine.HashToken(token!), row.TokenHash);
        }

        // --- the refusals -----------------------------------------------------

        [Fact]
        public async Task ALinkWorksExactlyOnce()
        {
            await SeedAccountAsync();

            string? token;
            await using (var db = NewContext())
            {
                token = await PasswordResetEngine.BeginResetAsync(db, Email, Now());
            }

            await using (var db = NewContext())
            {
                Assert.Equal(PasswordResetOutcome.Success,
                    await PasswordResetEngine.CompleteResetAsync(db, token!, NewPassword, Now()));
            }

            await using (var db = NewContext())
            {
                Assert.Equal(PasswordResetOutcome.AlreadyUsed,
                    await PasswordResetEngine.CompleteResetAsync(db, token!, "another password", Now()));
            }
        }

        [Fact]
        public async Task AnHourOldLinkIsRefused()
        {
            await SeedAccountAsync();
            long issuedAt = Now();

            string? token;
            await using (var db = NewContext())
            {
                token = await PasswordResetEngine.BeginResetAsync(db, Email, issuedAt);
            }

            await using (var db = NewContext())
            {
                long justPastExpiry = issuedAt + PasswordResetEngine.TokenLifetimeSeconds + 1;
                Assert.Equal(PasswordResetOutcome.Expired,
                    await PasswordResetEngine.CompleteResetAsync(db, token!, NewPassword, justPastExpiry));
            }
        }

        /// <summary>
        /// "Send it again" must not leave two live keys in two inboxes.
        /// </summary>
        [Fact]
        public async Task ASecondRequestKillsTheFirstLink()
        {
            await SeedAccountAsync();

            string? first;
            string? second;
            await using (var db = NewContext())
            {
                first = await PasswordResetEngine.BeginResetAsync(db, Email, Now());
            }
            await using (var db = NewContext())
            {
                second = await PasswordResetEngine.BeginResetAsync(db, Email, Now());
            }

            Assert.NotEqual(first, second);

            await using (var db = NewContext())
            {
                Assert.Equal(PasswordResetOutcome.AlreadyUsed,
                    await PasswordResetEngine.CompleteResetAsync(db, first!, NewPassword, Now()));
            }

            await using (var db = NewContext())
            {
                Assert.Equal(PasswordResetOutcome.Success,
                    await PasswordResetEngine.CompleteResetAsync(db, second!, NewPassword, Now()));
            }
        }

        [Fact]
        public async Task AForgedTokenIsRefused()
        {
            await SeedAccountAsync();

            await using var db = NewContext();
            Assert.Equal(PasswordResetOutcome.InvalidToken,
                await PasswordResetEngine.CompleteResetAsync(db, PasswordResetEngine.GenerateToken(), NewPassword, Now()));
            Assert.Equal(PasswordResetOutcome.InvalidToken,
                await PasswordResetEngine.CompleteResetAsync(db, string.Empty, NewPassword, Now()));
        }

        /// <summary>
        /// The new password goes through the same policy as a new account's,
        /// and it is checked BEFORE the token is spent - a player who fumbles
        /// it must not also lose their link.
        /// </summary>
        [Fact]
        public async Task AWeakNewPasswordIsRefusedAndTheLinkSurvives()
        {
            await SeedAccountAsync();

            string? token;
            await using (var db = NewContext())
            {
                token = await PasswordResetEngine.BeginResetAsync(db, Email, Now());
            }

            await using (var db = NewContext())
            {
                Assert.Equal(PasswordResetOutcome.InvalidPassword,
                    await PasswordResetEngine.CompleteResetAsync(db, token!, "short", Now()));
            }

            await using (var db = NewContext())
            {
                Assert.Equal(PasswordResetOutcome.Success,
                    await PasswordResetEngine.CompleteResetAsync(db, token!, NewPassword, Now()));
            }
        }

        // --- the silence ------------------------------------------------------

        /// <summary>
        /// AN UNKNOWN ADDRESS PRODUCES NOTHING, and that is the property that
        /// stops this being the enumeration oracle /api/v1/auth/check-email was
        /// deleted for. The endpoint answers 200 either way; here the assertion
        /// is that no row is written, so there is nothing to time or count.
        /// </summary>
        [Fact]
        public async Task AnUnknownAddressLeavesNoTrace()
        {
            await SeedAccountAsync();

            await using (var db = NewContext())
            {
                Assert.Null(await PasswordResetEngine.BeginResetAsync(db, "nobody@example.com", Now()));
            }

            await using var verify = NewContext();
            Assert.Empty(await verify.PasswordResetTokens.AsNoTracking().ToListAsync());
        }

        /// <summary>
        /// An anonymous device-provisioned account has no password to reset,
        /// and minting a token for one would let anybody who guessed the
        /// address SET a password on an account they never owned.
        /// </summary>
        [Fact]
        public async Task AnAccountWithNoPasswordCannotHaveOneSetThisWay()
        {
            await SeedAccountAsync("anonymous@example.com", password: null);

            await using var db = NewContext();
            Assert.Null(await PasswordResetEngine.BeginResetAsync(db, "anonymous@example.com", Now()));
        }

        [Fact]
        public async Task TheAddressIsMatchedCaseInsensitively()
        {
            await SeedAccountAsync();

            await using var db = NewContext();
            Assert.NotNull(await PasswordResetEngine.BeginResetAsync(db, Email.ToUpperInvariant(), Now()));
        }

        /// <summary>
        /// One player's link must never open another player's account. Sounds
        /// obvious; it is one join condition away from being false.
        /// </summary>
        [Fact]
        public async Task ALinkOnlyEverOpensItsOwnAccount()
        {
            long subjectId = await SeedAccountAsync();
            long bystanderId = await SeedAccountAsync("bystander@example.com");

            string? token;
            await using (var db = NewContext())
            {
                token = await PasswordResetEngine.BeginResetAsync(db, Email, Now());
            }

            await using (var db = NewContext())
            {
                await PasswordResetEngine.CompleteResetAsync(db, token!, NewPassword, Now());
            }

            await using var verify = NewContext();
            var bystander = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == bystanderId);
            Assert.True(PasswordHasher.Verify(OldPassword, bystander.PasswordHash));

            var subject = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == subjectId);
            Assert.True(PasswordHasher.Verify(NewPassword, subject.PasswordHash));
        }
    }
}

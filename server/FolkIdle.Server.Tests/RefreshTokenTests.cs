using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// STAYING SIGNED IN OVERNIGHT WITHOUT HANDING OUT A KEY THAT CANNOT BE
    /// TAKEN BACK.
    ///
    /// The JWT lasts 24 hours, which in a browser tab is a mild annoyance and
    /// on a phone is a password prompt every morning - in a game whose entire
    /// proposition is that it runs while you are away. The cheap fix was to
    /// lengthen the JWT, and it is the one NOT taken: a JWT is a bearer
    /// credential this server does not store, so a stolen 60-day one is valid
    /// for 60 days and nothing anybody does shortens that by a second.
    ///
    /// A refresh token is a row, and a row can be revoked. Every property that
    /// makes that true rather than merely intended is asserted here: single
    /// use, rotation, expiry, replay taking the whole family down, and a
    /// password reset ending every session on the account.
    ///
    /// Its own database, like PasswordResetTests, for the same reason - these
    /// count rows per account and the shared fixture holds other tests'.
    /// </summary>
    [Collection("Postgres collection")]
    public class RefreshTokenTests : IAsyncLifetime
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;
        private string _databaseName = string.Empty;
        private DbContextOptions<FolkIdleDbContext> _options = null!;
        private RetryingDbContextOptions _authOptions = null!;

        public RefreshTokenTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        public async Task InitializeAsync()
        {
            _databaseName = $"refreshtok_{Guid.NewGuid():N}";

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
            _authOptions = new RetryingDbContextOptions(_options);

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

        private async Task<int> LiveTokenCountAsync(Guid accountId)
        {
            await using var db = NewContext();
            return await db.PlayerRefreshTokens.CountAsync(t => t.AccountId == accountId && t.RevokedEpoch == 0L);
        }

        // --- what is stored -------------------------------------------------

        [Fact]
        public async Task TheRawTokenIsNeverWrittenDown()
        {
            // Modul: the row is a VERIFIER, not the credential. A dump of this
            // table must let nobody sign in as anybody, exactly as with a
            // password - so what is stored is a hash and the raw value exists
            // only in the reply the player's device received.
            var accountId = Guid.NewGuid();
            var issued = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);

            await using var db = NewContext();
            var row = await db.PlayerRefreshTokens.SingleAsync(t => t.AccountId == accountId);

            Assert.Equal(32, row.TokenHash.Length);
            Assert.Equal(AuthenticationEngine.HashRefreshToken(issued.Token), row.TokenHash);
            Assert.DoesNotContain(issued.Token, System.Text.Encoding.UTF8.GetString(row.TokenHash));
        }

        [Fact]
        public void TokensAreUnguessableAndDistinct()
        {
            // 32 bytes of CSPRNG output, base64url. Not a Guid: a Guid is 122
            // bits with fixed version and variant nibbles, and some
            // implementations of it are not cryptographic at all.
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 500; i++)
            {
                string token = AuthenticationEngine.GenerateRefreshToken();
                Assert.True(token.Length >= 40, $"a {token.Length}-character token is not 256 bits of anything");
                Assert.True(seen.Add(token), "two tokens collided, which means this is not what it claims to be");
            }
        }

        [Fact]
        public void TheLifetimeIsLongEnoughToBeWorthHaving()
        {
            // The whole point is a player who plays at weekends is never signed
            // out. Anything under a fortnight would not achieve that, and the
            // JWT it complements is deliberately still 24 hours.
            _output.WriteLine(
                $"refresh {AuthenticationEngine.RefreshTokenLifetimeSeconds / 86400} days, " +
                $"JWT {AuthenticationEngine.TokenLifetimeSeconds / 3600} hours");

            Assert.True(AuthenticationEngine.RefreshTokenLifetimeSeconds >= 14L * 86400L);
            Assert.Equal(86400L, AuthenticationEngine.TokenLifetimeSeconds);
        }

        // --- spending it ----------------------------------------------------

        [Fact]
        public async Task RedeemingReturnsTheAccountAndRotatesTheToken()
        {
            var accountId = Guid.NewGuid();
            var issued = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);

            var result = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, issued.Token);

            Assert.Equal(AuthenticationEngine.RefreshOutcome.Rotated, result.Outcome);
            Assert.Equal(accountId, result.AccountId);
            Assert.NotEqual(issued.Token, result.Token);

            // Exactly one live token afterwards: the successor. The spent one
            // is revoked rather than deleted, because "when" is the question
            // anybody investigating a stolen session will ask.
            Assert.Equal(1, await LiveTokenCountAsync(accountId));
        }

        [Fact]
        public async Task TheSuccessorWorksAndTheSpentOneDoesNot()
        {
            var accountId = Guid.NewGuid();
            var first = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);

            var second = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, first.Token);
            var third = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, second.Token);

            Assert.Equal(AuthenticationEngine.RefreshOutcome.Rotated, third.Outcome);
            Assert.Equal(accountId, third.AccountId);
        }

        [Fact]
        public async Task ARedeemedTokenComingBackREVOKESTHEWHOLEACCOUNT()
        {
            // Modul: THE ONE THAT LOOKS HARSH AND IS NOT.
            //
            // A spent token presented again has two possible causes and this
            // server cannot tell them apart: the client lost the reply to its
            // own refresh and retried, or somebody else has a copy. Treating it
            // as the first leaves a stolen credential working. Treating it as
            // the second signs one honest player out once. Only the second is
            // defensible.
            var accountId = Guid.NewGuid();
            var first = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);
            var second = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, first.Token);

            Assert.Equal(1, await LiveTokenCountAsync(accountId));

            var replay = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, first.Token);

            Assert.Equal(AuthenticationEngine.RefreshOutcome.Replayed, replay.Outcome);
            Assert.Equal(Guid.Empty, replay.AccountId);

            // The successor the thief did NOT have is gone too. That is the
            // whole point - the account is locked to a fresh sign-in.
            Assert.Equal(0, await LiveTokenCountAsync(accountId));

            var afterwards = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, second.Token);
            Assert.NotEqual(AuthenticationEngine.RefreshOutcome.Rotated, afterwards.Outcome);
        }

        [Fact]
        public async Task AnExpiredTokenIsRefusedAndRetired()
        {
            var accountId = Guid.NewGuid();
            var issued = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);

            // Hoisted into a local: ExecuteUpdate translates the setter to SQL,
            // and DateTimeOffset.ToUnixTimeSeconds has no translation. The
            // production paths already compute `now` before the call for the
            // same reason.
            long justExpired = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1L;
            await using (var db = NewContext())
            {
                await db.PlayerRefreshTokens
                    .Where(t => t.AccountId == accountId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAtEpoch, justExpired));
            }

            var result = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, issued.Token);

            Assert.Equal(AuthenticationEngine.RefreshOutcome.Expired, result.Outcome);
            Assert.Equal(0, await LiveTokenCountAsync(accountId));

            // Retired rather than left lying about, so a later presentation is
            // a plain refusal and not a replay alarm about a token that simply
            // ran out.
            var again = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, issued.Token);
            Assert.Equal(AuthenticationEngine.RefreshOutcome.Replayed, again.Outcome);
        }

        [Fact]
        public async Task NonsenseIsUnknownRatherThanAnException()
        {
            foreach (string candidate in new[] { "", "   ", "not-a-token", AuthenticationEngine.GenerateRefreshToken() })
            {
                var result = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, candidate);
                Assert.Equal(AuthenticationEngine.RefreshOutcome.Unknown, result.Outcome);
            }
        }

        // --- taking it back -------------------------------------------------

        [Fact]
        public async Task SigningOutEndsThatDeviceAndLeavesTheOthers()
        {
            var accountId = Guid.NewGuid();
            var phone = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);
            await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);

            await AuthenticationEngine.RevokeRefreshTokenAsync(_authOptions, phone.Token);

            Assert.Equal(
                AuthenticationEngine.RefreshOutcome.Replayed,
                (await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, phone.Token)).Outcome);

            // Modul: and the tablet is gone too - because the line above was a
            // REPLAY of a revoked token, which revokes the family. That is the
            // correct behaviour and it is worth seeing written down: signing
            // out on one device does not touch the others, but presenting the
            // signed-out token afterwards does.
            Assert.Equal(0, await LiveTokenCountAsync(accountId));
        }

        [Fact]
        public async Task SigningOutOnOneDeviceLeavesTheOtherWorking()
        {
            var accountId = Guid.NewGuid();
            var phone = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);
            var tablet = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);

            await AuthenticationEngine.RevokeRefreshTokenAsync(_authOptions, phone.Token);

            var result = await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, tablet.Token);
            Assert.Equal(AuthenticationEngine.RefreshOutcome.Rotated, result.Outcome);
        }

        [Fact]
        public async Task RevokingEverythingClearsEveryDevice()
        {
            var accountId = Guid.NewGuid();
            await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);
            var second = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);
            await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);

            await AuthenticationEngine.RevokeAllRefreshTokensAsync(_authOptions, accountId);

            Assert.Equal(0, await LiveTokenCountAsync(accountId));
            Assert.NotEqual(
                AuthenticationEngine.RefreshOutcome.Rotated,
                (await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, second.Token)).Outcome);
        }

        [Fact]
        public async Task OneAccountsRevocationLeavesAnotherAlone()
        {
            var mine = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, mine);
            var theirToken = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, theirs);

            await AuthenticationEngine.RevokeAllRefreshTokensAsync(_authOptions, mine);

            Assert.Equal(
                AuthenticationEngine.RefreshOutcome.Rotated,
                (await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, theirToken.Token)).Outcome);
        }

        // --- the reason revocability was the requirement ---------------------

        [Fact]
        public async Task APasswordResetEndsEverySession()
        {
            // Modul: THE CASE THAT MADE A LONGER JWT UNACCEPTABLE.
            //
            // Somebody resets a password because another person has been in
            // their account. If the reset changed the password and left sixty
            // days of refresh token working, the reset would have achieved
            // nothing - which is exactly why CompleteResetAsync already clears
            // the remembered DeviceId, and exactly why it now revokes these
            // too.
            var accountId = Guid.NewGuid();
            long playerId;

            await using (var db = NewContext())
            {
                var player = new PlayerRecord
                {
                    PlayerGuid = accountId,
                    AuthenticatorToken = Guid.NewGuid(),
                    Email = "refresh_reset@example.com",
                    Username = "RefreshResetSubject",
                    PasswordHash = PasswordHasher.Hash("the old password"),
                    DeviceId = "remembered-" + Guid.NewGuid().ToString("N"),
                };
                db.PlayerRecords.Add(player);
                await db.SaveChangesAsync();
                playerId = player.Id;
            }

            var stolen = await AuthenticationEngine.IssueRefreshTokenAsync(_authOptions, accountId);
            Assert.Equal(1, await LiveTokenCountAsync(accountId));

            string? resetToken;
            await using (var db = NewContext())
            {
                resetToken = await PasswordResetEngine.BeginResetAsync(
                    db, "refresh_reset@example.com", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            Assert.NotNull(resetToken);

            await using (var db = NewContext())
            {
                var outcome = await PasswordResetEngine.CompleteResetAsync(
                    db, resetToken!, "the new password", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Assert.Equal(PasswordResetOutcome.Success, outcome);
            }

            Assert.Equal(0, await LiveTokenCountAsync(accountId));
            Assert.NotEqual(
                AuthenticationEngine.RefreshOutcome.Rotated,
                (await AuthenticationEngine.RedeemRefreshTokenAsync(_authOptions, stolen.Token)).Outcome);
            Assert.True(playerId > 0);
        }
    }
}

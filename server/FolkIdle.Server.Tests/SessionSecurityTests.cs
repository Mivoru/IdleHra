using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    [Collection("Postgres collection")]
    public class SessionSecurityTests
    {
        private readonly PostgresTestFixture _fixture;

        public SessionSecurityTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private const string TestSecret = "test-secret-key-for-jwt-signing-only";

        [Fact]
        public async Task Migration_AddsSessionNonceAndAuthMethodColumns()
        {
            Guid accountId = Guid.NewGuid();
            long playerId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = new PlayerRecord { PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() };
                db.PlayerRecords.Add(player);
                await db.SaveChangesAsync();
                playerId = player.Id;

                // Default is null - the bootstrap state, not a NOT NULL violation.
                Assert.Null(player.CurrentSessionNonce);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var reloaded = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
                Assert.Null(reloaded.CurrentSessionNonce);

                reloaded.CurrentSessionNonce = "abc123";
                db.PlayerRecords.Update(reloaded);
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var withNonce = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
                Assert.Equal("abc123", withNonce.CurrentSessionNonce);

                var token = new PlayerRefreshToken
                {
                    AccountId = accountId,
                    TokenHash = new byte[32],
                    IssuedEpoch = 1000L,
                    ExpiresAtEpoch = 2000L,
                    RevokedEpoch = 0L,
                    AuthMethod = "dev"
                };
                db.PlayerRefreshTokens.Add(token);
                await db.SaveChangesAsync();
            }
        }

        [Fact]
        public void GenerateJwt_RoundTripsAuthMethod()
        {
            string jwt = AuthenticationEngine.GenerateJwt(Guid.NewGuid(), "nonce1", "dev", TestSecret, out _);
            var result = AuthenticationEngine.ValidateJwt(jwt, TestSecret);

            Assert.True(result.IsValid);
            Assert.Equal("dev", result.AuthMethod);
        }

        [Fact]
        public void GenerateJwt_PasswordMethodRoundTrips()
        {
            string jwt = AuthenticationEngine.GenerateJwt(Guid.NewGuid(), "nonce2", "pw", TestSecret, out _);
            var result = AuthenticationEngine.ValidateJwt(jwt, TestSecret);

            Assert.Equal("pw", result.AuthMethod);
        }

        [Fact]
        public void ValidateJwt_TreatsAMissingMethodClaimAsPasswordAuthenticated()
        {
            // Hand-build a token the way a pre-this-plan server would have -
            // no "m" claim at all - to prove an in-flight token at deploy
            // time is not treated as the device-bearer case (which would
            // wrongly demand a step-up it never needed).
            Guid accountId = Guid.NewGuid();
            string oldStyleJwt = BuildLegacyJwtWithNoMethodClaim(accountId, "nonce3", TestSecret);
            var result = AuthenticationEngine.ValidateJwt(oldStyleJwt, TestSecret);

            Assert.True(result.IsValid);
            Assert.Equal("pw", result.AuthMethod);
        }

        private static string BuildLegacyJwtWithNoMethodClaim(Guid accountId, string nonce, string secretKey)
        {
            long exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400L;
            string header = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string payloadJson = "{\"aid\":\"" + accountId.ToString("N") + "\",\"nonce\":\"" + nonce + "\",\"exp\":" + exp + "}";
            string payload = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string signingInput = header + "." + payload;
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secretKey));
            byte[] sig = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signingInput));
            string sigSegment = System.Convert.ToBase64String(sig).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return signingInput + "." + sigSegment;
        }
    }
}

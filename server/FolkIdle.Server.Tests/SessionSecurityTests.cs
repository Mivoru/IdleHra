using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
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
    }
}

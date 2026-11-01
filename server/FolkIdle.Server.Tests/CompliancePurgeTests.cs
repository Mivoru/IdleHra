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
    /// The GDPR purge, end to end against Postgres.
    ///
    /// Modul: IT HAD NEVER WORKED (found 2026-11-01). One raw DELETE named a
    /// table that does not exist ("PasswordResetTokens"; the real one is
    /// password_reset_tokens), every purge threw there, and the catch rolled
    /// the transaction back and logged a single console line. Nothing tested
    /// the purge, so nothing noticed. This drives it against the real schema,
    /// where a wrong table name fails the test instead of a deletion request.
    /// </summary>
    [Collection("Postgres collection")]
    public class CompliancePurgeTests
    {
        private readonly PostgresTestFixture _fixture;

        public CompliancePurgeTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task APurgeErasesTheAccountAndItsResetTokens()
        {
            long playerId = 9_400_000 + Random.Shared.Next(1, 99_999);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    PlayerId = playerId,
                    TokenHash = Guid.NewGuid().ToString("N"),
                    CreatedAtEpoch = now,
                    ExpiresAtEpoch = now + 3600,
                });
                await db.SaveChangesAsync();
            }

            var engine = new CompliancePurgeEngine(_fixture.ServiceProvider, redis: null);
            bool purged = await engine.ExecuteGdprPurgeAsync(playerId);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.True(purged, "the purge reported failure - see the console line it printed");
            Assert.False(await verify.PlayerRecords.AnyAsync(p => p.Id == playerId), "the player row survived the purge");
            Assert.False(await verify.PasswordResetTokens.AnyAsync(t => t.PlayerId == playerId), "a password reset token survived the purge");
        }

        [Fact]
        public async Task PurgingAnUnknownAccountIsAQuietNo()
        {
            var engine = new CompliancePurgeEngine(_fixture.ServiceProvider, redis: null);
            Assert.False(await engine.ExecuteGdprPurgeAsync(9_399_999_999));
        }
    }
}

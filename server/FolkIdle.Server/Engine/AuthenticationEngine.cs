using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    public readonly struct JwtValidationResult
    {
        public readonly bool IsValid;
        public readonly Guid AccountId;
        public readonly string SessionNonce;
        public readonly long ExpirationEpoch;

        public JwtValidationResult(bool isValid, Guid accountId, string sessionNonce, long expirationEpoch)
        {
            IsValid = isValid;
            AccountId = accountId;
            SessionNonce = sessionNonce;
            ExpirationEpoch = expirationEpoch;
        }

        public static readonly JwtValidationResult Invalid = new JwtValidationResult(false, Guid.Empty, string.Empty, 0L);
    }

    public enum OAuthLinkOutcome
    {
        Success,
        InvalidToken,
        AccountNotFound,
        AlreadyLinked,
        ExternalIdentityInUse,
        Failed
    }

    // Modul: hand-rolled minimal JWT (RFC 7519 shape: base64url(header).
    // base64url(payload).base64url(HMACSHA256 signature)) - no external JWT
    // library dependency, matching this codebase's established preference
    // for self-contained primitives over a package dependency for a single,
    // narrow, well-understood algorithm (see ObfuscatedInt32/ObfuscatedInt64,
    // the hand-rolled XorShift32 PRNGs used throughout combat/genetics).
    // Claims are fixed and minimal: aid (AccountId), nonce (SessionNonce),
    // exp (expiration epoch seconds) - exactly what this task's Part 1
    // requires, nothing more.
    public static class AuthenticationEngine
    {
        // Matches the codebase's existing TokenFreshnessWindowSeconds
        // convention (NetworkBroadcastSystem's old ActiveTokenCache scheme).
        public const long TokenLifetimeSeconds = 86400L;

        private const string HeaderJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";

        public static string GenerateJwt(Guid accountId, string sessionNonce, string secretKey, out long expirationEpoch)
        {
            expirationEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TokenLifetimeSeconds;

            string headerSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson));
            string payloadJson = "{\"aid\":\"" + accountId.ToString("N") + "\",\"nonce\":\"" + sessionNonce + "\",\"exp\":" + expirationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            string payloadSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

            string signingInput = headerSegment + "." + payloadSegment;
            byte[] signature = ComputeSignature(signingInput, secretKey);
            string signatureSegment = Base64UrlEncode(signature);

            return signingInput + "." + signatureSegment;
        }

        // Modul: signature verified via CryptographicOperations.FixedTimeEquals
        // (constant-time comparison) rather than byte[] equality or string
        // comparison, so signature verification does not leak timing
        // information about how many leading bytes matched - standard
        // practice for any MAC/signature check against attacker-supplied
        // input.
        public static JwtValidationResult ValidateJwt(string token, string secretKey)
        {
            if (string.IsNullOrEmpty(token))
            {
                return JwtValidationResult.Invalid;
            }

            string[] parts = token.Split('.');
            if (parts.Length != 3)
            {
                return JwtValidationResult.Invalid;
            }

            byte[] providedSignature;
            byte[] payloadBytes;
            try
            {
                providedSignature = Base64UrlDecode(parts[2]);
                payloadBytes = Base64UrlDecode(parts[1]);
            }
            catch (FormatException)
            {
                return JwtValidationResult.Invalid;
            }

            string signingInput = parts[0] + "." + parts[1];
            byte[] expectedSignature = ComputeSignature(signingInput, secretKey);

            if (expectedSignature.Length != providedSignature.Length ||
                !CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
            {
                return JwtValidationResult.Invalid;
            }

            System.Text.Json.JsonDocument document;
            try
            {
                document = System.Text.Json.JsonDocument.Parse(payloadBytes);
            }
            catch (System.Text.Json.JsonException)
            {
                return JwtValidationResult.Invalid;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("aid", out var aidElement) ||
                    !document.RootElement.TryGetProperty("nonce", out var nonceElement) ||
                    !document.RootElement.TryGetProperty("exp", out var expElement))
                {
                    return JwtValidationResult.Invalid;
                }

                string? aidString = aidElement.GetString();
                if (aidString == null || !Guid.TryParseExact(aidString, "N", out Guid accountId))
                {
                    return JwtValidationResult.Invalid;
                }

                string sessionNonce = nonceElement.GetString() ?? string.Empty;
                if (sessionNonce.Length == 0)
                {
                    return JwtValidationResult.Invalid;
                }

                if (expElement.ValueKind != System.Text.Json.JsonValueKind.Number || !expElement.TryGetInt64(out long expirationEpoch))
                {
                    return JwtValidationResult.Invalid;
                }

                if (expirationEpoch <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    return JwtValidationResult.Invalid;
                }

                return new JwtValidationResult(true, accountId, sessionNonce, expirationEpoch);
            }
        }

        public static string GenerateSessionNonce()
        {
            return Guid.NewGuid().ToString("N");
        }

        private static byte[] ComputeSignature(string signingInput, string secretKey)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(string segment)
        {
            string padded = segment.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }

        // Modul: device-ID login-or-provision. Mirrors NetworkBroadcastSystem's
        // former AutoProvisionPlayerAsync account-creation shape exactly (same
        // starting level/lineage/resources), but gated behind this single
        // controlled REST entry point instead of being reachable by sending
        // any syntactically-valid, previously-unseen token straight to the
        // WebSocket handshake - that auto-provision-on-any-token path was the
        // exact vulnerability this task exists to close, so it has been
        // removed from the handshake, not preserved alongside this.
        public static async Task<(long PlayerId, Guid AccountId)> LoginOrProvisionAsync(RetryingDbContextOptions authOptions, string deviceId)
        {
            // A dedicated, retry-configured context is constructed here
            // rather than accepting a caller-supplied FolkIdleDbContext -
            // see RetryingDbContextOptions for why this path cannot share
            // the DbContextOptions every other engine resolves through
            // IDbContextFactory<FolkIdleDbContext>.
            await using var db = new FolkIdleDbContext(authOptions.Options);

            var existing = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.DeviceId == deviceId);
            if (existing != null)
            {
                return (existing.Id, existing.PlayerGuid);
            }

            // Modul: the provisioning transaction runs at Serializable
            // isolation, so concurrent inserts for different devices can
            // legitimately collide on Postgres's read/write dependency
            // graph (SQLSTATE 40001) even though they touch unrelated rows.
            // CreateExecutionStrategy().ExecuteAsync wraps the whole
            // check-then-insert unit so the Npgsql retrying strategy
            // configured in Program.cs can replay the entire delegate on
            // that failure - EF Core requires explicit transactions to be
            // scoped this way once a retrying strategy is registered.
            var executionStrategy = db.Database.CreateExecutionStrategy();
            return await executionStrategy.ExecuteAsync(async () =>
            {
                // A retried attempt may still be holding entities tracked
                // by a prior failed attempt against this same DbContext
                // instance - start every attempt from a clean slate.
                db.ChangeTracker.Clear();

                using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    Guid characterId = Guid.NewGuid();

                    var player = new PlayerRecord
                    {
                        CurrentLevel = 1,
                        CurrentXp = 0L,
                        SelectedLineageId = 1,
                        PlayerGuid = characterId,
                        DeviceId = deviceId,
                        LastLogoutTimestamp = 0L,
                        PremiumDiamonds = 0
                    };
                    db.PlayerRecords.Add(player);
                    await db.SaveChangesAsync();

                    db.CharacterRecords.Add(new CharacterRecord
                    {
                        Id = characterId,
                        PlayerId = player.Id,
                        Level = 1,
                        AgePhase = 1,
                        AgeTicks = 0L
                    });

                    db.CharacterLineages.Add(new CharacterLineageRegistry
                    {
                        CharacterId = characterId,
                        GenerationIndex = 0,
                        GeneticVector = RaceIds.Human
                    });

                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1000L });
                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = ContentRegistry.GetMaterialString(1), Quantity = 25L });

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    Console.WriteLine($"Auto-provisioned new player {player.Id} for device login.");
                    return (player.Id, characterId);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                {
                    // A concurrent first-login for the same device won the
                    // unique-index race. This is not a transient failure
                    // the execution strategy should replay - retrying an
                    // insert that violates a unique constraint fails
                    // identically every time - so it is handled here
                    // directly: roll back and return the winner's row,
                    // which is, from this request's point of view, actually
                    // a successful login rather than a hard failure.
                    await transaction.RollbackAsync();

                    var raced = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.DeviceId == deviceId);
                    if (raced != null)
                    {
                        return (raced.Id, raced.PlayerGuid);
                    }

                    Console.WriteLine($"Device login provisioning failed: {ex.Message}");
                    throw;
                }
            });
        }

        // Modul: resolves the PlayerRecord bound to accountId and updates
        // its OAuth identity - see PlayerRecord.ProviderType/
        // ExternalProviderId. Linking is irreversible: a player that has
        // already linked any provider cannot link a different one or
        // re-link the same one through this method (AlreadyLinked), and the
        // validated external identity cannot already belong to a different
        // account (ExternalIdentityInUse) - the composite unique index is
        // the final authority on that second case, catching a concurrent
        // double-link race that a pre-check alone could miss, not just a
        // convenience check.
        public static async Task<OAuthLinkOutcome> LinkOAuthAccountAsync(RetryingDbContextOptions authOptions, Guid accountId, string providerToken, IOAuthTokenValidator validator)
        {
            OAuthTokenValidationResult validation = validator.Validate(providerToken);
            if (!validation.IsValid)
            {
                return OAuthLinkOutcome.InvalidToken;
            }

            await using var db = new FolkIdleDbContext(authOptions.Options);
            var strategy = db.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                    try
                    {
                        var player = await db.PlayerRecords
                            .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"PlayerGuid\" = {0} FOR UPDATE", accountId)
                            .FirstOrDefaultAsync();

                        if (player == null)
                        {
                            await transaction.RollbackAsync();
                            return OAuthLinkOutcome.AccountNotFound;
                        }

                        if (player.ProviderType != 0)
                        {
                            await transaction.RollbackAsync();
                            return OAuthLinkOutcome.AlreadyLinked;
                        }

                        player.ProviderType = (int)validation.ProviderType;
                        player.ExternalProviderId = validation.ExternalProviderId;

                        await db.SaveChangesAsync();
                        await transaction.CommitAsync();
                        return OAuthLinkOutcome.Success;
                    }
                    catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                    {
                        // A concurrent link already claimed this exact
                        // (ProviderType, ExternalProviderId) pair for a
                        // different account - not retryable, a real
                        // conflict that will fail identically every time.
                        await transaction.RollbackAsync();
                        return OAuthLinkOutcome.ExternalIdentityInUse;
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OAuth link failed for account {accountId}: {ex.Message}");
                return OAuthLinkOutcome.Failed;
            }
        }

        // Modul: recovery login only - looks up an existing link, never
        // creates one. A read-only query, no transaction needed (matches
        // LoginOrProvisionAsync's own initial existence check above).
        public static async Task<(bool Found, long PlayerId, Guid AccountId)> TryLoginByOAuthAsync(RetryingDbContextOptions authOptions, string providerToken, IOAuthTokenValidator validator)
        {
            OAuthTokenValidationResult validation = validator.Validate(providerToken);
            if (!validation.IsValid)
            {
                return (false, 0L, Guid.Empty);
            }

            await using var db = new FolkIdleDbContext(authOptions.Options);
            int providerType = (int)validation.ProviderType;
            var existing = await db.PlayerRecords.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProviderType == providerType && p.ExternalProviderId == validation.ExternalProviderId);

            if (existing == null)
            {
                return (false, 0L, Guid.Empty);
            }

            return (true, existing.Id, existing.PlayerGuid);
        }
    }
}

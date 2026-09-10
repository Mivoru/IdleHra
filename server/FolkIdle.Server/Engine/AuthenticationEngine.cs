using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

    public enum EmailRegisterOutcome
    {
        Success,
        InvalidEmail,
        InvalidUsername,
        InvalidPassword,
        EmailInUse,
        UsernameInUse,
        Failed
    }

    // Modul: Email/Password Auth. Deliberately a single outcome for "no
    // such email" and "wrong password" - distinguishing them in the
    // response would let a caller enumerate which emails are registered.
    public enum EmailLoginOutcome
    {
        Success,
        InvalidCredentials
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

        // ------------------------------------------------------------------
        // REFRESH TOKENS
        //
        // Modul: THE 24-HOUR JWT MEANT "LOGGED OUT EVERY MORNING" IN A GAME
        // WHOSE ENTIRE PROPOSITION IS THAT IT RUNS WHILE YOU ARE AWAY.
        //
        // A browser tab absorbs that. A phone does not: it is a password prompt
        // every day, on the app whose selling point is that you do not have to
        // open it. The three answers were lengthening TokenLifetimeSeconds,
        // adding a refresh token, or a device token exchanged for short JWTs.
        //
        // The first is the one NOT taken, and the reason is worth writing down:
        // a JWT is a bearer credential that this server does not store, so
        // there is no revocation. A stolen 60-day JWT is valid for 60 days and
        // nothing anybody does - not a password change, not a sign-out, not
        // support - shortens that by a second. Every refresh token below is a
        // row, and a row can be deleted.
        //
        // TokenLifetimeSeconds IS DELIBERATELY UNCHANGED at 24 hours. Nothing
        // about a live session moves; what is new is a way to get the NEXT JWT
        // without a password.
        // ------------------------------------------------------------------

        /// <summary>
        /// Sixty days of not being asked for a password.
        /// </summary>
        /// <remarks>
        /// A number, not a principle - long enough that a player who opens the
        /// game most weekends is never signed out, short enough that a device
        /// left in a drawer stops being a way in. Every use rotates it and
        /// extends it by another sixty days from that moment, so this is an
        /// IDLE timeout: it measures time since the last time the game was
        /// opened, not time since the password was typed.
        /// </remarks>
        public const long RefreshTokenLifetimeSeconds = 60L * 86400L;

        /// <summary>
        /// A fresh refresh token: 32 bytes of CSPRNG output, base64url.
        /// </summary>
        /// <remarks>
        /// `RandomNumberGenerator`, never `Random` and never a Guid - a Guid is
        /// 122 bits of which the version and variant are fixed, and some
        /// implementations of it are not cryptographic at all. This is a
        /// credential and is generated like one.
        /// </remarks>
        public static string GenerateRefreshToken()
        {
            byte[] raw = RandomNumberGenerator.GetBytes(32);
            return Base64UrlEncode(raw);
        }

        /// <summary>The stored verifier for a raw token.</summary>
        public static byte[] HashRefreshToken(string rawToken)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(rawToken ?? string.Empty));
        }

        /// <summary>
        /// Issues a refresh token for an account and returns the RAW value -
        /// the only moment it exists anywhere outside the caller's device.
        /// </summary>
        public static async Task<(string Token, long ExpiresAtEpoch)> IssueRefreshTokenAsync(
            RetryingDbContextOptions authOptions, Guid accountId)
        {
            await using var db = new FolkIdleDbContext(authOptions.Options);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long expiresAt = now + RefreshTokenLifetimeSeconds;
            string raw = GenerateRefreshToken();

            db.PlayerRefreshTokens.Add(new PlayerRefreshToken
            {
                AccountId = accountId,
                TokenHash = HashRefreshToken(raw),
                IssuedEpoch = now,
                ExpiresAtEpoch = expiresAt,
                RevokedEpoch = 0L
            });

            await db.SaveChangesAsync();
            return (raw, expiresAt);
        }

        public enum RefreshOutcome
        {
            /// <summary>Spent, and a successor was issued.</summary>
            Rotated,
            /// <summary>No such token, or it was never valid.</summary>
            Unknown,
            /// <summary>Past its expiry. The player signs in again.</summary>
            Expired,
            /// <summary>
            /// Already spent or explicitly revoked. The whole family is gone by
            /// the time this is returned - see the remarks on the method.
            /// </summary>
            Replayed
        }

        /// <summary>
        /// Spends one refresh token and issues its successor.
        /// </summary>
        /// <remarks>
        /// Modul: ROTATION, AND WHY A REPLAY TAKES THE WHOLE ACCOUNT DOWN WITH
        /// IT.
        ///
        /// A refresh token is spent exactly once. Presenting a spent one has
        /// two possible causes and this server cannot tell them apart: the
        /// client lost the reply to its own last refresh and retried, or
        /// somebody else has a copy. Treating it as the first leaves a stolen
        /// credential working; treating it as the second signs one honest
        /// player out once. The second is the only defensible choice, so every
        /// live token for that account is revoked and the player signs in
        /// again.
        ///
        /// The read and the write are one SERIALIZABLE transaction because two
        /// simultaneous refreshes of the same token must not both succeed -
        /// that is the race the whole rotation scheme exists to detect.
        /// </remarks>
        public static async Task<(RefreshOutcome Outcome, Guid AccountId, string Token, long ExpiresAtEpoch)>
            RedeemRefreshTokenAsync(RetryingDbContextOptions authOptions, string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return (RefreshOutcome.Unknown, Guid.Empty, string.Empty, 0L);
            }

            byte[] hash = HashRefreshToken(rawToken);

            await using var db = new FolkIdleDbContext(authOptions.Options);

            // Modul: THE TRANSACTION HAS TO LIVE INSIDE THE EXECUTION STRATEGY,
            // and finding that out cost a 500 on a running server rather than a
            // red test.
            //
            // `authOptions` is the retry-configured options from Program.cs, and
            // EF Core refuses a user-initiated transaction once a retrying
            // strategy is registered - the whole unit has to be replayable,
            // because the strategy replays the DELEGATE, not the statement.
            // Every other explicit transaction in this file is already written
            // this way; this one was not, and the unit tests never saw it
            // because they build plain options with no strategy at all.
            //
            // Serializable is the isolation because two simultaneous refreshes
            // of the same token must not both succeed - that is exactly the race
            // the rotation scheme exists to detect, and 40001 here is a normal
            // outcome to be retried rather than a fault.
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                // A retried attempt may still hold entities tracked by the
                // previous failed one against this same context.
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable);

                var row = await db.PlayerRefreshTokens.SingleOrDefaultAsync(r => r.TokenHash == hash);
                if (row == null)
                {
                    await transaction.RollbackAsync();
                    return (RefreshOutcome.Unknown, Guid.Empty, string.Empty, 0L);
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Guid accountId = row.AccountId;

                if (row.RevokedEpoch != 0L)
                {
                    // The replay case. Everything this account holds goes.
                    await db.PlayerRefreshTokens
                        .Where(r => r.AccountId == accountId && r.RevokedEpoch == 0L)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.RevokedEpoch, now));
                    await transaction.CommitAsync();
                    return (RefreshOutcome.Replayed, Guid.Empty, string.Empty, 0L);
                }

                if (row.ExpiresAtEpoch <= now)
                {
                    row.RevokedEpoch = now;
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return (RefreshOutcome.Expired, Guid.Empty, string.Empty, 0L);
                }

                row.RevokedEpoch = now;

                string successor = GenerateRefreshToken();
                long expiresAt = now + RefreshTokenLifetimeSeconds;
                db.PlayerRefreshTokens.Add(new PlayerRefreshToken
                {
                    AccountId = accountId,
                    TokenHash = HashRefreshToken(successor),
                    IssuedEpoch = now,
                    ExpiresAtEpoch = expiresAt,
                    RevokedEpoch = 0L
                });

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                return (RefreshOutcome.Rotated, accountId, successor, expiresAt);
            });
        }

        /// <summary>
        /// Revokes one token, if it is still live. Signing out.
        /// </summary>
        /// <remarks>
        /// Deliberately silent about whether anything was found: the caller is
        /// a sign-out button and there is no answer it would act on
        /// differently. Saying "no such token" would also confirm which tokens
        /// exist to anyone who can reach the route.
        /// </remarks>
        public static async Task RevokeRefreshTokenAsync(RetryingDbContextOptions authOptions, string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken)) return;

            byte[] hash = HashRefreshToken(rawToken);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await using var db = new FolkIdleDbContext(authOptions.Options);
            await db.PlayerRefreshTokens
                .Where(r => r.TokenHash == hash && r.RevokedEpoch == 0L)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.RevokedEpoch, now));
        }

        /// <summary>
        /// Revokes everything an account holds. A password change, or a player
        /// who says a device was stolen.
        /// </summary>
        public static async Task RevokeAllRefreshTokensAsync(RetryingDbContextOptions authOptions, Guid accountId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await using var db = new FolkIdleDbContext(authOptions.Options);
            await db.PlayerRefreshTokens
                .Where(r => r.AccountId == accountId && r.RevokedEpoch == 0L)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.RevokedEpoch, now));
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

                    // Modul: breeding pairs. Was a single character. A lone
                    // starter is a dead end - BreedingEngine needs a male and a
                    // female of the same race, so a one-character account could
                    // never breed at all until it happened to be granted a
                    // second. Every account now starts with a Human pair; see
                    // CharacterGrantEngine.
                    CharacterGrantEngine.SeedStarterHumanPair(db, player.Id, characterId);

                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1000L });

                    // Modul: A NEW ACCOUNT NOW OWNS THE THREE BASIC TOOLS, and
                    // 25 copper ore no longer appears from nowhere.
                    //
                    // Gathering needs a tool that matches the job - an axe for
                    // wood, a pickaxe for ore, a rod for fish - and an account
                    // was created owning none of them, so the three professions
                    // the game asks a new player to use were all gated behind
                    // crafting something they had no materials for. The
                    // catalogue has carried normal_axe_tool, normal_pickaxe_tool
                    // and normal_fishing_rod_tool all along; nothing granted
                    // them.
                    //
                    // The ore went with it. It was seeded so that SOMETHING
                    // could be crafted on day one, which is the same problem
                    // solved by the wrong end - it read as a glitch to the
                    // player, because a pile of ore appearing in an empty
                    // account is one.
                    StarterEquipmentGrant.Seed(db, player.Id);

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

        // Modul: Email/Password Auth. Read-only "remember this device"
        // lookup - unlike LoginOrProvisionAsync, this NEVER auto-provisions
        // a new account for an unseen device. It is what the client's
        // silent relaunch check calls (see UiLoginWindow): a hit means the
        // device previously completed a real Register/LoginWithEmailAsync
        // and can skip straight past the login/register screen; a miss
        // means a login/register choice must be shown, not a fresh
        // anonymous account.
        public static async Task<(bool Found, long PlayerId, Guid AccountId)> TryLoginByDeviceIdAsync(RetryingDbContextOptions authOptions, string deviceId)
        {
            await using var db = new FolkIdleDbContext(authOptions.Options);
            var existing = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.DeviceId == deviceId);
            if (existing == null)
            {
                return (false, 0L, Guid.Empty);
            }

            return (true, existing.Id, existing.PlayerGuid);
        }

        // Modul: IsEmailAvailableAsync IS GONE, with the /api/v1/auth/
        // check-email endpoint it served. Unauthenticated and unthrottled, it
        // answered "does this address have an account here" - an enumeration
        // oracle, and one no screen in this game ever called. Registration
        // still refuses a duplicate address, which is where a real player
        // finds out.

        // Modul: Email/Password Auth. Creates a brand new account bound to
        // (Email, PasswordHash, Username) - mirrors LoginOrProvisionAsync's
        // starting-state shape (same initial level/lineage/resources)
        // exactly, but keyed by a real credential instead of an implicit
        // device ID. deviceId (optional) is bound onto the new row so the
        // registering device can silently re-authenticate on its next
        // launch via TryLoginByDeviceIdAsync, without the client needing to
        // resubmit the password.
        public static async Task<(EmailRegisterOutcome Outcome, long PlayerId, Guid AccountId)> RegisterWithEmailAsync(RetryingDbContextOptions authOptions, string email, string username, string password, string? deviceId)
        {
            string normalizedEmail = NormalizeEmail(email);
            if (!IsValidEmailFormat(normalizedEmail))
            {
                return (EmailRegisterOutcome.InvalidEmail, 0L, Guid.Empty);
            }

            string trimmedUsername = (username ?? string.Empty).Trim();
            if (trimmedUsername.Length < 3 || trimmedUsername.Length > 20)
            {
                return (EmailRegisterOutcome.InvalidUsername, 0L, Guid.Empty);
            }

            // Length only, eight minimum - see PasswordPolicy for why there is
            // no composition rule and why the maximum exists.
            if (!PasswordPolicy.IsAcceptable(password))
            {
                return (EmailRegisterOutcome.InvalidPassword, 0L, Guid.Empty);
            }

            string passwordHash = PasswordHasher.Hash(password);

            await using var db = new FolkIdleDbContext(authOptions.Options);
            var executionStrategy = db.Database.CreateExecutionStrategy();
            return await executionStrategy.ExecuteAsync(async () =>
            {
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
                        DeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId,
                        Email = normalizedEmail,
                        Username = trimmedUsername,
                        PasswordHash = passwordHash,
                        LastLogoutTimestamp = 0L,
                        PremiumDiamonds = 0
                    };
                    db.PlayerRecords.Add(player);
                    await db.SaveChangesAsync();

                    // Modul: breeding pairs. Was a single character. A lone
                    // starter is a dead end - BreedingEngine needs a male and a
                    // female of the same race, so a one-character account could
                    // never breed at all until it happened to be granted a
                    // second. Every account now starts with a Human pair; see
                    // CharacterGrantEngine.
                    CharacterGrantEngine.SeedStarterHumanPair(db, player.Id, characterId);

                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1000L });
                    // The same three tools the device path grants - see
                    // StarterEquipmentGrant. Seeding one registration route and
                    // not the other is exactly how accounts came to differ.
                    StarterEquipmentGrant.Seed(db, player.Id);

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    Console.WriteLine($"Registered new player {player.Id} via email.");
                    return (EmailRegisterOutcome.Success, player.Id, characterId);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                {
                    // Modul: a concurrent registration won the race on
                    // whichever unique index this attempt also targeted -
                    // not retryable (an insert violating a unique
                    // constraint fails identically every time), so this is
                    // handled directly rather than left for the execution
                    // strategy to replay. The Postgres constraint name
                    // tells the caller which field actually collided so the
                    // register screen can show an accurate error.
                    await transaction.RollbackAsync();

                    string constraintName = pgEx.ConstraintName ?? string.Empty;
                    if (constraintName.Contains("Email", StringComparison.OrdinalIgnoreCase))
                    {
                        return (EmailRegisterOutcome.EmailInUse, 0L, Guid.Empty);
                    }
                    if (constraintName.Contains("Username", StringComparison.OrdinalIgnoreCase))
                    {
                        return (EmailRegisterOutcome.UsernameInUse, 0L, Guid.Empty);
                    }

                    Console.WriteLine($"Email registration failed: {ex.Message}");
                    return (EmailRegisterOutcome.Failed, 0L, Guid.Empty);
                }
            });
        }

        // Modul: Email/Password Auth. Verifies (Email, Password) against
        // the stored PBKDF2 hash and, on success, rebinds the caller's
        // current deviceId onto the resolved account as the new "remember
        // me" anchor - logging in from a different device than the one
        // that registered is expected (a player switching phones),
        // DeviceId is a convenience shortcut here, not a security boundary
        // (the password check is). A failed rebind (the deviceId already
        // belongs to a different, unrelated row - e.g. an anonymous
        // LoginOrProvisionAsync account from before this player ever
        // registered) does not fail the login itself.
        public static async Task<(EmailLoginOutcome Outcome, long PlayerId, Guid AccountId)> LoginWithEmailAsync(RetryingDbContextOptions authOptions, string email, string password, string? deviceId)
        {
            string normalizedEmail = NormalizeEmail(email);

            await using var db = new FolkIdleDbContext(authOptions.Options);
            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Email == normalizedEmail);

            if (player == null || !PasswordHasher.Verify(password, player.PasswordHash))
            {
                return (EmailLoginOutcome.InvalidCredentials, 0L, Guid.Empty);
            }

            if (!string.IsNullOrWhiteSpace(deviceId) && player.DeviceId != deviceId)
            {
                player.DeviceId = deviceId;
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                {
                    Console.WriteLine($"DeviceId rebind skipped for player {player.Id}: {ex.Message}");
                }
            }

            return (EmailLoginOutcome.Success, player.Id, player.PlayerGuid);
        }

        private static string NormalizeEmail(string? email)
        {
            return (email ?? string.Empty).Trim().ToLowerInvariant();
        }

        // Modul: deliberately not a full RFC 5322 validator (that grammar
        // is notoriously over-engineered for real-world use) - just enough
        // shape-checking to reject obvious garbage before it reaches the
        // database: exactly one '@', not at either end, at least one '.'
        // somewhere after it.
        private static bool IsValidEmailFormat(string normalizedEmail)
        {
            if (string.IsNullOrEmpty(normalizedEmail) || normalizedEmail.Length > 254)
            {
                return false;
            }

            int atIndex = normalizedEmail.IndexOf('@');
            if (atIndex <= 0 || atIndex != normalizedEmail.LastIndexOf('@') || atIndex >= normalizedEmail.Length - 1)
            {
                return false;
            }

            return normalizedEmail.IndexOf('.', atIndex + 1) > atIndex + 1;
        }
    }
}

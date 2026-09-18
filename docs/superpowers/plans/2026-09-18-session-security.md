# Session Security Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a live session's identity revisitable after login - a logout, a password reset, or a detected refresh-token theft must invalidate the access token already issued, not just future refreshes - and require a fresh password check before a device-bearer session (one established by silent DeviceId auto-login, not a password) can complete a real-money purchase or link an external identity to an upgraded account.

**Architecture:** A per-account `CurrentSessionNonce` column, checked against the JWT's existing (currently inert) `nonce` claim on every authenticated REST call and WebSocket handshake, cached in memory the same way this file already caches AccountId->PlayerId. Bumping the nonce - on logout, password reset, or a detected refresh-token replay - forces every future validation of an older token to fail and immediately disconnects any live WebSocket for that account. A second JWT claim tags how the session was established (`"pw"` or `"dev"`), carried through refresh-token rotation via a new `AuthMethod` column, and gates two endpoints.

**Tech Stack:** C# / .NET 8, EF Core + Npgsql, xUnit + Testcontainers; Svelte 5 + TypeScript, vitest.

**Spec:** `docs/superpowers/specs/2026-09-18-session-security-design.md`

## Global Constraints

- `TokenLifetimeSeconds` (24h) and `RefreshTokenLifetimeSeconds` (60d) are UNCHANGED. This plan adds revocation, not a lifetime change.
- A `null` `CurrentSessionNonce` (the bootstrap/pre-migration state) always validates - nothing already logged in is force-signed-out by the migration itself.
- The `"m"` (method) JWT claim is OPTIONAL on read: a token minted before this ships has none, and `ValidateJwt` treats a missing claim as `"pw"` (the safer default - it only skips a step-up for the rest of that token's remaining life, never wrongly demands one).
- Step-up request field is **lowercase `password`**, matching `HandleBillingVerify`'s own existing lowercase `receipt` field - not `Password`. (The spec document says `Password`; this is a corrected, grounded value - the field must match this handler's established JSON casing, not the PascalCase some other, unrelated handlers use.)
- Step-up refusal is HTTP `403` with body `{"StepUpRequired":true}` - never `401`, because the session itself is valid; this is a request for proof, not re-authentication.
- Stop any running `FolkIdle.Server.exe` / `dotnet` before `dotnet build` or `dotnet test` (the stale-build hook blocks the whole command otherwise - run the stop in a SEPARATE command).
- `dotnet test` needs Docker running.
- Commit messages: write the message file with the Write tool and use `git commit -F <file>` - never PowerShell `Set-Content`/`Out-File` (it adds a UTF-8 BOM).
- Comments follow the repo's `// Modul:` convention: explain why, not what.
- Line numbers cited below were read from `main` at commit `84fe16c` (2026-09-18). Several tasks touch the same file in sequence and numbers will drift task to task - locate by function/method NAME, never by a stale line number.
- `HandleVerifyReceipt` and its route no longer exist (removed 2026-09-18, unrelated finding). The only REST purchase endpoint is `HandleBillingVerify` at `/api/v1/billing/verify`.

## File Map

**Modify (server)**
- `Models/PlayerRecord.cs` - `CurrentSessionNonce`.
- `Models/PlayerRefreshToken.cs` - `AuthMethod`.
- `Migrations/<timestamp>_AddSessionSecurity.cs` (+ Designer, snapshot) - generated.
- `Engine/AuthenticationEngine.cs` - `JwtValidationResult.AuthMethod`; `GenerateJwt`/`ValidateJwt` carry the `"m"` claim; `RevokeRefreshTokenAsync` returns the revoked token's `AccountId`; `IssueRefreshTokenAsync`/`RedeemRefreshTokenAsync` carry `AuthMethod`; three new nonce-persistence methods.
- `Engine/PasswordResetEngine.cs` - `CompleteResetAsync` sets the nonce and returns the account id.
- `Network/NetworkBroadcastSystem.cs` - nonce cache, the validation gate at both entry points, issuance/refresh/logout/reset/replay wiring, the step-up gate on two handlers, two stale comments corrected.
- `client_web/src/lib/net/billing.ts` - step-up outcome and retry-with-password.
- `client_web/src/routes/Store.svelte` - the password prompt.

**Modify (tests)**
- `server/FolkIdle.Server.Tests/RefreshTokenTests.cs` - two callers of the now `Task<Guid>` `RevokeRefreshTokenAsync`.
- `server/FolkIdle.Server.Tests/E2EGameLoopTest.cs` - new E2E cases.
- New: `server/FolkIdle.Server.Tests/SessionSecurityTests.cs` (pure + integration).
- `client_web/tests/billing.test.ts` - the new outcome kind.

---

### Task 1: Data model and migration

**Files:**
- Modify: `server/FolkIdle.Server/Models/PlayerRecord.cs`
- Modify: `server/FolkIdle.Server/Models/PlayerRefreshToken.cs`
- Create: `server/FolkIdle.Server/Migrations/<timestamp>_AddSessionSecurity.cs` (+ `.Designer.cs`, snapshot update) - generated by `dotnet ef migrations add`, not hand-written.
- Test: `server/FolkIdle.Server.Tests/SessionSecurityTests.cs` (new file, first test in it)

**Interfaces:**
- Produces: `PlayerRecord.CurrentSessionNonce` (`string?`, default `null`), `PlayerRefreshToken.AuthMethod` (`string`, default `""`).

- [ ] **Step 1: Add the two properties**

In `server/FolkIdle.Server/Models/PlayerRecord.cs`, find the `PasswordHash` property (`public string? PasswordHash { get; set; }`) and add directly after it:

```csharp
        // Modul: the JWT's `nonce` claim is checked against this on every
        // authenticated request. Null means no revocation event has ever
        // happened for this account - every token validates as it did before
        // this column existed, so deploying this migration does not force
        // every already-logged-in player to sign in again. See
        // AuthenticationEngine.BumpSessionNonceAsync and
        // NetworkBroadcastSystem.IsNonceCurrentAsync.
        public string? CurrentSessionNonce { get; set; }
```

In `server/FolkIdle.Server/Models/PlayerRefreshToken.cs`, add after `RevokedEpoch`:

```csharp
        /// <summary>
        /// How the login that first issued this token's family proved who it
        /// was: "pw" for a password or OAuth login, "dev" for a silent
        /// DeviceId auto-login. Carried onto the successor row on every
        /// rotation (see AuthenticationEngine.RedeemRefreshTokenAsync) - the
        /// method a session was established with does not change just
        /// because the token rotated.
        /// </summary>
        public string AuthMethod { get; set; } = string.Empty;
```

- [ ] **Step 2: Generate the migration**

Stop any running server first (separate command, the stale-build hook blocks this otherwise):

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
```

```powershell
cd server/FolkIdle.Server
dotnet ef migrations add AddSessionSecurity
# if that fails: dotnet tool install --global dotnet-ef --version 8.*
```

- [ ] **Step 3: Apply it locally and verify both columns exist**

```powershell
$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'
dotnet run --project . --migrate
```

Confirm no error. This requires the local Postgres container from `run-dev.ps1` to be up; if it is not, `docker compose -f ../../docker-compose.yml up -d postgres` first (or skip - Task 3's Testcontainers tests will exercise the migration against a fresh container regardless).

- [ ] **Step 4: Write the first test - the migration produces the right shape**

Create `server/FolkIdle.Server.Tests/SessionSecurityTests.cs`:

```csharp
using System;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    [Collection("Postgres")]
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
```

- [ ] **Step 5: Run it**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~SessionSecurityTests"
```

Expected: PASS. Requires Docker running.

- [ ] **Step 6: Commit**

```bash
git add server/FolkIdle.Server/Models/PlayerRecord.cs server/FolkIdle.Server/Models/PlayerRefreshToken.cs server/FolkIdle.Server/Migrations/ server/FolkIdle.Server.Tests/SessionSecurityTests.cs
git commit -F <message file>
```

---

### Task 2: JWT carries how a session was established (pure)

No DB, no network layer - `AuthenticationEngine` only.

**Files:**
- Modify: `server/FolkIdle.Server/Engine/AuthenticationEngine.cs`
- Test: `server/FolkIdle.Server.Tests/SessionSecurityTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `JwtValidationResult.AuthMethod` (`string`); `GenerateJwt(Guid accountId, string sessionNonce, string authMethod, string secretKey, out long expirationEpoch)` (authMethod inserted as the third parameter); `ValidateJwt` populates `AuthMethod` from the token's `"m"` claim, defaulting to `"pw"` when absent.

- [ ] **Step 1: Write the failing tests**

Add to `SessionSecurityTests.cs`:

```csharp
using FolkIdle.Server.Engine;

// ... inside the class:

        private const string TestSecret = "test-secret-key-for-jwt-signing-only";

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
```

- [ ] **Step 2: Run, confirm they fail to compile** (the third parameter and `AuthMethod` property don't exist yet).

- [ ] **Step 3: Implement**

In `AuthenticationEngine.cs`, `JwtValidationResult` struct: add the field and constructor parameter.

```csharp
    public readonly struct JwtValidationResult
    {
        public readonly bool IsValid;
        public readonly Guid AccountId;
        public readonly string SessionNonce;
        public readonly long ExpirationEpoch;
        public readonly string AuthMethod;

        public JwtValidationResult(bool isValid, Guid accountId, string sessionNonce, long expirationEpoch, string authMethod)
        {
            IsValid = isValid;
            AccountId = accountId;
            SessionNonce = sessionNonce;
            ExpirationEpoch = expirationEpoch;
            AuthMethod = authMethod;
        }

        public static readonly JwtValidationResult Invalid = new JwtValidationResult(false, Guid.Empty, string.Empty, 0L, string.Empty);
    }
```

`GenerateJwt` gains the parameter and claim:

```csharp
        public static string GenerateJwt(Guid accountId, string sessionNonce, string authMethod, string secretKey, out long expirationEpoch)
        {
            expirationEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TokenLifetimeSeconds;

            string headerSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson));
            string payloadJson = "{\"aid\":\"" + accountId.ToString("N") + "\",\"nonce\":\"" + sessionNonce + "\",\"m\":\"" + authMethod + "\",\"exp\":" + expirationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            string payloadSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

            string signingInput = headerSegment + "." + payloadSegment;
            byte[] signature = ComputeSignature(signingInput, secretKey);
            string signatureSegment = Base64UrlEncode(signature);

            return signingInput + "." + signatureSegment;
        }
```

`ValidateJwt`: after the existing required-claims block reads `sessionNonce`, add an OPTIONAL read for `"m"` (do not fail validation if it is absent), and pass it into the returned result:

```csharp
                string sessionNonce = nonceElement.GetString() ?? string.Empty;
                if (sessionNonce.Length == 0)
                {
                    return JwtValidationResult.Invalid;
                }

                // Modul: "m" is OPTIONAL, unlike aid/nonce/exp above. A token
                // minted before this claim existed has none, and treating
                // that as invalid would sign out every live session at
                // deploy time. Missing defaults to "pw" - the safer
                // direction, since it only skips a step-up this token never
                // needed rather than wrongly demanding one from a real
                // password session.
                string authMethod = document.RootElement.TryGetProperty("m", out var methodElement)
                    ? (methodElement.GetString() ?? "pw")
                    : "pw";

                if (expElement.ValueKind != System.Text.Json.JsonValueKind.Number || !expElement.TryGetInt64(out long expirationEpoch))
                {
                    return JwtValidationResult.Invalid;
                }

                if (expirationEpoch <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    return JwtValidationResult.Invalid;
                }

                return new JwtValidationResult(true, accountId, sessionNonce, expirationEpoch, authMethod);
```

- [ ] **Step 4: Run the three tests, confirm PASS**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~SessionSecurityTests"
```

Expected: `dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj` will show every OTHER `GenerateJwt(` call site as a compile ERROR now (wrong argument count) - that is expected and is NOT this task's job to fix. Confirm the build error list contains only `GenerateJwt(` call sites in `NetworkBroadcastSystem.cs`, nothing else, then leave them - Tasks 5 and 6 fix them.

- [ ] **Step 5: Commit**

```bash
git add server/FolkIdle.Server/Engine/AuthenticationEngine.cs server/FolkIdle.Server.Tests/SessionSecurityTests.cs
git commit -F <message file>
```

---

### Task 3: Revocable refresh tokens and nonce persistence (engine layer)

**Files:**
- Modify: `server/FolkIdle.Server/Engine/AuthenticationEngine.cs`
- Modify: `server/FolkIdle.Server.Tests/RefreshTokenTests.cs` (two call sites)
- Test: `server/FolkIdle.Server.Tests/SessionSecurityTests.cs`

**Interfaces:**
- Consumes: Task 1's `PlayerRecord.CurrentSessionNonce`, `PlayerRefreshToken.AuthMethod`.
- Produces:
  - `RevokeRefreshTokenAsync(RetryingDbContextOptions, string rawToken) -> Task<Guid>` (was `Task`) - the token's `AccountId`, or `Guid.Empty` if not found/already revoked.
  - `IssueRefreshTokenAsync(RetryingDbContextOptions, Guid accountId, string authMethod) -> Task<(string Token, long ExpiresAtEpoch)>` (authMethod is a new third parameter).
  - `RedeemRefreshTokenAsync(RetryingDbContextOptions, string rawToken) -> Task<(RefreshOutcome Outcome, Guid AccountId, string Token, long ExpiresAtEpoch, string AuthMethod)>` (widened; `Replayed` now carries the real `AccountId`, not `Guid.Empty`).
  - `SetCurrentSessionNonceAsync(RetryingDbContextOptions, Guid accountId, string nonce) -> Task`.
  - `BumpSessionNonceAsync(RetryingDbContextOptions, Guid accountId) -> Task<string>` (generates a nonce, persists it, returns it).
  - `GetCurrentSessionNonceAsync(RetryingDbContextOptions, Guid accountId) -> Task<string?>`.

- [ ] **Step 1: Write the failing tests**

Add to `SessionSecurityTests.cs`:

```csharp
        [Fact]
        public async Task NoncePersistence_SetThenGetRoundTrips()
        {
            Guid accountId = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            string? beforeSet = await AuthenticationEngine.GetCurrentSessionNonceAsync(_fixture.RetryingOptions, accountId);
            Assert.Null(beforeSet);

            await AuthenticationEngine.SetCurrentSessionNonceAsync(_fixture.RetryingOptions, accountId, "n-abc");
            string? afterSet = await AuthenticationEngine.GetCurrentSessionNonceAsync(_fixture.RetryingOptions, accountId);
            Assert.Equal("n-abc", afterSet);
        }

        [Fact]
        public async Task BumpSessionNonceAsync_ProducesADifferentStoredValue()
        {
            Guid accountId = Guid.NewGuid();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid(), CurrentSessionNonce = "old" });
                await db.SaveChangesAsync();
            }

            string bumped = await AuthenticationEngine.BumpSessionNonceAsync(_fixture.RetryingOptions, accountId);

            Assert.NotEqual("old", bumped);
            string? stored = await AuthenticationEngine.GetCurrentSessionNonceAsync(_fixture.RetryingOptions, accountId);
            Assert.Equal(bumped, stored);
        }

        [Fact]
        public async Task RevokeRefreshTokenAsync_ReturnsTheAccountIdItRevoked()
        {
            Guid accountId = Guid.NewGuid();
            var (rawToken, _) = await AuthenticationEngine.IssueRefreshTokenAsync(_fixture.RetryingOptions, accountId, "pw");

            Guid revokedAccountId = await AuthenticationEngine.RevokeRefreshTokenAsync(_fixture.RetryingOptions, rawToken);

            Assert.Equal(accountId, revokedAccountId);
        }

        [Fact]
        public async Task RevokeRefreshTokenAsync_UnknownTokenReturnsEmptyGuid()
        {
            Guid revokedAccountId = await AuthenticationEngine.RevokeRefreshTokenAsync(_fixture.RetryingOptions, "not-a-real-token");
            Assert.Equal(Guid.Empty, revokedAccountId);
        }

        [Fact]
        public async Task RedeemRefreshTokenAsync_CarriesAuthMethodThroughRotation()
        {
            Guid accountId = Guid.NewGuid();
            var (rawToken, _) = await AuthenticationEngine.IssueRefreshTokenAsync(_fixture.RetryingOptions, accountId, "dev");

            var result = await AuthenticationEngine.RedeemRefreshTokenAsync(_fixture.RetryingOptions, rawToken);

            Assert.Equal(AuthenticationEngine.RefreshOutcome.Rotated, result.Outcome);
            Assert.Equal("dev", result.AuthMethod);
        }

        [Fact]
        public async Task RedeemRefreshTokenAsync_ReplayReturnsTheRealAccountId()
        {
            Guid accountId = Guid.NewGuid();
            var (rawToken, _) = await AuthenticationEngine.IssueRefreshTokenAsync(_fixture.RetryingOptions, accountId, "pw");

            var first = await AuthenticationEngine.RedeemRefreshTokenAsync(_fixture.RetryingOptions, rawToken);
            Assert.Equal(AuthenticationEngine.RefreshOutcome.Rotated, first.Outcome);

            // Presenting the SAME (now-spent) token again is the replay case.
            var replay = await AuthenticationEngine.RedeemRefreshTokenAsync(_fixture.RetryingOptions, rawToken);

            Assert.Equal(AuthenticationEngine.RefreshOutcome.Replayed, replay.Outcome);
            Assert.Equal(accountId, replay.AccountId);
        }
```

Check `PostgresTestFixture`/`_fixture` exposes `RetryingOptions` (a `RetryingDbContextOptions`) - grep other test files in this project for `_fixture.RetryingOptions` to confirm the exact property name before using it; several existing tests in `HardenedEngineIntegrationTests.cs` already construct engines with `_fixture.RetryingOptions`, so it exists.

- [ ] **Step 2: Run, confirm compile/assert failures**

- [ ] **Step 3: Implement the three new nonce methods**

Add near `GenerateSessionNonce()` in `AuthenticationEngine.cs`:

```csharp
        /// <summary>
        /// Sets an already-generated nonce as the account's current one.
        /// Used at every point a JWT is issued (login, register, refresh) -
        /// the caller already minted the nonce for the token it is about to
        /// hand back, and this is what makes that value the one
        /// NetworkBroadcastSystem.IsNonceCurrentAsync checks future requests
        /// against.
        /// </summary>
        public static async Task SetCurrentSessionNonceAsync(RetryingDbContextOptions authOptions, Guid accountId, string nonce)
        {
            await using var db = new FolkIdleDbContext(authOptions.Options);
            await db.PlayerRecords
                .Where(p => p.PlayerGuid == accountId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.CurrentSessionNonce, nonce));
        }

        /// <summary>
        /// Generates a fresh nonce nobody has been issued yet and makes it
        /// the account's current one - so every token minted before this
        /// call now fails the next check. Used by logout, password reset,
        /// and refresh-token replay detection: the three revocation events
        /// that have no already-generated nonce to reuse the way a login
        /// does.
        /// </summary>
        public static async Task<string> BumpSessionNonceAsync(RetryingDbContextOptions authOptions, Guid accountId)
        {
            string nonce = GenerateSessionNonce();
            await SetCurrentSessionNonceAsync(authOptions, accountId, nonce);
            return nonce;
        }

        /// <summary>
        /// Cold-path lookup for NetworkBroadcastSystem's in-memory cache on a
        /// miss. Null if the account does not exist or has never had a
        /// revocation event (see the column's own doc comment).
        /// </summary>
        public static async Task<string?> GetCurrentSessionNonceAsync(RetryingDbContextOptions authOptions, Guid accountId)
        {
            await using var db = new FolkIdleDbContext(authOptions.Options);
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.PlayerGuid == accountId);
            return player?.CurrentSessionNonce;
        }
```

- [ ] **Step 4: `RevokeRefreshTokenAsync` returns the `AccountId`**

Change the signature and both return points:

```csharp
        public static async Task<Guid> RevokeRefreshTokenAsync(RetryingDbContextOptions authOptions, string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken)) return Guid.Empty;

            byte[] hash = HashRefreshToken(rawToken);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await using var db = new FolkIdleDbContext(authOptions.Options);
            var row = await db.PlayerRefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash && r.RevokedEpoch == 0L);
            if (row == null)
            {
                return Guid.Empty;
            }

            row.RevokedEpoch = now;
            await db.SaveChangesAsync();
            return row.AccountId;
        }
```

(This changes from the bulk `ExecuteUpdateAsync` to a tracked entity load, because the `AccountId` now has to travel back to the caller - a single-row lookup-then-save, same cost shape as `RedeemRefreshTokenAsync` already uses elsewhere in this file.)

- [ ] **Step 5: `IssueRefreshTokenAsync` takes and stores `authMethod`**

```csharp
        public static async Task<(string Token, long ExpiresAtEpoch)> IssueRefreshTokenAsync(
            RetryingDbContextOptions authOptions, Guid accountId, string authMethod)
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
                RevokedEpoch = 0L,
                AuthMethod = authMethod
            });

            await db.SaveChangesAsync();
            return (raw, expiresAt);
        }
```

- [ ] **Step 6: `RedeemRefreshTokenAsync` carries `AuthMethod` and fixes the `Replayed` AccountId**

Widen the return tuple everywhere in this method (its signature and all four `return` statements):

```csharp
        public static async Task<(RefreshOutcome Outcome, Guid AccountId, string Token, long ExpiresAtEpoch, string AuthMethod)>
            RedeemRefreshTokenAsync(RetryingDbContextOptions authOptions, string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return (RefreshOutcome.Unknown, Guid.Empty, string.Empty, 0L, string.Empty);
            }

            byte[] hash = HashRefreshToken(rawToken);

            await using var db = new FolkIdleDbContext(authOptions.Options);

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable);

                var row = await db.PlayerRefreshTokens.SingleOrDefaultAsync(r => r.TokenHash == hash);
                if (row == null)
                {
                    await transaction.RollbackAsync();
                    return (RefreshOutcome.Unknown, Guid.Empty, string.Empty, 0L, string.Empty);
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Guid accountId = row.AccountId;
                string authMethod = row.AuthMethod;

                if (row.RevokedEpoch != 0L)
                {
                    // Modul: the replay case now returns the REAL AccountId
                    // (it used to return Guid.Empty) so the caller -
                    // HandleAuthRefresh - can bump that account's session
                    // nonce and force-disconnect its live socket, not just
                    // revoke future refreshes. See the session-security spec,
                    // section 3.
                    await db.PlayerRefreshTokens
                        .Where(r => r.AccountId == accountId && r.RevokedEpoch == 0L)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.RevokedEpoch, now));
                    await transaction.CommitAsync();
                    return (RefreshOutcome.Replayed, accountId, string.Empty, 0L, string.Empty);
                }

                if (row.ExpiresAtEpoch <= now)
                {
                    row.RevokedEpoch = now;
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return (RefreshOutcome.Expired, Guid.Empty, string.Empty, 0L, string.Empty);
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
                    RevokedEpoch = 0L,
                    AuthMethod = authMethod
                });

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                return (RefreshOutcome.Rotated, accountId, successor, expiresAt, authMethod);
            });
        }
```

- [ ] **Step 7: Fix the two existing test callers**

`RefreshTokenTests.cs:248` and `:269` call `await AuthenticationEngine.RevokeRefreshTokenAsync(_authOptions, phone.Token);` as a bare statement. That still compiles against a `Task<Guid>` return (the value is simply discarded) - **run the file's tests to confirm**, no code change should be needed here, but verify rather than assume.

- [ ] **Step 8: Run all of Task 2 and Task 3's tests**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~SessionSecurityTests|FullyQualifiedName~RefreshTokenTests"
```

Expected: PASS. `dotnet build` at this point will still show errors at `NetworkBroadcastSystem.cs`'s `GenerateJwt(`, `RedeemRefreshTokenAsync(`, and `IssueRefreshTokenAsync(`/`TryIssueRefreshTokenAsync(` call sites - expected, fixed in Tasks 5-9.

- [ ] **Step 9: Commit**

```bash
git add server/FolkIdle.Server/Engine/AuthenticationEngine.cs server/FolkIdle.Server.Tests/SessionSecurityTests.cs
git commit -F <message file>
```

---

### Task 4: The validation gate (NetworkBroadcastSystem)

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`
- Test: `server/FolkIdle.Server.Tests/E2EGameLoopTest.cs`

**Interfaces:**
- Consumes: `AuthenticationEngine.GetCurrentSessionNonceAsync`, `JwtValidationResult.SessionNonce`/`AccountId`.
- Produces: `private async Task<bool> IsNonceCurrentAsync(Guid accountId, string presentedNonce)`; wired into `TryResolveAuthenticatedPlayerAsync` (REST) and the WebSocket auth handshake block.

This task cannot fully compile in isolation (Tasks 5/6/7/8/9 still owe fixes elsewhere in this same file for the `GenerateJwt`/refresh/issue call sites) - build errors OUTSIDE the lines this task touches are expected and not this task's to fix. Confirm with a narrow build check in Step 4.

- [ ] **Step 1: Add the cache and the gate helper**

Near `_accountIdToPlayerIdCache` (`private readonly ConcurrentDictionary<Guid, long> _accountIdToPlayerIdCache = new();`), add:

```csharp
        // Modul: mirrors _accountIdToPlayerIdCache immediately above - avoid a
        // DB round trip on every authenticated request. Unlike that cache,
        // this one DOES need invalidation: a revocation event (logout,
        // password reset, refresh-token replay) writes a new value here at
        // the moment it bumps the DB column, so this server's own writes
        // never go stale. A miss (this pod just started, or the account has
        // never been looked up here) falls through to the DB and populates
        // the cache either way, including with null.
        private readonly ConcurrentDictionary<Guid, string?> _accountCurrentNonce = new();

        private async Task<bool> IsNonceCurrentAsync(Guid accountId, string presentedNonce)
        {
            if (!_accountCurrentNonce.TryGetValue(accountId, out string? currentNonce))
            {
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                currentNonce = await AuthenticationEngine.GetCurrentSessionNonceAsync(authOptions, accountId);
                _accountCurrentNonce[accountId] = currentNonce;
            }

            // Modul: null is the bootstrap state (see PlayerRecord.
            // CurrentSessionNonce's own doc comment) - no revocation event
            // has ever happened for this account, so every token validates
            // exactly as it did before this column existed.
            return currentNonce == null || currentNonce == presentedNonce;
        }

        /// <summary>
        /// Writes a just-bumped nonce into the cache and immediately
        /// disconnects this account's live WebSocket, if it has one - so a
        /// stolen access token stops working on its very next use rather
        /// than only once its normal 24-hour clock runs out.
        /// </summary>
        private async Task EvictAccountSessionAsync(Guid accountId, string newNonce)
        {
            _accountCurrentNonce[accountId] = newNonce;
            long playerId = await ResolvePlayerIdFromAccountIdAsync(accountId);
            if (playerId > 0L)
            {
                ForceDisconnect(playerId);
            }
        }
```

- [ ] **Step 2: Wire the REST path**

In `TryResolveAuthenticatedPlayerAsync`, between the existing `if (!result.IsValid) return 0L;` and the final `return await ResolvePlayerIdFromAccountIdAsync(result.AccountId);`, add:

```csharp
            string token = bearerHeader.Substring(bearerPrefix.Length);
            JwtValidationResult result = AuthenticationEngine.ValidateJwt(token, _jwtSecretKey);
            if (!result.IsValid)
            {
                return 0L;
            }

            if (!await IsNonceCurrentAsync(result.AccountId, result.SessionNonce))
            {
                return 0L;
            }

            return await ResolvePlayerIdFromAccountIdAsync(result.AccountId);
```

- [ ] **Step 3: Wire the WebSocket handshake**

In the handshake block, between `JwtValidationResult validation = AuthenticationEngine.ValidateJwt(jwtToken, _jwtSecretKey);` / `if (!validation.IsValid) { ...close... }` and `long resolvedPlayerId = await ResolvePlayerIdFromAccountIdAsync(validation.AccountId);`, add:

```csharp
                    JwtValidationResult validation = AuthenticationEngine.ValidateJwt(jwtToken, _jwtSecretKey);
                    if (!validation.IsValid)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid or expired token", CancellationToken.None);
                        return;
                    }

                    if (!await IsNonceCurrentAsync(validation.AccountId, validation.SessionNonce))
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session revoked", CancellationToken.None);
                        return;
                    }

                    long resolvedPlayerId = await ResolvePlayerIdFromAccountIdAsync(validation.AccountId);
```

- [ ] **Step 4: Narrow build check**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj 2>&1 | Select-String "error"
```

Confirm every remaining error is at a `GenerateJwt(`, `RedeemRefreshTokenAsync(`, or `IssueRefreshTokenAsync(`/`TryIssueRefreshTokenAsync(` call site (Tasks 5-9's territory), and that nothing you touched in Steps 1-3 introduced a NEW error.

- [ ] **Step 5: Write the failing E2E test** (will not compile/pass until Task 5 exists, since it needs a real login to get a token with a matching nonce stored - write it now, it becomes green once Task 5 lands)

Add to `E2EGameLoopTest.cs`, following the same pattern as `Test_E2E_Billing_UnsafeVerifyReceiptRouteIsGone`:

```csharp
        [Fact]
        public async Task Test_E2E_SessionSecurity_TamperedNonceIsRejected()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping session security E2E verification because Docker is unavailable. CI must provide Docker for mandatory database coverage.");
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options =>
                options.UseNpgsql(_dbContainer.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var retryingOptions = serviceProvider.GetRequiredService<RetryingDbContextOptions>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            Guid accountId = Guid.NewGuid();
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid(), CurrentSessionNonce = "the-real-nonce" });
                await db.SaveChangesAsync();
            }

            // A token whose embedded nonce does not match the stored value -
            // exactly what a token issued before a revocation event looks
            // like from the validator's point of view.
            string mismatchedJwt = AuthenticationEngine.GenerateJwt(accountId, "a-different-nonce", "pw", AuthenticationDefaults.LocalDevelopmentFallback, out _);
            string matchingJwt = AuthenticationEngine.GenerateJwt(accountId, "the-real-nonce", "pw", AuthenticationDefaults.LocalDevelopmentFallback, out _);

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8085/");
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            networkSystem.Start();

            using var httpClient = new System.Net.Http.HttpClient();

            try
            {
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mismatchedJwt);
                var rejected = await httpClient.GetAsync("http://localhost:8085/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);

                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", matchingJwt);
                var accepted = await httpClient.GetAsync("http://localhost:8085/api/v1/market/listings?pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }
```

Check `/api/v1/market/listings` answers 401 (not some other code) for an unauthenticated caller today by reading its handler before trusting this - if it does not require auth, pick a route from this file that does (`TryResolveAuthenticatedPlayerAsync` is called by several; grep for its call sites and pick one with a simple GET).

- [ ] **Step 6: Run it, confirm it PASSES already** (Task 4 alone is sufficient for this specific test - it only exercises the validate-side gate against a hand-built token and a directly-seeded DB row, not real login issuance).

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_E2E_SessionSecurity_TamperedNonceIsRejected"
```

If it does not compile because of unrelated errors elsewhere in `NetworkBroadcastSystem.cs`, that confirms Task 4 cannot be isolated from Tasks 5/6 after all - proceed to Task 5 immediately and treat 4+5 as one review unit; note this in the ledger.

- [ ] **Step 7: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/E2EGameLoopTest.cs
git commit -F <message file>
```

---

### Task 5: Login and registration persist the nonce and the method

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`

**Interfaces:**
- Consumes: Task 3's `SetCurrentSessionNonceAsync`, `IssueRefreshTokenAsync(..., authMethod)`; Task 2's `GenerateJwt(..., authMethod, ...)`.
- Produces: every login/registration issues a JWT whose embedded nonce is durably the account's current one, tagged with the real method.

This is the task that finally makes the whole server compile again for the login/register paths.

- [ ] **Step 1: `HandleAuthLogin` - determine `authMethod` per branch**

Find the four-branch `if (!string.IsNullOrWhiteSpace(oauthProviderToken)) ... else if (email/password) ... else if (rememberedDeviceId) ... else if (deviceId) ...` block (search for `TryLoginByOAuthAsync` to locate it). Change `Guid accountId;` to also declare the method, and set it in every branch:

```csharp
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                Guid accountId;
                string authMethod;

                if (!string.IsNullOrWhiteSpace(oauthProviderToken))
                {
                    var validator = _serviceProvider.GetRequiredService<IOAuthTokenValidator>();
                    var oauthResult = await AuthenticationEngine.TryLoginByOAuthAsync(authOptions, oauthProviderToken, validator);
                    if (!oauthResult.Found)
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                    accountId = oauthResult.AccountId;
                    authMethod = "pw";
                }
                else if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrEmpty(password))
                {
                    var emailResult = await AuthenticationEngine.LoginWithEmailAsync(authOptions, email, password, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);
                    if (emailResult.Outcome != EmailLoginOutcome.Success)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        return;
                    }
                    accountId = emailResult.AccountId;
                    authMethod = "pw";
                }
                else if (!string.IsNullOrWhiteSpace(rememberedDeviceId) && rememberedDeviceId.Length <= 128)
                {
                    var rememberedResult = await AuthenticationEngine.TryLoginByDeviceIdAsync(authOptions, rememberedDeviceId);
                    if (!rememberedResult.Found)
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                    accountId = rememberedResult.AccountId;
                    authMethod = "dev";
                }
                else if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Length <= 128)
                {
                    (_, accountId) = await AuthenticationEngine.LoginOrProvisionAsync(authOptions, deviceId);
                    authMethod = "dev";
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }
```

A few lines below (still inside `HandleAuthLogin`), the nonce/JWT/refresh block:

```csharp
                await DailyLoginRewardEngine.TryGrantLoginRewardAsync(authOptions, accountId);

                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, accountId, sessionNonce);
                _accountCurrentNonce[accountId] = sessionNonce;
                string token = AuthenticationEngine.GenerateJwt(accountId, sessionNonce, authMethod, _jwtSecretKey, out long expiresAtEpoch);

                var refresh = await TryIssueRefreshTokenAsync(authOptions, accountId, authMethod);
```

(`TryIssueRefreshTokenAsync` gains the parameter in Step 3 below.)

- [ ] **Step 2: `HandleAuthRegister` - always `"pw"`**

Find its `GenerateJwt` call site (search `HandleAuthRegister`, the handler right after the deleted `check-email` comment block). Change:

```csharp
                await DailyLoginRewardEngine.TryGrantLoginRewardAsync(authOptions, result.AccountId);

                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, result.AccountId, sessionNonce);
                _accountCurrentNonce[result.AccountId] = sessionNonce;
                string token = AuthenticationEngine.GenerateJwt(result.AccountId, sessionNonce, "pw", _jwtSecretKey, out long expiresAtEpoch);

                var refresh = await TryIssueRefreshTokenAsync(authOptions, result.AccountId, "pw");
```

- [ ] **Step 3: `TryIssueRefreshTokenAsync` passes `authMethod` through**

```csharp
        private async Task<(string Token, long ExpiresAtEpoch)> TryIssueRefreshTokenAsync(
            RetryingDbContextOptions authOptions, Guid accountId, string authMethod)
        {
            try
            {
                return await AuthenticationEngine.IssueRefreshTokenAsync(authOptions, accountId, authMethod);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Refresh token issue failed for account {accountId}: {ex.Message}");
                return (string.Empty, 0L);
            }
        }
```

- [ ] **Step 4: Fix the two stale comments claiming the nonce feeds eviction**

At the `GenerateSessionNonce()` call inside `HandleAuthLogin` (the one you just edited in Step 1) - if it still carries the old "Modul: a fresh SessionNonce, exactly as a password login mints one. The nonce is what the Redis eviction check uses..." comment, replace it:

```csharp
                // Modul: THE NONCE DOES NOT FEED SESSION EVICTION - that is a
                // SEPARATE mechanism (RedisPlayerSessionLock, its own
                // per-connection RedisLockToken, see SubscribeToSessionEviction
                // and the WebSocket handshake's ForceAcquireAndEvictAsync
                // call). An earlier comment here claimed otherwise; it was
                // wrong. This nonce exists so a logout/reset/replay event -
                // none of which are a new login - can invalidate an access
                // token nothing else touches. See IsNonceCurrentAsync.
```

There is a second copy of the same claim near `LoginOrProvisionAsync`'s device-login path (search the string `"Redis eviction check"` across the whole file - fix every occurrence found, there should be exactly two before this task and zero after).

- [ ] **Step 5: Build and run**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj 2>&1 | Select-String "error"
```

Remaining errors should now be confined to the refresh endpoint (`HandleAuthRefresh`) and `HandleAuthRevoke`/`HandleResetPassword`/`HandleBillingVerify`/`HandleOAuthLink` - Tasks 6-10.

```powershell
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~Test_E2E_SessionSecurity_TamperedNonceIsRejected"
```

Expected: PASS (this test only needed the gate from Task 4; confirm it still is).

- [ ] **Step 6: Add and run a login-issues-a-durable-nonce test**

Add to `SessionSecurityTests.cs` (needs `NetworkBroadcastSystem` - follow the `E2EGameLoopTest.cs` pattern instead if this test class is not set up for a live HTTP server; if so, put this test in `E2EGameLoopTest.cs` instead and name it `Test_E2E_SessionSecurity_LoginPersistsNonceDurably`):

```csharp
        [Fact]
        public async Task Test_E2E_SessionSecurity_LoginPersistsNonceDurably()
        {
            // ... same server-startup boilerplate as Task 4's test, port 8086 ...
            // POST /api/v1/auth/login with a fresh deviceId, capture the returned Token.
            // Assert AuthenticationEngine.GetCurrentSessionNonceAsync(retryingOptions, thatAccountId)
            // is non-null and equals the nonce embedded in the returned JWT
            // (decode it with AuthenticationEngine.ValidateJwt using the same secret).
        }
```

Write out the full body following `Test_E2E_MarketBrowser_PaginatedListingsAndBoundsValidation`'s setup shape exactly (service collection, migrate, start `NetworkBroadcastSystem`, `HttpClient`, try/finally with `networkSystem.Stop()`), POSTing `{"deviceId": "<a new guid string>"}` to `/api/v1/auth/login`, decoding the response's `Token` field with `AuthenticationEngine.ValidateJwt`, and asserting `result.AccountId`'s stored nonce (via `AuthenticationEngine.GetCurrentSessionNonceAsync`) equals `result.SessionNonce`.

- [ ] **Step 7: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/E2EGameLoopTest.cs
git commit -F <message file>
```

---

### Task 6: Refresh reissuance carries the nonce and the method forward

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`

**Interfaces:**
- Consumes: Task 3's widened `RedeemRefreshTokenAsync` (now returns `AuthMethod`), `SetCurrentSessionNonceAsync`.

- [ ] **Step 1: `HandleAuthRefresh` - success path**

Find the `Rotated`-only success path (after the `if (result.Outcome != AuthenticationEngine.RefreshOutcome.Rotated)` early-return block). Replace:

```csharp
                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, result.AccountId, sessionNonce);
                _accountCurrentNonce[result.AccountId] = sessionNonce;
                string jwt = AuthenticationEngine.GenerateJwt(result.AccountId, sessionNonce, result.AuthMethod, _jwtSecretKey, out long expiresAtEpoch);

                var response = new AuthLoginResponse
                {
                    Token = jwt,
                    ExpiresAtEpoch = expiresAtEpoch,
                    RefreshToken = result.Token,
                    RefreshExpiresAtEpoch = result.ExpiresAtEpoch
                };
```

- [ ] **Step 2: Build**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj 2>&1 | Select-String "error"
```

Remaining errors confined to `HandleAuthRevoke`/`HandleResetPassword`/`HandleBillingVerify`/`HandleOAuthLink` (none should exist there yet - it is fine if there are zero errors left; Tasks 7-10 do not require prior errors to exist, they add new behavior).

- [ ] **Step 3: Write and run the carries-method-through-rotation E2E test**

Add `Test_E2E_SessionSecurity_RefreshCarriesAuthMethod` to `E2EGameLoopTest.cs`: log in with a fresh `deviceId` (yields `"dev"`), capture the refresh token, POST it to `/api/v1/auth/refresh`, decode the NEW access token, assert its `AuthMethod` (via `ValidateJwt`) is still `"dev"`.

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~SessionSecurity"
```

Expected: all session-security tests so far PASS.

- [ ] **Step 4: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/E2EGameLoopTest.cs
git commit -F <message file>
```

---

### Task 7: Logout revokes the access token, not just future refreshes

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`

**Interfaces:**
- Consumes: Task 3's `RevokeRefreshTokenAsync -> Task<Guid>`, `BumpSessionNonceAsync`; Task 4's `EvictAccountSessionAsync`.

- [ ] **Step 1: `HandleAuthRevoke`**

```csharp
        private async Task HandleAuthRevoke(HttpListenerContext context)
        {
            try
            {
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

                string rawToken = string.Empty;
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    if (parsed.RootElement.TryGetProperty("refreshToken", out var t))
                    {
                        rawToken = t.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // A malformed body is still a sign-out. Nothing to revoke.
                }

                Guid revokedAccountId = await AuthenticationEngine.RevokeRefreshTokenAsync(authOptions, rawToken);
                if (revokedAccountId != Guid.Empty)
                {
                    // Modul: the refresh token being gone was already true.
                    // What was missing is this: the ACCESS token this device
                    // is still holding stays valid for up to 24 more hours
                    // unless something invalidates it too. Bump and evict so
                    // "sign out" actually ends the session everywhere, not
                    // just the ability to silently get a new one.
                    string newNonce = await AuthenticationEngine.BumpSessionNonceAsync(authOptions, revokedAccountId);
                    await EvictAccountSessionAsync(revokedAccountId, newNonce);
                }

                context.Response.StatusCode = 204;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth revoke error: {ex}");
                context.Response.StatusCode = 204;
            }

            context.Response.Close();
        }
```

- [ ] **Step 2: Write and run the E2E test**

Add `Test_E2E_SessionSecurity_LogoutRevokesTheLiveAccessToken` to `E2EGameLoopTest.cs`: log in, capture `Token` and `RefreshToken`, open a raw WebSocket to this test server's port with the auth handshake using `Token` and confirm it stays open (send/receive a no-op or just check `WebSocketState.Open` after a short delay), POST `RefreshToken` to `/api/v1/auth/revoke`, then assert (a) a GET to an authenticated REST endpoint with the OLD `Token` now returns 401, and (b) the WebSocket's state is `Closed` or `CloseReceived` shortly after.

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~LogoutRevokes"
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/E2EGameLoopTest.cs
git commit -F <message file>
```

---

### Task 8: Password reset revokes the access token, not just refresh tokens

**Files:**
- Modify: `server/FolkIdle.Server/Engine/PasswordResetEngine.cs`
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`

**Interfaces:**
- Produces: `CompleteResetAsync(...) -> Task<(PasswordResetOutcome Outcome, Guid AccountId, string NewNonce)>` (was `Task<PasswordResetOutcome>`). `NewNonce` is `string.Empty` on every non-`Success` outcome.

- [ ] **Step 1: `CompleteResetAsync` sets the nonce and returns the account id and the nonce**

Widen the signature and every return point (there are four early returns - `InvalidToken` x2, `AlreadyUsed`, `Expired` - plus the success path):

```csharp
        public static async Task<(PasswordResetOutcome Outcome, Guid AccountId, string NewNonce)> CompleteResetAsync(
            FolkIdleDbContext db, string token, string newPassword, long nowEpoch)
        {
            if (string.IsNullOrEmpty(token)) return (PasswordResetOutcome.InvalidToken, Guid.Empty, string.Empty);

            if (!PasswordPolicy.IsAcceptable(newPassword)) return (PasswordResetOutcome.InvalidPassword, Guid.Empty, string.Empty);

            string hash = HashToken(token);

            var row = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (row == null) return (PasswordResetOutcome.InvalidToken, Guid.Empty, string.Empty);

            if (row.UsedAtEpoch != 0L) return (PasswordResetOutcome.AlreadyUsed, Guid.Empty, string.Empty);
            if (row.ExpiresAtEpoch <= nowEpoch) return (PasswordResetOutcome.Expired, Guid.Empty, string.Empty);

            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == row.PlayerId);
            if (player == null) return (PasswordResetOutcome.InvalidToken, Guid.Empty, string.Empty);

            player.PasswordHash = PasswordHasher.Hash(newPassword);
            player.DeviceId = null;

            // Modul: THE ACCESS TOKEN IS CUT LOOSE TOO, not only the device
            // anchor and the refresh tokens below - a JWT already issued used
            // to outlive a password reset it invalidated, for up to 24 more
            // hours. Same nonce mechanism as logout and refresh-token replay
            // (AuthenticationEngine.BumpSessionNonceAsync); done inline here,
            // on the already-tracked `player` entity, so it rides the same
            // SaveChangesAsync as the password hash change rather than
            // costing a second round trip. The caller needs the value back
            // (rather than re-reading it) to update its own in-memory cache
            // and evict the live socket without a second DB round trip.
            string newNonce = AuthenticationEngine.GenerateSessionNonce();
            player.CurrentSessionNonce = newNonce;

            row.UsedAtEpoch = nowEpoch;

            await db.SaveChangesAsync();

            await db.PlayerRefreshTokens
                .Where(t => t.AccountId == player.PlayerGuid && t.RevokedEpoch == 0L)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedEpoch, nowEpoch));

            return (PasswordResetOutcome.Success, player.PlayerGuid, newNonce);
        }
```

- [ ] **Step 2: `HandleResetPassword` evicts using the returned account id and nonce**

```csharp
                long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var (outcome, accountId, newNonce) = await Engine.PasswordResetEngine.CompleteResetAsync(db, token, newPassword, nowEpoch);

                if (outcome == Engine.PasswordResetOutcome.Success)
                {
                    // Modul: the nonce is already persisted - CompleteResetAsync
                    // wrote it in the same save as the password hash. This is
                    // cache-plus-disconnect only, no second DB write.
                    await EvictAccountSessionAsync(accountId, newNonce);
                }

                context.Response.StatusCode = outcome switch
                {
                    Engine.PasswordResetOutcome.Success => 200,
                    Engine.PasswordResetOutcome.InvalidPassword => 422,
                    Engine.PasswordResetOutcome.Expired => 410,
                    Engine.PasswordResetOutcome.AlreadyUsed => 410,
                    _ => 400,
                };
```

- [ ] **Step 3: Build**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj 2>&1 | Select-String "error"
```

Fix any other caller of `CompleteResetAsync` the build surfaces (grep `CompleteResetAsync` across `server/` first to know how many exist before you start).

- [ ] **Step 4: Write and run the E2E test**

`Test_E2E_SessionSecurity_PasswordResetRevokesTheLiveAccessToken`: register with email+password, capture `Token`, generate a reset token the way an existing password-reset test in this codebase does (grep for `PasswordResetEngine.BeginResetAsync` usage in tests), complete the reset via `HandleResetPassword`, assert the OLD `Token` now 401s on an authenticated REST call.

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~PasswordResetRevokes"
```

- [ ] **Step 5: Commit**

```bash
git add server/FolkIdle.Server/Engine/PasswordResetEngine.cs server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/E2EGameLoopTest.cs
git commit -F <message file>
```

---

### Task 9: Refresh-token replay revokes the access token too

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`

**Interfaces:**
- Consumes: Task 3's `RedeemRefreshTokenAsync` now returning the real `AccountId` on `Replayed`.

- [ ] **Step 1: `HandleAuthRefresh` - the `Replayed` branch**

```csharp
                var result = await AuthenticationEngine.RedeemRefreshTokenAsync(authOptions, rawToken);
                if (result.Outcome != AuthenticationEngine.RefreshOutcome.Rotated)
                {
                    if (result.Outcome == AuthenticationEngine.RefreshOutcome.Replayed)
                    {
                        Console.WriteLine("Refresh token replay detected; all sessions for that account revoked.");

                        // Modul: "all sessions" used to mean only the refresh
                        // half - RedeemRefreshTokenAsync already revoked every
                        // refresh token for this account before returning
                        // Replayed. The access token survived that, for up to
                        // 24 more hours, until this bump.
                        string newNonce = await AuthenticationEngine.BumpSessionNonceAsync(authOptions, result.AccountId);
                        await EvictAccountSessionAsync(result.AccountId, newNonce);
                    }

                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }
```

- [ ] **Step 2: Write and run the E2E test**

`Test_E2E_SessionSecurity_RefreshReplayRevokesTheLiveAccessToken`: log in, capture `Token` and `RefreshToken`, redeem `RefreshToken` once (getting a successor and a new access token), then POST the ORIGINAL (now-spent) `RefreshToken` again to `/api/v1/auth/refresh` (the replay), assert 401, then assert the FIRST `Token` (captured at login, still within its 24h life) now also 401s on an authenticated REST call.

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~ReplayRevokes"
```

- [ ] **Step 3: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/E2EGameLoopTest.cs
git commit -F <message file>
```

---

### Task 10: Step-up gate on purchase verification and OAuth linking

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs`

**Interfaces:**
- Consumes: `JwtValidationResult.AuthMethod` (via `TryResolveAuthenticatedPlayerAsync` - note this method today returns only a `long playerId`, not the full `JwtValidationResult`; see Step 1).
- Produces: both handlers respond `403 {"StepUpRequired":true}` when a device-bearer session on a password-holding account omits or gets wrong the `password` field.

- [ ] **Step 1: `TryResolveAuthenticatedPlayerAsync` needs to expose `AuthMethod`, not just `playerId`**

Both gated handlers currently call `long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);` and have no way to learn the token's method from that. Add a second method rather than changing the first (its other seven-plus callers only need the id):

```csharp
        private async Task<(long PlayerId, string AuthMethod)> TryResolveAuthenticatedPlayerWithMethodAsync(HttpListenerRequest request)
        {
            const string bearerPrefix = "Bearer ";
            string bearerHeader = request.Headers["Authorization"] ?? string.Empty;
            if (bearerHeader.Length <= bearerPrefix.Length || !bearerHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (0L, string.Empty);
            }

            string token = bearerHeader.Substring(bearerPrefix.Length);
            JwtValidationResult result = AuthenticationEngine.ValidateJwt(token, _jwtSecretKey);
            if (!result.IsValid || !await IsNonceCurrentAsync(result.AccountId, result.SessionNonce))
            {
                return (0L, string.Empty);
            }

            long playerId = await ResolvePlayerIdFromAccountIdAsync(result.AccountId);
            return (playerId, result.AuthMethod);
        }

        /// <summary>
        /// True if this session was established by a real password/OAuth
        /// login (or the JWT predates the "m" claim, defaulted safe) and
        /// therefore never needs a step-up; false only for a device-bearer
        /// session, and even then only when the account has a password to
        /// step up TO.
        /// </summary>
        private async Task<bool> RequiresPasswordStepUpAsync(long playerId, string authMethod)
        {
            if (authMethod != "dev") return false;

            await using var db = await _contextFactory.CreateDbContextAsync();
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            return player?.PasswordHash != null;
        }

        private static async Task<bool> VerifyStepUpPasswordAsync(FolkIdleDbContext db, long playerId, string suppliedPassword)
        {
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
            return player != null && PasswordHasher.Verify(suppliedPassword, player.PasswordHash);
        }

        private static void WriteStepUpRequired(HttpListenerContext context)
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("{\"StepUpRequired\":true}");
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
```

Check this class's field name for the context factory (`_contextFactory` was used inside `TryResolveAuthenticatedPlayerAsync`'s sibling `ResolvePlayerIdFromAccountIdAsync` - confirm the exact field name there before using it here) and whether `PasswordHasher` is already `using`d in this file (it is referenced via `AuthenticationEngine`-adjacent code elsewhere - grep to confirm the namespace is reachable unqualified or needs `Engine.PasswordHasher`).

- [ ] **Step 2: Gate `HandleBillingVerify`**

```csharp
        private async Task HandleBillingVerify(HttpListenerContext context)
        {
            try
            {
                var (playerId, authMethod) = await TryResolveAuthenticatedPlayerWithMethodAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (_billingVerificationEngine == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);

                if (await RequiresPasswordStepUpAsync(playerId, authMethod))
                {
                    string suppliedPassword = payload.TryGetProperty("password", out var pwElement) ? (pwElement.GetString() ?? string.Empty) : string.Empty;
                    await using var stepUpDb = await _contextFactory.CreateDbContextAsync();
                    if (suppliedPassword.Length == 0 || !await VerifyStepUpPasswordAsync(stepUpDb, playerId, suppliedPassword))
                    {
                        WriteStepUpRequired(context);
                        context.Response.Close();
                        return;
                    }
                }

                if (!payload.TryGetProperty("receipt", out var receiptElement))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string base64Receipt = receiptElement.GetString() ?? string.Empty;
                bool success = await _billingVerificationEngine.VerifyReceiptAsync(playerId, base64Receipt);
                context.Response.StatusCode = success ? 200 : 409;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Billing verify error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }
```

- [ ] **Step 3: Gate `HandleOAuthLink`**

This handler parses its body with `JsonDocument.Parse`, not `JsonSerializer.Deserialize<JsonElement>` like `HandleBillingVerify` - read `password` inside the SAME `using var document = ...` block that already reads `oauthProviderToken`, since `document` is disposed at the end of that block:

```csharp
        private async Task HandleOAuthLink(HttpListenerContext context)
        {
            try
            {
                var (playerId, authMethod) = await TryResolveAuthenticatedPlayerWithMethodAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                Guid accountId = await ResolveAccountIdAsync(playerId);

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

                string oauthProviderToken;
                string suppliedPassword;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (!document.RootElement.TryGetProperty("oauthProviderToken", out var tokenElement))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }
                    oauthProviderToken = tokenElement.GetString() ?? string.Empty;
                    suppliedPassword = document.RootElement.TryGetProperty("password", out var pwElement)
                        ? (pwElement.GetString() ?? string.Empty)
                        : string.Empty;
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (await RequiresPasswordStepUpAsync(playerId, authMethod))
                {
                    await using var stepUpDb = await _contextFactory.CreateDbContextAsync();
                    if (suppliedPassword.Length == 0 || !await VerifyStepUpPasswordAsync(stepUpDb, playerId, suppliedPassword))
                    {
                        WriteStepUpRequired(context);
                        context.Response.Close();
                        return;
                    }
                }

                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                var validator = _serviceProvider.GetRequiredService<IOAuthTokenValidator>();
                OAuthLinkOutcome outcome = await AuthenticationEngine.LinkOAuthAccountAsync(authOptions, accountId, oauthProviderToken, validator);

                context.Response.StatusCode = outcome switch
                {
                    OAuthLinkOutcome.Success => 200,
                    OAuthLinkOutcome.InvalidToken => 400,
                    OAuthLinkOutcome.AccountNotFound => 404,
                    OAuthLinkOutcome.AlreadyLinked => 409,
                    OAuthLinkOutcome.ExternalIdentityInUse => 409,
                    _ => 500
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"OAuth link error: {ex}");
                context.Response.StatusCode = 500;
            }
            context.Response.Close();
        }
```

(The trailing `catch`/`context.Response.Close()` were already there - shown here only so the whole method reads as one piece; do not duplicate them.)

- [ ] **Step 4: Write and run the tests**

Add to `SessionSecurityTests.cs` or `HardenedEngineIntegrationTests.cs` (wherever `BillingVerificationEngine` is already constructed for a test - follow that pattern):

- A device-bearer player with a password set, posting no `password` field to `/api/v1/billing/verify`, gets 403 with `StepUpRequired: true`.
- The same player with the WRONG password gets 403.
- The same player with the CORRECT password proceeds to the normal receipt-validation outcome (200/409, whatever an invalid-but-well-formed receipt gets today - it should NOT be 403).
- A passwordless guest device-bearer player succeeds without any `password` field.
- A password-authenticated (`"pw"`) session succeeds without any `password` field even if the account has a password.

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~StepUp"
```

- [ ] **Step 5: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/
git commit -F <message file>
```

---

### Task 11: Client step-up flow for purchases

**Files:**
- Modify: `client_web/src/lib/net/billing.ts`
- Modify: `client_web/src/routes/Store.svelte`
- Modify: `client_web/tests/billing.test.ts`

**Interfaces:**
- Consumes: server's `403 {"StepUpRequired":true}` from `/api/v1/billing/verify`.
- Produces: `PurchaseOutcome` gains `{ kind: 'stepUpRequired' }`; `submitReceipt(base64Receipt, password?: string)`.

- [ ] **Step 1: Widen `PurchaseOutcome` and `submitReceipt`**

```typescript
export type PurchaseOutcome =
  | { kind: 'granted' }
  | { kind: 'unavailable'; reason: string }
  | { kind: 'cancelled' }
  | { kind: 'stepUpRequired' }
  | { kind: 'rejected'; reason: string };
```

```typescript
export async function submitReceipt(base64Receipt: string, password?: string): Promise<PurchaseOutcome> {
  try {
    const body: { receipt: string; password?: string } = { receipt: base64Receipt };
    if (password) body.password = password;
    await authedPost('/api/v1/billing/verify', body);
  } catch (err) {
    const status = (err as { status?: number }).status;
    if (status === 403) {
      return { kind: 'stepUpRequired' };
    }
    if (status === 409) {
      return { kind: 'rejected', reason: 'The store receipt was not accepted. It may already have been used.' };
    }
    if (status === 503) {
      return { kind: 'unavailable', reason: 'Purchases are not configured on this server.' };
    }
    return { kind: 'rejected', reason: 'Could not reach the server to confirm the purchase.' };
  }

  // ... existing balance pull-through unchanged ...
}
```

`purchase()` (the function `Store.svelte` calls) needs the receipt available to retry with a password, since `submitReceipt` is currently the only thing that saw it. Check `purchase()`'s current body - it calls `submitReceipt(receipt)` once and returns its outcome directly. Change it to return the `stepUpRequired` outcome up through unchanged (no retry logic inside `purchase()` itself - the retry with a password is a SEPARATE exported function the UI calls once it has one):

```typescript
/**
 * Retries a purchase that came back stepUpRequired, this time with the
 * account's password. Separate from `purchase()` because only the UI layer
 * has anywhere to ask for a password - this file never prompts for anything.
 */
export async function retryReceiptWithPassword(base64Receipt: string, password: string): Promise<PurchaseOutcome> {
  return submitReceipt(base64Receipt, password);
}
```

This means `purchase()`/`buy()` needs to hold onto the receipt bytes across the step-up round trip. Check `purchase()`'s current implementation for where `receipt` is obtained (the store adapter's `purchase()` call) - it is a local variable already in scope at the point `submitReceipt(receipt)` is called; `purchase()` (the exported billing.ts function) should return the outcome from `submitReceipt` unchanged, AND the caller (`Store.svelte`) needs the raw receipt too if the outcome is `stepUpRequired`. Widen `purchase()`'s return slightly instead of hiding the receipt:

```typescript
export async function purchase(productIdentifier: string): Promise<PurchaseOutcome & { receipt?: string }> {
  // ... existing body unchanged up to the point it has `receipt` ...
  const outcome = await submitReceipt(receipt);
  return outcome.kind === 'stepUpRequired' ? { ...outcome, receipt } : outcome;
}
```

Read the actual current body of `purchase()` (lines ~95-121 as of this plan's baseline) before writing this edit - it already has cancellation/error handling around obtaining `receipt` from the adapter; only the final return needs this change, nothing about the adapter-calling logic above it.

- [ ] **Step 2: `Store.svelte` prompts for a password on `stepUpRequired`**

```svelte
  let stepUpPending = $state<{ productIdentifier: string; receipt: string } | null>(null);
  let stepUpPassword = $state('');
  let stepUpError = $state<string | null>(null);

  async function buy(productIdentifier: string) {
    buying = productIdentifier;
    try {
      const outcome = await purchase(productIdentifier);
      if (outcome.kind === 'granted') {
        play('levelUp');
        pushLocalNotice('Purchase confirmed - your diamonds are on the way.', 'info');
      } else if (outcome.kind === 'cancelled') {
        // Deliberately silent.
      } else if (outcome.kind === 'stepUpRequired' && outcome.receipt) {
        stepUpPending = { productIdentifier, receipt: outcome.receipt };
        stepUpPassword = '';
        stepUpError = null;
      } else {
        pushLocalNotice(outcome.reason);
      }
    } finally {
      buying = null;
    }
  }

  async function confirmStepUp() {
    if (!stepUpPending) return;
    const { receipt } = stepUpPending;
    const outcome = await retryReceiptWithPassword(receipt, stepUpPassword);
    if (outcome.kind === 'granted') {
      play('levelUp');
      pushLocalNotice('Purchase confirmed - your diamonds are on the way.', 'info');
      stepUpPending = null;
    } else if (outcome.kind === 'stepUpRequired') {
      stepUpError = 'Wrong password. Try again.';
    } else if (outcome.kind === 'rejected') {
      stepUpError = outcome.reason;
    }
  }
```

Import `retryReceiptWithPassword` alongside the existing `purchase, purchaseUnavailableReason` import. Add markup (an `{#if stepUpPending}` block per CLAUDE.md's own rule against `<details>`-style hidden panels - use `{#if}`, not a collapsed/hidden element) with a `type="password"` input bound to `stepUpPassword`, a Confirm button calling `confirmStepUp()`, a Cancel button setting `stepUpPending = null`, and `{#if stepUpError}` showing `stepUpError`. Match this file's existing style (plain markup, no component library) - look at how `cannotBuy` is rendered nearby for the pattern to follow.

- [ ] **Step 3: Update the client test**

In `billing.test.ts`, the existing `'sends the receipt VERBATIM...'` test still passes unchanged (it does not hit a step-up path). Add:

```typescript
  it('surfaces a step-up prompt distinctly from an ordinary rejection', async () => {
    postStatus = 403;
    const outcome = await purchase('diamonds_small');
    expect(outcome.kind).toBe('stepUpRequired');
  });

  it('retries with a password and can succeed', async () => {
    postStatus = null;
    const outcome = await retryReceiptWithPassword('BASE64RECEIPT', 'correct-horse-battery-staple');
    expect(outcome.kind).toBe('granted');
    expect(posted[posted.length - 1].body).toEqual({ receipt: 'BASE64RECEIPT', password: 'correct-horse-battery-staple' });
  });
```

Import `retryReceiptWithPassword` in the test's destructured import alongside `purchase, submitReceipt, ...`.

- [ ] **Step 4: Run**

```powershell
cd client_web
npx vitest run tests/billing.test.ts
npm run check:ratchet
```

Expected: all billing tests pass; typecheck count still 4 (the pre-existing `GuildOps.svelte` baseline), no new errors from `Store.svelte`.

- [ ] **Step 5: Commit**

```bash
git add client_web/src/lib/net/billing.ts client_web/src/routes/Store.svelte client_web/tests/billing.test.ts
git commit -F <message file>
```

---

### Task 12: Full verification and docs

**Files:**
- Modify: `docs/architecture/NEXT_STEPS_BACKLOG.md` (handoff entry)
- Modify: `docs/TASK_BOARD.md` (mark tasks 14 and 15 done)

- [ ] **Step 1: Full server suite**

```powershell
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
```

Expected: all green except the pre-existing, environment-only `Test_ContentValidatorScript_MalformedJson_ExitsNonZero` failure IF this machine's Python install is broken (confirmed broken on 2026-09-18 - `ModuleNotFoundError: No module named 'encodings'`, unrelated to this plan; CI's Python is fine). Any OTHER failure is this plan's to fix before proceeding.

- [ ] **Step 2: Client checks**

```powershell
cd client_web
npm run check:ratchet
npx vitest run
```

- [ ] **Step 3: `npm run exercise` against a local dev stack**

Per CLAUDE.md, this is how gameplay changes get verified, not smoke-screens. Follow the `run-stack` skill to bring up the local stack, then:

```powershell
cd client_web
npm run exercise
```

Confirm the login/purchase/logout flows this plan touched still work end to end (the script signs in, spends things, and asserts state changed - it does not specifically drive a purchase, since purchases need a store adapter this dev environment does not have; confirm the run completes with no NEW failures relative to its known baseline).

- [ ] **Step 4: Update the docs**

In `docs/architecture/NEXT_STEPS_BACKLOG.md`, add a new `HANDOFF 2026-09-1X` entry (use today's actual date) summarizing: the nonce-based revocation mechanism, what it fixed, the corrected discovery that `RevokeAllRefreshTokensAsync` remains dead code (still true - this plan never gave it a caller either; `CompleteResetAsync` revokes inline, matching the existing pattern rather than the unused helper), and that step-up now gates `HandleBillingVerify`/`HandleOAuthLink`.

In `docs/TASK_BOARD.md`, mark tasks 14 and 15 (`## 14.` and `## 15.` headers, in the "OPEN — 14 through 23" section) with a `**DONE <date>**` line each, following the exact style already used for task 16 (`**DONE 2026-09-17, deployed 2026-09-18 (...)**`).

- [ ] **Step 5: Commit**

```bash
git add docs/architecture/NEXT_STEPS_BACKLOG.md docs/TASK_BOARD.md
git commit -F <message file>
```

---

## Deploying this plan

Per the `deploy` skill: this release DOES carry a migration (`AddSessionSecurity`, additive only - two nullable/defaulted columns, no existing row rewritten). Take a Supabase backup first anyway, per this project's own precedent ("every breeding release has"). After merge, the controller (not a task subagent) runs the `deploy` skill and confirms with the user before shipping, same as every other deploy in this project.

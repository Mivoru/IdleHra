# DeviceId step-up auth (audit #15) — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement fix-shape (b) from `docs/TASK_BOARD.md` section "## 15.
DeviceId is a bearer credential with no proof of possession" — cap what a
device-bearer session (one established by `TryLoginByDeviceIdAsync`/
`LoginOrProvisionAsync`, not by a password) can do, by requiring a fresh
password re-check ("step-up") before the account's riskiest actions, even
though the device-bearer session is otherwise fully authenticated. This is
the board's own recommended, cheaper v1, explicitly instead of fix-shape (a)
(a device-bound rotating refresh token replacing DeviceId's bare authority),
which stays out of scope.

---

## OPEN DECISION — confirm with the owner before implementing Task 3

The board's Done-when criterion names three example commands
("password-change, email-change, and purchase/redemption"). Researching the
live code (2026-09-17) found that **two of those three do not exist as
features in this codebase at all**:

- There is **no password-change flow**. `PasswordPolicy.cs`'s own doc comment
  says so directly: "ENFORCED AT REGISTRATION ONLY... there is no
  change-password flow to walk them through it." The only password-adjacent
  flow is forgot-password (`POST /api/v1/auth/request-password-reset` +
  `POST /api/v1/auth/reset-password`, `PasswordResetEngine.CompleteResetAsync`),
  which is deliberately **unauthenticated** — it works precisely because the
  caller does not have (and is not trusted to have) a live session, proven
  instead by owning the account's email inbox. A device-bearer session gate
  cannot apply to a flow that never required a session in the first place, so
  this is out of scope by construction, not an oversight.
- There is **no email-change flow**. `Email` is set once at registration
  (`AuthenticationEngine.RegisterWithEmailAsync`) and nothing in the server
  ever writes to it again.

Below is this plan's own researched candidate list — the closest real
matches to the board's intent, found by grepping every
`TryResolveAuthenticatedPlayerAsync`-gated REST handler and every WebSocket
`CommandType`. **This list, and only this list, is what Task 3 gates — pick,
edit, or replace any entry before implementation starts.** The mechanism in
Tasks 1, 2, 4 and 5 is fully decided and does not depend on which of these
end up chosen; Task 3 is the one task whose diff is literally "add one `if
(!await TryRequireFreshPasswordAsync(...)) return;` line per approved
handler," so re-scoping the list later is cheap.

| # | Candidate | Where | Why it's the closest match |
|---|---|---|---|
| 1 | **Purchase confirmation** | `HandleBillingVerify`, `POST /api/v1/billing/verify` (`NetworkBroadcastSystem.cs:7638`), the signature-checked IAP path that credits real-money diamonds. Already gated by `TryResolveAuthenticatedPlayerAsync`. | This is literally the board's "purchase/redemption" example, and the only purchase path that is both real (signature-checked) and session-authenticated today (see caveat below). |
| 2 | **Permanent GDPR data purge** | `CommandType.TriggerGdprPurge` (opcode 34), `SimulationEngine.cs:3255-3265` | Irreversible and destroys the account — this game's closest functional equivalent to "account deletion," which the board's own fix-shape paragraph names as a sibling example to email-change/purchase, even though a literal delete-account feature doesn't exist either. |
| 3 | **Binding a new OAuth identity to the account** | `HandleOAuthLink`, `POST /api/v1/auth/oauth-link` (`NetworkBroadcastSystem.cs:8731`) | Durably expands the account's login surface — an attacker who links their own Google/Apple id retains a way back into the account even after the device is evicted or the password is later reset. This is the closest functional equivalent to "email change" (a credential-surface change), since no literal email-change feature exists. Already gated by `TryResolveAuthenticatedPlayerAsync`. |

**Considered and NOT recommended for v1** (state the reasoning so the owner
can override deliberately, not by omission):
- In-game currency spends (market listings, forge rerolls, chest sales,
  `PurchaseBattlePass` opcode 59 spending `PremiumDiamonds`) — these move
  value an attacker with a stolen device-bearer session could already reach
  through ordinary gameplay; gating them protects nothing a password-check
  meaningfully adds, and would annoy every legitimate device-only player
  (every brand-new account, by design) on routine play.
- `HandleAuthRevoke` (sign-out) — a session should always be able to sign
  itself out without re-proving anything; gating it would make an attacker's
  session *harder to end*, the opposite of the goal.
- Village/breeding/guild actions — none touch real money or durable
  account-level credentials; same reasoning as the currency-spend bullet.

**Caveat the owner should also see, found during this research and not part
of what this plan fixes:** the shipped client
(`client_web/src/lib/net/billing.ts:133`) calls
`authedPost('/api/v1/billing/verify-receipt', { receipt })`, but the server
route with that exact path (`NetworkBroadcastSystem.cs:973` →
`HandleVerifyReceipt`, `:7592`) is documented in its own comment as a
"legacy webhook-style verification path" that identifies the caller from a
client-supplied `AccountId` in the JSON body and **never calls
`TryResolveAuthenticatedPlayerAsync` at all** — it doesn't read `receipt`
from the body either (it reads `AccountId`/`TransactionId`/`ProductId`). The
intended hardened, session-authenticated, signature-checked path is a
*different* route, `POST /api/v1/billing/verify` (`:987` → `HandleBillingVerify`,
`:7638`), which the client does not appear to call anywhere. This looks like
a pre-existing route-name collision/drift bug independent of DeviceId
step-up, and it means gating `HandleBillingVerify` (candidate #1 above) may
not actually be in the path the shipped client uses today. This plan does
**not** fix that mismatch — it is a separate finding worth its own backlog
entry — but the owner should know about it when deciding whether candidate
#1 is worth gating in isolation, or whether the route mismatch needs fixing
first for the gate to matter in practice.

---

## Architecture

**The mechanism (decided, not open):**

1. `PlayerRecord` gets one new column, `LastPasswordVerifiedEpoch` (`long`,
   default `0`, meaning "never"). It is bumped to `now` at exactly three
   moments where this server has just seen proof of the account's password:
   a successful email/password login, a successful registration (choosing
   the password *is* proof of it), and a successful password-reset
   completion. It is deliberately **not** bumped by device-bearer login,
   OAuth login, or refresh-token redemption — none of those involve the
   account's password at all.
2. A new, narrow, reusable check —
   `AuthenticationEngine.CheckStepUpFreshnessAsync(db, playerId)` — answers
   two questions with one indexed row read: is the account's last password
   proof still within a freshness window (`StepUpFreshnessWindowSeconds`,
   15 minutes), and does the account even have a password credential to
   check (`PasswordHash != null` — a pure device/guest account, or an
   OAuth-only account, has none). The second question exists because
   telling a player with no password "please re-enter your password" is a
   dead end; that case gets a different, distinct answer
   (`StepUpNoPasswordSet`) so the client can point them at setting one up
   instead of looping them on a box that can never succeed.
3. A new endpoint, `POST /api/v1/auth/verify-password`, lets an
   already-authenticated session (of any kind, including a device-bearer
   one) prove the password *right now* and bump the freshness timestamp —
   this is the step-up action itself, independent of which commands end up
   requiring it.
4. Two thin, reusable gates plug into whichever commands Task 3's list
   picks: a REST one (`TryRequireFreshPasswordAsync`, writes the 403 itself)
   and a WebSocket one (an inline `CheckStepUpFreshnessAsync` call plus two
   new `CommandResultCode` values). Neither gate is specific to any one
   command — adding or removing a gated command later is a one-line change
   at the call site, not a new mechanism.
5. The freshness timestamp is **durable** (a `PlayerRecord` column, not an
   in-memory or JWT-embedded flag), so it survives a WebSocket reconnect or
   an access-token refresh without forcing a redundant re-prompt — a player
   who typed their password four minutes ago and briefly lost their
   connection should not have to type it again immediately after.

**Tech Stack:** C# / .NET 8 server (`server/FolkIdle.Server/`), xUnit +
Testcontainers (real Postgres 16 per collection) for server tests; Svelte 5 /
TypeScript client (`client_web/`) for the step-up prompt and retry flow.

**Spec:** `docs/TASK_BOARD.md`, section "## 15. DeviceId is a bearer
credential with no proof of possession" (grep it; it's in the "OPEN — 14
through 23" section). This plan implements the board's fix-shape (b) only.
Where this plan's researched detail disagrees with the board's one-paragraph
sketch (the concrete command list, since password-change/email-change don't
exist), this plan's detail wins — it was verified against live source on
2026-09-17.

## Global Constraints

- **This plan does not touch fix-shape (a)** (device-bound rotating refresh
  tokens replacing DeviceId's bare authority) or any DeviceId
  rotation/revocation. That is a separate, larger follow-up per the board's
  own "Done when" note ("Full credential-lifecycle rework... is a larger
  follow-up, not required for v1").
- **`LastPasswordVerifiedEpoch` is an additive column with a safe default**
  (`nullable: false, defaultValue: 0L`), matching the exact shape of
  `LastVillagerArrivalEpoch`'s own migration
  (`20260808201840_AddVillageNewcomers.cs:14-19`) — every existing account
  reads `0` (never verified) after this deploys, which is correct,
  conservative behavior: it means the *first* gated action any existing
  player attempts after this ships will ask for a step-up once, not that
  anyone is logged out or blocked from anything else.
- **`CommandResultCode` is not part of the generated wire protocol.** It is
  a `byte` already carried generically inside a result slot
  (`readResultSlots` / `COMMAND_RESULT_MESSAGES` in
  `client_web/src/lib/stores/commandResults.ts`, a hand-maintained
  `Record<number,string>`, not `protocol.generated.ts`). Adding two new
  enum values needs no `npm run generate:protocol` step and does not touch
  `protocol.generated.ts` — do not run the `add-command` skill for this;
  it is only for `ClientCommandPacket`/`StateUpdatePacket` field changes,
  and this plan adds neither.
- **Every new/changed server test seeds its own rows** and, for anything
  touching `PlayerSessionRegistry`/notification queues, passes a fresh
  `new PlayerSessionRegistry()` rather than the xUnit collection's shared
  fixture registry — the established convention from
  `BreedingRoundOneTests.cs:116`, reused by both prior 2026-09-17 plans.
- **Stop the running server before `dotnet build`/`dotnet test`** (CLAUDE.md,
  the stale-build rule; `.claude/hooks/guard_stale_build.py` enforces this).
- **Task 3 must not be implemented until the OPEN DECISION list above is
  confirmed or edited by the owner.** Tasks 1, 2, 4 and 5 have no such
  dependency and can proceed regardless — Task 4 gates
  `TriggerGdprPurge` specifically because that candidate has essentially no
  plausible reason to be excluded from any final list (it is destructive and
  irreversible with no in-between), so it is written as its own task rather
  than folded into Task 3's owner-gated list.

---

### Task 1: The mechanism — schema, freshness check, and the three moments that prove a password

**Files:**
- New migration: `server/FolkIdle.Server/Migrations/` (add
  `PlayerRecords.LastPasswordVerifiedEpoch`, `bigint`, `nullable: false,
  defaultValue: 0L` — mirror `20260808201840_AddVillageNewcomers.cs:14-19`
  exactly in shape)
- Modify: `server/FolkIdle.Server/Models/PlayerRecord.cs` (new property,
  near `PasswordHash`/`CurrentSessionNonce`)
- Modify: `server/FolkIdle.Server/Engine/AuthenticationEngine.cs`
  (`IssueSessionAsync`, ~line 208-220; new
  `CheckStepUpFreshnessAsync`/`StepUpFreshnessStatus`; new
  `VerifyStepUpPasswordAsync`/`PasswordStepUpOutcome`)
- Modify: `server/FolkIdle.Server/Engine/PasswordResetEngine.cs`,
  `CompleteResetAsync` (~line 158-171 — it already sets `PasswordHash` and
  `CurrentSessionNonce` on the same tracked, about-to-be-saved `player`
  entity; add one more field to the same save)
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` — the
  three `IssueSessionAsync` call sites: refresh-redeem (~line 8165),
  `HandleAuthLogin` (~line 8387), `HandleAuthRegister` (~line 8701) — confirm
  exact lines against the live file, they may have drifted since this
  research pass
- Test: new file `server/FolkIdle.Server.Tests/StepUpAuthTests.cs`

**Interfaces:**
- Consumes: nothing new (builds on `IssueSessionAsync`/`CurrentSessionNonce`,
  already landed on this branch per the audit-fixes-14 plan).
- Produces: `AuthenticationEngine.CheckStepUpFreshnessAsync(FolkIdleDbContext
  db, long playerId) -> Task<StepUpFreshnessStatus>` — the ONE check every
  gate in Tasks 3 and 4 calls. `StepUpFreshnessStatus` has two `readonly
  bool` fields: `IsFresh` and `HasPasswordCredential`.
- Produces: `AuthenticationEngine.VerifyStepUpPasswordAsync(
  RetryingDbContextOptions authOptions, long playerId, string password) ->
  Task<PasswordStepUpOutcome>` (`Success | WrongPassword | NoPasswordSet |
  AccountNotFound`) — Task 2's endpoint calls this and nothing else.

- [ ] **Step 1: Write the failing test**

In a new `server/FolkIdle.Server.Tests/StepUpAuthTests.cs`, own database
(mirroring `SessionNonceTests.cs`'s "own database, mutates shared auth
state" pattern):

```csharp
using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    [Collection("StepUpAuthDatabase")]
    public class StepUpAuthTests : IAsyncLifetime
    {
        private readonly PostgresTestFixture _fixture = new();
        public async Task InitializeAsync() => await _fixture.InitializeAsync();
        public async Task DisposeAsync() => await _fixture.DisposeAsync();

        [Fact]
        public async Task Test_FreshPasswordLogin_IsImmediatelyFresh()
        {
            const long playerId = 960000101L;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId,
                    PlayerGuid = accountId,
                    PasswordHash = PasswordHasher.Hash("correct horse battery staple")
                });
                await db.SaveChangesAsync();
            }

            var authOptions = _fixture.AuthOptions;
            await AuthenticationEngine.IssueSessionAsync(authOptions, accountId, "test-secret", passwordJustVerified: true);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var status = await AuthenticationEngine.CheckStepUpFreshnessAsync(verifyDb, playerId);
            Assert.True(status.IsFresh);
            Assert.True(status.HasPasswordCredential);
        }

        [Fact]
        public async Task Test_DeviceBearerLogin_IsNotFresh()
        {
            const long playerId = 960000102L;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId,
                    PlayerGuid = accountId,
                    PasswordHash = PasswordHasher.Hash("correct horse battery staple")
                });
                await db.SaveChangesAsync();
            }

            var authOptions = _fixture.AuthOptions;
            // passwordJustVerified defaults to false - the device-bearer/
            // remembered-device/OAuth shape.
            await AuthenticationEngine.IssueSessionAsync(authOptions, accountId, "test-secret");

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var status = await AuthenticationEngine.CheckStepUpFreshnessAsync(verifyDb, playerId);
            Assert.False(status.IsFresh);
            Assert.True(status.HasPasswordCredential);
        }

        [Fact]
        public async Task Test_AccountWithNoPassword_ReportsNoCredentialRatherThanStale()
        {
            const long playerId = 960000103L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), PasswordHash = null });
                await db.SaveChangesAsync();
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var status = await AuthenticationEngine.CheckStepUpFreshnessAsync(verifyDb, playerId);
            Assert.False(status.IsFresh);
            Assert.False(status.HasPasswordCredential);
        }

        [Fact]
        public async Task Test_VerifyStepUpPassword_WrongPasswordDoesNotBumpFreshness()
        {
            const long playerId = 960000104L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), PasswordHash = PasswordHasher.Hash("correct horse battery staple") });
                await db.SaveChangesAsync();
            }

            var outcome = await AuthenticationEngine.VerifyStepUpPasswordAsync(_fixture.AuthOptions, playerId, "wrong password");
            Assert.Equal(AuthenticationEngine.PasswordStepUpOutcome.WrongPassword, outcome);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var status = await AuthenticationEngine.CheckStepUpFreshnessAsync(verifyDb, playerId);
            Assert.False(status.IsFresh);
        }

        [Fact]
        public async Task Test_VerifyStepUpPassword_CorrectPasswordBumpsFreshness()
        {
            const long playerId = 960000105L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), PasswordHash = PasswordHasher.Hash("correct horse battery staple") });
                await db.SaveChangesAsync();
            }

            var outcome = await AuthenticationEngine.VerifyStepUpPasswordAsync(_fixture.AuthOptions, playerId, "correct horse battery staple");
            Assert.Equal(AuthenticationEngine.PasswordStepUpOutcome.Success, outcome);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var status = await AuthenticationEngine.CheckStepUpFreshnessAsync(verifyDb, playerId);
            Assert.True(status.IsFresh);
        }

        [Fact]
        public async Task Test_VerifyStepUpPassword_NoPasswordSetIsItsOwnOutcome()
        {
            const long playerId = 960000106L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), PasswordHash = null });
                await db.SaveChangesAsync();
            }

            var outcome = await AuthenticationEngine.VerifyStepUpPasswordAsync(_fixture.AuthOptions, playerId, "anything");
            Assert.Equal(AuthenticationEngine.PasswordStepUpOutcome.NoPasswordSet, outcome);
        }

        [Fact]
        public async Task Test_PasswordResetCompletion_BumpsFreshnessTooNotJustNonce()
        {
            // Modul: CompleteResetAsync already bumps CurrentSessionNonce
            // (see SessionNonceTests.cs) - this pins that it ALSO proves the
            // password fresh, on the same save, since choosing/confirming a
            // new password during reset is exactly the proof step-up exists
            // to require.
            const long playerId = 960000107L;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = accountId, PasswordHash = PasswordHasher.Hash("old password 12345") });
                await db.SaveChangesAsync();
            }

            string token = "raw-test-token-" + Guid.NewGuid().ToString("N");
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PasswordResetTokens.Add(new PasswordResetToken
                {
                    PlayerId = playerId,
                    TokenHash = PasswordResetEngine.HashToken(token),
                    ExpiresAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
                    UsedAtEpoch = 0L
                });
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var outcome = await PasswordResetEngine.CompleteResetAsync(db, token, "brand new password 67890", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Assert.Equal(PasswordResetOutcome.Success, outcome);
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var status = await AuthenticationEngine.CheckStepUpFreshnessAsync(verifyDb, playerId);
            Assert.True(status.IsFresh);
        }
    }
}
```

Check `PasswordResetEngine.HashToken`'s accessibility (it's used internally
by `RequestResetAsync`/`CompleteResetAsync` — confirm it's `internal` or
`public static` before calling it from a test; if it's `private`, seed the
`PasswordResetTokens` row with the hash computed however
`RequestResetAsync` already does it, or call `RequestResetAsync` itself to
get a real token instead of hand-seeding). Check `PostgresTestFixture`'s
exact exposed members (`DbContextFactory`, and whatever it calls the
`RetryingDbContextOptions` handle — `AuthOptions` above is a guess at the
name; match whatever `SessionNonceTests.cs` or `RefreshTokenTests.cs`
already calls it).

- [ ] **Step 2: Run the tests to verify they fail**

Stop the running server first. Run:
`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~StepUpAuthTests"`

Expected: compile failure — `LastPasswordVerifiedEpoch`,
`CheckStepUpFreshnessAsync`, `VerifyStepUpPasswordAsync`,
`IssueSessionAsync`'s `passwordJustVerified` parameter all not present yet.

- [ ] **Step 3: Migration**

```csharp
migrationBuilder.AddColumn<long>(
    name: "LastPasswordVerifiedEpoch",
    table: "PlayerRecords",
    type: "bigint",
    nullable: false,
    defaultValue: 0L);
```

Generate with the project's normal EF tooling; do not hand-write the
`Designer.cs` snapshot.

- [ ] **Step 4: `PlayerRecord.cs`**

```csharp
        // Modul: the epoch second this account's password was last proven -
        // by an email/password login, a registration, or a password-reset
        // completion. NOT bumped by a device-bearer/remembered-device login,
        // an OAuth login, or a refresh-token redemption, none of which
        // involve the password at all. 0 means never (every pre-existing
        // account after this deploys, and every pure device/guest account
        // forever). See AuthenticationEngine.CheckStepUpFreshnessAsync -
        // the step-up gate that reads this is what makes a device-bearer
        // session unable to purchase, link a new login identity, or erase
        // the account on the strength of the device id alone
        // (docs/superpowers/plans/2026-09-17-deviceid-stepup-auth.md).
        public long LastPasswordVerifiedEpoch { get; set; }
```

- [ ] **Step 5: `AuthenticationEngine.cs` — `IssueSessionAsync` gains a
      parameter**

Change (exact current body at ~line 208-220):

```csharp
        public static async Task<(string Jwt, long ExpiresAtEpoch)> IssueSessionAsync(
            RetryingDbContextOptions authOptions, Guid accountId, string secretKey)
        {
            string sessionNonce = GenerateSessionNonce();
            string jwt = GenerateJwt(accountId, sessionNonce, secretKey, out long expiresAtEpoch);

            await using var db = new FolkIdleDbContext(authOptions.Options);
            await db.PlayerRecords
                .Where(p => p.PlayerGuid == accountId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.CurrentSessionNonce, sessionNonce));

            return (jwt, expiresAtEpoch);
        }
```

to:

```csharp
        /// <param name="passwordJustVerified">
        /// True only when THIS call is minting a session because the caller
        /// just proved the account's password (an email/password login or a
        /// fresh registration) - never for a device-bearer/remembered-device
        /// login, an OAuth login, or a refresh-token redemption. When true,
        /// LastPasswordVerifiedEpoch is bumped in the same write as
        /// CurrentSessionNonce, so a password login is immediately "fresh"
        /// for step-up purposes without a separate round trip through
        /// /api/v1/auth/verify-password.
        /// </param>
        public static async Task<(string Jwt, long ExpiresAtEpoch)> IssueSessionAsync(
            RetryingDbContextOptions authOptions, Guid accountId, string secretKey, bool passwordJustVerified = false)
        {
            string sessionNonce = GenerateSessionNonce();
            string jwt = GenerateJwt(accountId, sessionNonce, secretKey, out long expiresAtEpoch);

            await using var db = new FolkIdleDbContext(authOptions.Options);
            if (passwordJustVerified)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await db.PlayerRecords
                    .Where(p => p.PlayerGuid == accountId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.CurrentSessionNonce, sessionNonce)
                        .SetProperty(p => p.LastPasswordVerifiedEpoch, now));
            }
            else
            {
                await db.PlayerRecords
                    .Where(p => p.PlayerGuid == accountId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.CurrentSessionNonce, sessionNonce));
            }

            return (jwt, expiresAtEpoch);
        }
```

(Two separate `ExecuteUpdateAsync` calls rather than one conditional
expression inside the setters lambda — EF Core translates that lambda to
SQL and a runtime C# branch selecting between two different `SetProperty`
chains is not something to rely on translating correctly; two small,
unambiguous lambdas are the safe shape.)

- [ ] **Step 6: `AuthenticationEngine.cs` — the freshness check and the
      step-up verify method**

Add near `ValidateSessionNonceAsync`:

```csharp
        // Modul: 15 minutes. A step-up proof stays good across a WebSocket
        // reconnect or an access-token refresh (LastPasswordVerifiedEpoch is
        // a PlayerRecord column, not something scoped to one JWT), so this
        // window is the ONLY thing standing between "prove it once per
        // sensitive action" and "prove it once per sensitive minute" - long
        // enough that a real purchase flow or an OAuth-link click right
        // after entering a password doesn't get asked twice, short enough
        // that a session left open for hours has to re-prove before doing
        // something destructive.
        public const long StepUpFreshnessWindowSeconds = 900L;

        public readonly struct StepUpFreshnessStatus
        {
            public readonly bool IsFresh;
            public readonly bool HasPasswordCredential;

            public StepUpFreshnessStatus(bool isFresh, bool hasPasswordCredential)
            {
                IsFresh = isFresh;
                HasPasswordCredential = hasPasswordCredential;
            }
        }

        /// <summary>
        /// The one check every step-up gate calls - REST (Task 3) and
        /// WebSocket (Task 4) alike. One indexed row read, keyed by the
        /// PlayerId every caller already has in hand (no AccountId
        /// resolution needed).
        /// </summary>
        public static async Task<StepUpFreshnessStatus> CheckStepUpFreshnessAsync(FolkIdleDbContext db, long playerId)
        {
            var row = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => new { p.PasswordHash, p.LastPasswordVerifiedEpoch })
                .FirstOrDefaultAsync();

            if (row == null)
            {
                return new StepUpFreshnessStatus(false, false);
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool fresh = row.LastPasswordVerifiedEpoch != 0L
                && (now - row.LastPasswordVerifiedEpoch) <= StepUpFreshnessWindowSeconds;

            return new StepUpFreshnessStatus(fresh, row.PasswordHash != null);
        }

        public enum PasswordStepUpOutcome
        {
            Success,
            WrongPassword,
            NoPasswordSet,
            AccountNotFound
        }

        /// <summary>
        /// The step-up action itself: re-proves the account's CURRENT
        /// password and, on success, bumps LastPasswordVerifiedEpoch. Called
        /// from POST /api/v1/auth/verify-password (Task 2) and nothing else
        /// - this is deliberately not folded into IssueSessionAsync, since
        /// step-up can happen mid-session without minting a new JWT.
        /// </summary>
        public static async Task<PasswordStepUpOutcome> VerifyStepUpPasswordAsync(
            RetryingDbContextOptions authOptions, long playerId, string password)
        {
            await using var db = new FolkIdleDbContext(authOptions.Options);
            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
            if (player == null)
            {
                return PasswordStepUpOutcome.AccountNotFound;
            }

            if (player.PasswordHash == null)
            {
                return PasswordStepUpOutcome.NoPasswordSet;
            }

            if (!PasswordHasher.Verify(password, player.PasswordHash))
            {
                return PasswordStepUpOutcome.WrongPassword;
            }

            player.LastPasswordVerifiedEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync();
            return PasswordStepUpOutcome.Success;
        }
```

- [ ] **Step 7: the three `IssueSessionAsync` call sites**

In `NetworkBroadcastSystem.cs`:

- Refresh-redeem (~line 8165): pass `passwordJustVerified: false` explicitly
  (a refresh never re-proves the password):
  ```csharp
  var (jwt, expiresAtEpoch) = await AuthenticationEngine.IssueSessionAsync(authOptions, result.AccountId, _jwtSecretKey, passwordJustVerified: false);
  ```
- `HandleAuthLogin` (~line 8271-8409): add a local `bool
  passwordJustVerified = false;` before the branch chain (~line 8330), set
  it `= true;` only inside the email+password branch (~line 8344-8354,
  right after `accountId = emailResult.AccountId;`), then pass it at the
  `IssueSessionAsync` call (~line 8387):
  ```csharp
  var (token, expiresAtEpoch) = await AuthenticationEngine.IssueSessionAsync(authOptions, accountId, _jwtSecretKey, passwordJustVerified);
  ```
  (The oauth, rememberedDeviceId, and bare-deviceId branches leave it
  `false`.)
- `HandleAuthRegister` (~line 8701): pass `passwordJustVerified: true`
  (choosing the password during registration is proof of it):
  ```csharp
  var (token, expiresAtEpoch) = await AuthenticationEngine.IssueSessionAsync(authOptions, result.AccountId, _jwtSecretKey, passwordJustVerified: true);
  ```

- [ ] **Step 8: `PasswordResetEngine.cs` — `CompleteResetAsync`**

Alongside the existing `player.CurrentSessionNonce =
AuthenticationEngine.GenerateSessionNonce();` (~line 171), add:

```csharp
            player.LastPasswordVerifiedEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
```

(Same tracked entity, same `SaveChangesAsync()` a few lines below at ~line
184 — no new round trip.)

- [ ] **Step 9: Run the tests to verify they pass**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~StepUpAuthTests"`

Expected: all PASS.

- [ ] **Step 10: Run the full server suite once**

`IssueSessionAsync`'s signature gained an optional parameter (source
compatible with every existing call site) — confirm nothing else broke,
particularly `SessionNonceTests.cs` and `RefreshTokenTests.cs`.

- [ ] **Step 11: Commit**

```bash
git add server/FolkIdle.Server/Models/PlayerRecord.cs server/FolkIdle.Server/Engine/AuthenticationEngine.cs server/FolkIdle.Server/Engine/PasswordResetEngine.cs server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server/Migrations/ server/FolkIdle.Server.Tests/StepUpAuthTests.cs
git commit -m "$(cat <<'EOF'
feat(auth): a device-bearer session's step-up freshness is now tracked

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01MFT1gVfu81MgCUQhRpLYYK
EOF
)"
```

---

### Task 2: The step-up endpoint itself — `POST /api/v1/auth/verify-password`

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (new
  route registration near the other `/api/v1/auth/*` routes, ~line 828-920;
  new `HandleVerifyStepUpPassword` handler near `HandleAuthRevoke`)
- Test: extends `StepUpAuthTests.cs` from Task 1, or a new
  `server/FolkIdle.Server.Tests/StepUpEndpointTests.cs` if the handler-level
  test needs its own HTTP-driving fixture (check how
  `RefreshTokenTests.cs`/`SessionNonceTests.cs` drive `HandleAuthRevoke` /
  `HandleAuthLogin` today and match that seam rather than inventing a new
  one)

**Interfaces:**
- Consumes: `AuthenticationEngine.VerifyStepUpPasswordAsync` from Task 1.
- Produces: nothing another task depends on for its mechanism, but Task 5
  (client) calls this exact route.

- [ ] **Step 1: Write the failing test**

Drive the route the same way the existing auth-handler tests do (check
`SessionNonceTests.cs`/`RefreshTokenTests.cs` for whether they invoke the
`HttpListenerContext`-taking handler directly, via a test double, or through
a real HTTP client against a spun-up listener, and match that exact
approach — do not invent a new test-harness shape for one endpoint). Cover:
1. A session that resolves to a real player, correct password → 200, and a
   subsequent `CheckStepUpFreshnessAsync` for that player is now fresh.
2. Wrong password → 401, freshness unchanged.
3. No `PasswordHash` on the account → 409 (or whatever status Step 3 below
   settles on — pick ONE and match it consistently with Task 3/5), body
   names the reason distinctly from "wrong password."
4. No/invalid bearer token → 401, same as every other authenticated
   endpoint in this file.

- [ ] **Step 2: Run the tests to verify they fail**

Expected: route not found / handler doesn't exist.

- [ ] **Step 3: Implement the route and handler**

Register alongside the other `/api/v1/auth/*` checks (~line 828-920, in
`HandleHttpRequestAsync` or whatever the enclosing dispatcher method is
called — match the existing `if (requestPath == "..." &&
context.Request.HttpMethod == "POST")` shape exactly):

```csharp
                    if (requestPath == "/api/v1/auth/verify-password" && context.Request.HttpMethod == "POST")
                    {
                        _ = HandleVerifyStepUpPassword(context);
                        continue;
                    }
```

Add it to the `AuthThrottle` budget list too (~line 828-838) — this is a
password-guessing surface exactly like `/api/v1/auth/login`, and deserves
the same per-address rate limit:

```csharp
                    if (requestPath == "/api/v1/auth/login"
                        || requestPath == "/api/v1/auth/register"
                        || requestPath == "/api/v1/auth/oauth-link"
                        || requestPath == "/api/v1/auth/request-password-reset"
                        || requestPath == "/api/v1/auth/reset-password"
                        || requestPath == "/api/v1/auth/refresh"
                        || requestPath == "/api/v1/auth/verify-password")
```

Handler, near `HandleAuthRevoke`:

```csharp
        /// <summary>
        /// The step-up action: an already-authenticated caller (of ANY
        /// session kind, including a device-bearer one) re-proves the
        /// account's current password. On success, bumps
        /// PlayerRecord.LastPasswordVerifiedEpoch so a subsequent gated
        /// action (Task 3/4) reads as fresh.
        /// </summary>
        private async Task HandleVerifyStepUpPassword(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

                string password = string.Empty;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("password", out var passwordElement))
                    {
                        password = passwordElement.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                var outcome = await AuthenticationEngine.VerifyStepUpPasswordAsync(authOptions, playerId, password);

                context.Response.StatusCode = outcome switch
                {
                    AuthenticationEngine.PasswordStepUpOutcome.Success => 200,
                    AuthenticationEngine.PasswordStepUpOutcome.WrongPassword => 401,
                    AuthenticationEngine.PasswordStepUpOutcome.NoPasswordSet => 409,
                    _ => 404
                };
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Outcome = outcome.ToString() });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Step-up verify error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
```

- [ ] **Step 4: Run the tests to verify they pass, then the full server
      suite once**

- [ ] **Step 5: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/
git commit -m "$(cat <<'EOF'
feat(auth): add the step-up password re-verification endpoint

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01MFT1gVfu81MgCUQhRpLYYK
EOF
)"
```

---

### Task 3: The reusable REST gate — applied to the OPEN DECISION list's REST entries

**Do not start this task until the OPEN DECISION section near the top of
this plan has been confirmed or edited by the owner.** As written, it gates
candidates #1 (`HandleBillingVerify`) and #3 (`HandleOAuthLink`) — the two
REST handlers on the candidate list. Candidate #2
(`TriggerGdprPurge`) is a WebSocket command and is Task 4.

**Files:**
- Modify: `server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs` (new
  `TryRequireFreshPasswordAsync` helper near `TryResolveAuthenticatedPlayerAsync`,
  ~line 7561; one call added inside `HandleBillingVerify` ~line 7638-7648 and
  `HandleOAuthLink` ~line 8731-8741, right after each handler's existing
  `playerId == 0L` check)
- Test: extends `StepUpAuthTests.cs` or a new
  `server/FolkIdle.Server.Tests/StepUpGateTests.cs`

**Interfaces:**
- Consumes: `AuthenticationEngine.CheckStepUpFreshnessAsync` from Task 1.
- Produces: `TryRequireFreshPasswordAsync(HttpListenerContext context, long
  playerId) -> Task<bool>` — the one gate every REST handler on the owner's
  final list adds, in the exact same two-line shape, regardless of which
  handler it is. This is the task whose implementation should not need to
  change if the OPEN DECISION list changes — only which handlers call it.

- [ ] **Step 1: Write the failing test**

For each of the two gated handlers (or however many the owner's confirmed
list has — mirror this shape per handler): a device-bearer session (never
called `/api/v1/auth/verify-password`, or last did so >15 minutes ago)
attempting the action gets refused (403, body names `StepUpRequired`) and
the underlying effect did NOT happen (no diamonds credited / no OAuth
identity linked); after a successful `/api/v1/auth/verify-password` call,
retrying the same action succeeds.

```csharp
        [Fact]
        public async Task Test_OAuthLink_RefusesADeviceBearerSessionWithoutStepUp()
        {
            // Seed a player with a password AND a device id (so the login
            // used to obtain the session is genuinely the device-bearer
            // path this test means to exercise), issue a session via
            // IssueSessionAsync(passwordJustVerified: false), then call the
            // OAuth link handler and assert 403 / StepUpRequired and that
            // AuthenticationEngine.LinkOAuthAccountAsync was never reached
            // (no OAuthIdentity row was created).
        }

        [Fact]
        public async Task Test_OAuthLink_SucceedsAfterStepUpVerification()
        {
            // Same setup, but call VerifyStepUpPasswordAsync with the
            // correct password first - the link now succeeds.
        }
```

Fill in the exact HTTP-driving mechanics using whatever seam Task 2's tests
established. Repeat the same two-test shape for `HandleBillingVerify`,
substituting a stubbed/fake receipt validator the way
`StoreApiReceiptVerifierTests.cs` or `BillingVerificationEngine`'s own
existing tests already do (check those first rather than inventing a new
IAP-mocking approach) — the point of THIS test is that the step-up gate
fires before `VerifyReceiptAsync` is ever reached, not that receipt
verification itself works (that's already covered elsewhere).

- [ ] **Step 2: Run the tests to verify they fail**

- [ ] **Step 3: Implement the gate**

Near `TryResolveAuthenticatedPlayerAsync` (~line 7561):

```csharp
        /// <summary>
        /// The reusable step-up gate for a REST handler. Call it right
        /// after resolving playerId and before doing anything the caller
        /// asked for. Writes the refusal response itself (403, JSON body
        /// naming why) and returns false when the caller must stop; returns
        /// true when the handler should proceed exactly as before. Which
        /// handlers call this is a product decision (see the OPEN DECISION
        /// section in docs/superpowers/plans/2026-09-17-deviceid-stepup-auth.md)
        /// - this method itself is not specific to any one of them.
        /// </summary>
        private async Task<bool> TryRequireFreshPasswordAsync(HttpListenerContext context, long playerId)
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            var status = await AuthenticationEngine.CheckStepUpFreshnessAsync(db, playerId);
            if (status.IsFresh)
            {
                return true;
            }

            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                Reason = status.HasPasswordCredential ? "StepUpRequired" : "NoPasswordSet"
            });
            context.Response.Close();
            return false;
        }
```

In `HandleBillingVerify` (~line 7642-7648), right after the existing
`playerId == 0L` check:

```csharp
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId == 0L)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                if (!await TryRequireFreshPasswordAsync(context, playerId))
                {
                    return;
                }
```

In `HandleOAuthLink` (~line 8735-8741), same shape, right after its
`playerId == 0L` check.

- [ ] **Step 4: Run the tests to verify they pass, then the full server
      suite once**

- [ ] **Step 5: Commit**

```bash
git add server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs server/FolkIdle.Server.Tests/
git commit -m "$(cat <<'EOF'
fix(auth): purchases and OAuth-link require a fresh password on a device-bearer session

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01MFT1gVfu81MgCUQhRpLYYK
EOF
)"
```

---

### Task 4: The WebSocket gate — `TriggerGdprPurge`

**Files:**
- Modify: `server/FolkIdle.Server/Network/StateUpdatePacket.cs`
  (`CommandResultCode` enum, add two values after `BreedingFailed = 36`)
- Modify: `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs`
  (the `CommandType.TriggerGdprPurge` branch, ~line 3255-3265)
- Test: extends `StepUpAuthTests.cs` or a new
  `server/FolkIdle.Server.Tests/GdprPurgeStepUpTests.cs`

**Interfaces:**
- Consumes: `AuthenticationEngine.CheckStepUpFreshnessAsync` from Task 1.
- Produces: `CommandResultCode.StepUpRequired = 37`,
  `CommandResultCode.StepUpNoPasswordSet = 38`.

- [ ] **Step 1: Write the failing test**

Drive `TriggerGdprPurge` the way existing `SimulationEngine` command-dispatch
tests already do (check for an existing pattern — e.g. how
`ClientCommandValidator.ValidateGdprPurgeRequest`'s own rejection path is
tested today, since this task's new branch sits right next to it). Assert:
1. A device-bearer session (not fresh) sending `TriggerGdprPurge` gets
   `EnqueueCommandResult` with `StepUpRequired` (account has a password),
   `_compliancePurgeEngine.QueueGdprPurge` is NOT called, and the session is
   NOT terminated (`RemoveActivePlayer`/`ForceDisconnect` not invoked) - the
   player can retry after stepping up, they are not kicked for asking.
2. The same, but the account has no `PasswordHash` at all → the result code
   is `StepUpNoPasswordSet` instead.
3. A fresh session (password verified within the window) sending
   `TriggerGdprPurge` behaves exactly as it does today: the purge is queued
   and the session IS terminated.

- [ ] **Step 2: Run the tests to verify they fail**

- [ ] **Step 3: Add the two `CommandResultCode` values**

In `StateUpdatePacket.cs`, right after `BreedingFailed = 36`:

```csharp

        // Modul: audit #15, device-bearer step-up
        // (docs/superpowers/plans/2026-09-17-deviceid-stepup-auth.md). A
        // device-bearer session cannot erase its own account on the
        // strength of the device id alone - this is the refusal the player
        // can act on (go prove the password), distinct from a security
        // termination. StepUpNoPasswordSet is its own code, not folded into
        // StepUpRequired, because "type your password again" is a dead end
        // for an account that never set one - the client needs to tell
        // those two cases apart.
        StepUpRequired = 37,
        StepUpNoPasswordSet = 38
```

- [ ] **Step 4: Change the `TriggerGdprPurge` branch**

Current (`SimulationEngine.cs:3255-3265`):

```csharp
                    else if (cmd.Command == CommandType.TriggerGdprPurge)
                    {
                        if (!ClientCommandValidator.ValidateGdprPurgeRequest(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        _compliancePurgeEngine.QueueGdprPurge(currentPayload.PlayerId);
                        TerminateSessionForSecurity(routingPlayerId);
                    }
```

Change to:

```csharp
                    else if (cmd.Command == CommandType.TriggerGdprPurge)
                    {
                        if (!ClientCommandValidator.ValidateGdprPurgeRequest(ref currentPayload, ref cmd))
                        {
                            TerminateSessionForSecurity(routingPlayerId);
                            continue;
                        }

                        // Modul: awaited inline like every other DB-backed
                        // command in this loop (village upgrades, breeding,
                        // forge - see the plain `await ..Async(...)` calls a
                        // few hundred lines up, none wrapped in
                        // SafeDispatchAsync), not deferred to a background
                        // task: this command fires at most once per
                        // account's entire lifetime (it destroys the
                        // account), so the tick cost here is nothing like
                        // the per-request cost the hot auth-validation path
                        // has to stay cheap for. Staying on the tick thread
                        // also means TerminateSessionForSecurity below can
                        // still safely touch _activePlayers exactly as it
                        // always has - moving this check into a
                        // SafeDispatchAsync callback would call that from
                        // off the tick thread instead, which nothing else
                        // in this file does.
                        await using var gdprStepUpDb = await _contextFactory.CreateDbContextAsync();
                        var gdprStepUpStatus = await AuthenticationEngine.CheckStepUpFreshnessAsync(gdprStepUpDb, currentPayload.PlayerId);
                        if (!gdprStepUpStatus.IsFresh)
                        {
                            // A refusal the player can act on, not a
                            // security termination - CLAUDE.md's "silent
                            // rollback is this server's favourite way to
                            // lie" rule: the button must visibly refuse,
                            // not just do nothing.
                            _playerRegistry.EnqueueCommandResult(routingPlayerId, (byte)(
                                gdprStepUpStatus.HasPasswordCredential
                                    ? CommandResultCode.StepUpRequired
                                    : CommandResultCode.StepUpNoPasswordSet));
                            continue;
                        }

                        _compliancePurgeEngine.QueueGdprPurge(currentPayload.PlayerId);
                        TerminateSessionForSecurity(routingPlayerId);
                    }
```

- [ ] **Step 5: Run the tests to verify they pass, then the full server
      suite once**

- [ ] **Step 6: Commit**

```bash
git add server/FolkIdle.Server/Network/StateUpdatePacket.cs server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs server/FolkIdle.Server.Tests/
git commit -m "$(cat <<'EOF'
fix(auth): a GDPR account purge requires a fresh password on a device-bearer session

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01MFT1gVfu81MgCUQhRpLYYK
EOF
)"
```

---

### Task 5: Client — surface the refusal and let the player step up

**Files:**
- Modify: `client_web/src/lib/net/auth.ts` (`AuthError` gains an optional
  `reason` field parsed from a JSON body on failure; new
  `verifyStepUpPassword(password)` calling `POST
  /api/v1/auth/verify-password`)
- Modify: `client_web/src/lib/stores/commandResults.ts`
  (`COMMAND_RESULT_MESSAGES`, two new entries for codes 37/38)
- New: a small, reusable step-up prompt component (e.g.
  `client_web/src/lib/components/StepUpPasswordModal.svelte`) - a password
  field, a submit button calling `verifyStepUpPassword`, and on success a
  caller-supplied retry callback; on `NoPasswordSet`, a message pointing at
  the existing Register/email flow instead of a password field (check what
  screen currently handles "set up an email login" for a guest account and
  link there, rather than inventing new copy)
- Modify: whichever call sites reach the two REST-gated actions from Task 3
  (search for where `submitReceipt`/`purchase` in
  `client_web/src/lib/net/billing.ts` and the OAuth-link call are invoked
  from a screen - likely `Store.svelte` and a Settings/account-linking
  screen) to catch a 403 with `reason === 'StepUpRequired'` and show the
  modal, retrying the original action on success
- Test: extends `client_web/tests/billing.test.ts` (it already asserts the
  exact POST path/body, per the earlier grep of that file) and a new test
  for the OAuth-link call site if one doesn't already exist

**Interfaces:**
- Consumes: the two new `CommandResultCode` values (Task 4) and the 403
  JSON shape `{ Reason: "StepUpRequired" | "NoPasswordSet" }` (Task 3/2).
- Produces: nothing another task depends on.

- [ ] **Step 1: Write the failing test**

Extend `client_web/tests/billing.test.ts` (or add a sibling file) to assert:
a `submitReceipt` call that receives a 403 with `{Reason: "StepUpRequired"}`
surfaces that reason distinctly from the existing 409/503 handling (check
what `submitReceipt`'s current `catch` block does at
`client_web/src/lib/net/billing.ts:134-147` and add a branch, not a
replacement).

- [ ] **Step 2: Run the tests to verify they fail**

- [ ] **Step 3: `AuthError` carries a reason**

In `auth.ts`, extend `AuthError`:

```typescript
export class AuthError extends Error {
  constructor(message: string, readonly status: number, readonly reason?: string) {
    super(message);
  }
}
```

And in `authedPost` (~line 345-361), read the body on failure before
throwing (currently discarded entirely):

```typescript
  if (!response.ok) {
    let reason: string | undefined;
    try {
      const errorBody = await response.json();
      reason = typeof errorBody?.Reason === 'string' ? errorBody.Reason : undefined;
    } catch {
      /* no JSON body, or not JSON - status alone is the whole message */
    }
    throw new AuthError(`POST ${path} failed (HTTP ${response.status})`, response.status, reason);
  }
```

Add `verifyStepUpPassword`:

```typescript
export async function verifyStepUpPassword(password: string): Promise<boolean> {
  try {
    await authedPost('/api/v1/auth/verify-password', { password });
    return true;
  } catch (err) {
    return false;
  }
}
```

- [ ] **Step 4: `COMMAND_RESULT_MESSAGES`**

```typescript
  37: 'Confirm your password to continue.',
  38: 'Set up a password for this account first (see Settings).',
```

(Match the exact surrounding formatting/style of the existing entries in
that file rather than guessing indentation.)

- [ ] **Step 5: the modal component and its call sites**

Build `StepUpPasswordModal.svelte` (password input, submit, cancel, calls
`verifyStepUpPassword`, emits success/cancel). Wire it into `submitReceipt`'s
caller in `Store.svelte` and the OAuth-link screen: on an `AuthError` with
`reason === 'StepUpRequired'`, show the modal; on the modal's success
callback, retry the original action once (not a loop — if it fails again,
surface that failure normally). On `reason === 'NoPasswordSet'`, skip the
modal and show a message/link pointing at the existing "add an email login"
flow instead.

- [ ] **Step 6: Run the tests to verify they pass**

`cd client_web && npm run check:ratchet` (svelte-check must not regress the
baseline) and the targeted vitest run for the files touched here.

- [ ] **Step 7: `npm run exercise`**

This changes what a real screen does on a real refusal path — per CLAUDE.md's
"verify" rule this needs the browser-level check, not just unit tests.
Triggering an actual `StepUpRequired` refusal from `exercise.mjs` requires a
device-bearer session attempting a gated action, which may not fit the
existing fixture flow cleanly (the dev fixture is a long-lived, already
fully-authenticated account) — if a clean automated trigger isn't practical,
note that explicitly in the report and record a manual verification instead
(force a device-bearer login via the dev tools, attempt an OAuth link,
confirm the modal appears and a successful step-up lets the link go
through), matching how the audit-fixes-14/17/19/20 plan handled its own
hard-to-automate offline-catchup step.

- [ ] **Step 8: Commit**

```bash
git add client_web/src/lib/net/auth.ts client_web/src/lib/stores/commandResults.ts client_web/src/lib/components/StepUpPasswordModal.svelte client_web/src/ client_web/tests/
git commit -m "$(cat <<'EOF'
feat(client): prompt for a password step-up when the server refuses one

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01MFT1gVfu81MgCUQhRpLYYK
EOF
)"
```

---

## Done when

Matching the board's own criterion: **a device-bearer session cannot
perform any command on the owner-confirmed OPEN DECISION list (as
researched: purchase confirmation, permanent GDPR purge, OAuth identity
linking) without a fresh password check — and that gate has a test**, per
handler, in the shape Tasks 3 and 4 each specify: (1) a device-bearer
session is refused with a distinct, actionable result (not a silent
rollback, not a session termination) and the underlying effect provably did
not happen; (2) the same session succeeds after calling
`/api/v1/auth/verify-password` with the correct password; (3) an account
with no password credential at all gets a distinct refusal
(`NoPasswordSet`/`StepUpNoPasswordSet`) rather than being stuck on a
re-auth prompt it can never satisfy; (4) a session established BY password
(or a fresh registration, or a just-completed password reset) is never
asked to step up a second time within the freshness window.

## Not in this plan

- Fix-shape (a) — a device-bound rotating refresh token replacing
  DeviceId's bare authority. The board names this explicitly as the more
  expensive alternative; this plan implements (b) only.
- DeviceId rotation, revocation, or any other credential-lifecycle rework
  for the device-bearer login path itself. Per the board: "a larger
  follow-up, not required for v1."
- The `/api/v1/billing/verify-receipt` vs `/api/v1/billing/verify` route
  mismatch flagged in the OPEN DECISION section above. Real, found during
  this plan's research, and independent of DeviceId step-up — worth its own
  backlog entry, not fixed here.
- Gating any command beyond the OPEN DECISION list's final, owner-confirmed
  set. The mechanism (Tasks 1, 2) supports gating more later at the cost of
  one line per handler (Task 3's shape) or one inline check per WebSocket
  command (Task 4's shape) — no new plumbing needed.
- Per-device session tracking / multi-device session management, matching
  the same scope limit the audit-fixes-14 plan already stated for
  `CurrentSessionNonce`.

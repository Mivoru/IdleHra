# Session security: revocable access tokens and step-up for device-bearer sessions - design

Status: approved in conversation with the owner, 2026-09-18. Covers audit
findings 14 and 15 in `docs/TASK_BOARD.md` (GitHub Copilot audit,
2026-09-17, both P0).

## 1. Why

Two gaps share one root cause: a live session's identity is proven once, at
login, and nothing about it can be revisited afterward.

- **Task 14.** The JWT is a bearer credential this server does not store, so
  logout, a password reset, or a detected refresh-token theft cannot
  invalidate an access token already issued. It stays valid for up to 24
  hours (`AuthenticationEngine.TokenLifetimeSeconds`) regardless. The
  scaffolding for a fix is already half-built and inert: every JWT already
  carries a `nonce` claim (`AuthenticationEngine.cs:19,69,160`), generated
  fresh at every login/refresh/register, but nothing stores a per-account
  "current" value and `ValidateJwt` never compares against one - a claim
  with no writer, this codebase's recurring trap.
- **Task 15.** `TryLoginByDeviceIdAsync` (`AuthenticationEngine.cs:666`) is a
  bare `WHERE DeviceId == deviceId` lookup with no proof of possession. The
  realistic attack is reading that `localStorage` value (XSS, a synced
  browser profile, a compromised device), not brute force, and it grants
  full silent login - including to an account that has since added a real
  email/password. Checked against the live code: this game does not yet
  have a password-change, email-change, or account-deletion command, so the
  only two authenticated actions actually sensitive enough to matter today
  are **completing a real-money purchase** (`HandleVerifyReceipt`) and
  **attaching an external OAuth identity to the account**
  (`HandleOAuthLink`) - the latter durably extends who can log in as this
  account.

## 2. Decisions (owner-approved)

| Question | Decision |
|---|---|
| Task 14 scope | Bump-on-event revocation via a per-account nonce; reject on next validate. |
| Already-open sockets on a bump | Force-closed immediately, not left to expire - reusing the existing `ForceDisconnect(playerId)`. |
| Task 15 scope | Only the two sensitive actions that exist today (purchase, OAuth link). No stub for features that don't exist yet. |
| Step-up UX | A distinct response code the client prompts inline for (not a generic 401, and not a forced full re-login) - the session itself remains valid. |

## 3. Task 14: revocable access tokens

### Storage

- `PlayerRecord.CurrentSessionNonce` (`string?`, default `null`). One
  additive migration (`AddCurrentSessionNonce`); no existing row is
  rewritten. `null` means "no revocation event has happened since this
  column existed" - every JWT validates as today until the first bump, which
  is the correct bootstrap behavior (nothing forces every existing player to
  re-login the moment this ships).
- An in-memory cache, `ConcurrentDictionary<Guid, string?> _accountCurrentNonce`
  alongside the existing `_accountIdToPlayerIdCache`
  (`NetworkBroadcastSystem.cs:7529`) - same shape, same reason: avoid a DB
  round trip on every authenticated request. Written whenever the nonce is
  set or bumped; read (falling back to a DB lookup, then populating the
  cache, on a miss) by the validate path below.

### Issuing a nonce

All three `GenerateJwt` call sites (`NetworkBroadcastSystem.cs:8157, 8370,
8685` - login-by-device, register, login-by-email) already call
`GenerateSessionNonce()` fresh. Each now also persists that value as the
account's `CurrentSessionNonce` (DB write + cache update) in the same
operation that issues the token.

### Validating a nonce

`ValidateJwt` itself stays pure/stateless (signature + expiry only, as
today) - it already returns `SessionNonce` on `JwtValidationResult`. Both
callers gain one extra step after `IsValid` is confirmed:

- `TryResolveAuthenticatedPlayerAsync` (`:7531`, the REST bearer path).
- The WebSocket auth handshake block (`:8913`).

Each looks up the account's current nonce (cache-first) and rejects -
exactly like an invalid signature (401 for REST, `PolicyViolation` close for
the WebSocket handshake) - if it does not match `result.SessionNonce`. A
`null` current-nonce (pre-migration bootstrap state, see above) always
matches, so nothing already logged in is force-signed-out by the migration
itself.

**Scope is per-account, not per-device**, since the nonce is one column, not
one per refresh token - a logout on one device bumps every device's access
token, not just the one signing out. Checked: this is not a new failure
shape. `client_web/src/lib/net/rest.ts` has no 401-triggers-refresh retry
today, so a socket closing for "the access token stopped validating"
already happens at ordinary 24-hour expiry and the client's reconnect path
already has to handle it for the game to be usable at all; this task makes
that same path fire earlier and on purpose for the account being revoked, on
whichever other device is also logged in. A per-device nonce (keyed to the
refresh-token row instead of the account) would avoid this but is more
mechanism than the audit's "cheap fix, no new subsystem" framing asked for.

### Bumping a nonce (the revocation events)

Generate a fresh nonce, persist it as `CurrentSessionNonce`, update the
cache, and immediately call `ForceDisconnect(playerId)` for that account's
current connection if one exists (resolve `playerId` the same way
`ResolvePlayerIdFromAccountIdAsync` already does). Three call sites:

1. **Logout.** `HandleAuthRevoke` (`:8196`) currently only calls
   `RevokeRefreshTokenAsync(authOptions, rawToken)`, which returns `Task`
   (a bulk `ExecuteUpdateAsync`, no entity, no `AccountId` back to the
   caller) - there is nothing here to bump a nonce against today.
   `RevokeRefreshTokenAsync` changes to `Task<Guid>` (the token's
   `AccountId`, or `Guid.Empty` if the token was not found/already revoked)
   so `HandleAuthRevoke` can bump that account's nonce and
   `ForceDisconnect` its session. A breaking signature change; its three
   existing callers (`NetworkBroadcastSystem.cs:8219`,
   `RefreshTokenTests.cs:248,269`) are updated, same shape as `RecruitAsync`
   in the village gold-sync fix.
   `RevokeAllRefreshTokensAsync(authOptions, accountId)` (`:415`) already
   takes the `accountId` directly, but **has zero callers anywhere in the
   server today** - its doc comment describes a password change and a
   player-reported stolen device, neither of which currently calls it. A
   password reset today revokes nothing: not the refresh tokens, not (until
   this plan) the access token. Section 3's next bullet gives this method
   its first caller.
2. **Password reset completes.** `PasswordResetEngine.CompleteResetAsync`
   succeeding (`NetworkBroadcastSystem.cs:8561`) now also calls
   `RevokeAllRefreshTokensAsync` (finally exercised) and bumps the nonce for
   the account whose password just changed - closing both the access-token
   gap this task targets and the pre-existing refresh-token gap next to it,
   in one change since both fire from the same event.
3. **Refresh-token replay detected.** `RedeemRefreshTokenAsync`'s
   `RefreshOutcome.Replayed` branch (`AuthenticationEngine.cs:351-359`)
   already revokes every refresh token for the account inside its existing
   transaction; it now also bumps the nonce as part of the same event. This
   method is on the auth DB context, not `NetworkBroadcastSystem`, so the
   bump-plus-`ForceDisconnect` composite (which needs the network layer)
   happens in the caller that already handles this outcome, not inside
   `RedeemRefreshTokenAsync` itself.

Bumping a nonce never issues a new token - the account must log in again,
which is correct for all three triggers.

### What is explicitly not changing

`TokenLifetimeSeconds` stays at 24 hours, per the existing comment's own
reasoning (`AuthenticationEngine.cs:203`) - this is a revocation mechanism,
not a lifetime change. Refresh token rotation/replay-detection logic is
unchanged except for the added nonce bump.

## 4. Task 15: step-up for device-bearer sessions

### Tagging how a session was established

Add one more minimal JWT claim, `m` (method): `"pw"` for
`LoginWithEmailAsync`/`RegisterWithEmailAsync`, `"dev"` for
`TryLoginByDeviceIdAsync`. Set at all three `GenerateJwt` call sites (the
same three as section 3), read back into `JwtValidationResult` alongside
`SessionNonce`. No new session-registry state - the token already carries
everything needed, consistent with the existing "hand-rolled minimal JWT"
design (`AuthenticationEngine.cs:63-71`).

### The gate

In `HandleVerifyReceipt` (`:7579`) and `HandleOAuthLink` (`:8716`), after
resolving the authenticated player: if the token's method is `"dev"` **and**
the account has a password set (`PlayerRecord.PasswordHash != null` - a pure
guest has none, so there is nothing to step up to, and the exposure the
audit describes is specifically about an upgraded account being
silently re-entered), require the request body to carry a `Password` field
and verify it with the existing `PasswordHasher.Verify` before proceeding.

- **Missing or wrong password:** respond `403` with a body the client
  distinguishes from an ordinary refusal (`{ "StepUpRequired": true }`) -
  not a 401, because the session itself is valid; this is a request for
  proof, not a re-authentication.
- **Correct password:** proceed exactly as today. The step-up is
  per-request, not persisted - a device-bearer session does not get
  "upgraded" to password-authenticated by this; the JWT's `m` claim is
  unchanged, so every future purchase/link on this same token asks again,
  same as a card reader asking for a PIN every time rather than once per
  card.

### Client

- `billing.ts`'s purchase flow and the OAuth-link flow both gain a small
  "confirm your password" prompt, shown only on the `StepUpRequired`
  response, which retries the same request with `Password` included.
- No change for a password-authenticated session or a passwordless guest -
  the new field is simply never required for them.

## 5. Testing

- **Pure (xUnit).** Nonce bump-and-reject: a token issued before a bump
  fails `ValidateJwt`'s caller-side check after it; a `null` current-nonce
  matches any token (bootstrap); the cache and a cold DB lookup agree.
- **Integration (Testcontainers).** Login → capture token → logout →
  replay the old token against an authenticated REST endpoint → expect 401
  (this is the audit's own "Done when" reproduction). Same shape for a
  password reset and for a manufactured refresh-token replay. A connected
  WebSocket is actually closed when its account's nonce is bumped.
  Step-up: a device-bearer session with a password set is refused on
  `HandleVerifyReceipt`/`HandleOAuthLink` without `Password`, accepted with
  the correct one, rejected with the wrong one; a passwordless guest and a
  password-authenticated session both proceed unconditionally.
- **`StateUpdatePacketFieldCoverageTests`-style guard:** not applicable -
  nothing here touches `StateUpdatePacket` or `ClientCommandPacket`; both
  new claims live inside the JWT payload, and the step-up field is a REST
  body field. Confirm during implementation that this is still true (no
  wire/packet change), since a JWT claim is easy to add without touching a
  struct and this plan depends on that staying so.
- **End to end:** `exercise.mjs`/`smoke:screens` need no changes - neither
  flow they drive triggers a step-up (the dev fixture and exercise accounts
  are password-authenticated or lack a password entirely). Manual
  verification: register with email+password, force a device-bearer relogin
  path, attempt a receipt verification, confirm the step-up prompt.

## 6. Out of scope

- A UI/flow for changing password or email while logged in (doesn't exist;
  nothing to gate).
- Account deletion (doesn't exist).
- Rotating or capping `RefreshTokenLifetimeSeconds` (60 days) - unrelated to
  either finding.
- Rate-limiting the step-up password check itself beyond whatever general
  throttling already applies to authenticated REST calls - a determined
  attacker who already holds a valid device-bearer session guessing a
  password is a brute-force problem, not this task's.

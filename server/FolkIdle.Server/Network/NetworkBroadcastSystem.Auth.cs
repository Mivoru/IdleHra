using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    // Modul: moved out of NetworkBroadcastSystem.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md). Routing stays in RouteCoreAsync.
    public partial class NetworkBroadcastSystem
    {
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

        // Modul: final-review Finding 3 - this used to duplicate
        // TryResolveAuthenticatedPlayerWithMethodAsync almost line-for-line
        // (bearer-prefix parsing, ValidateJwt, the nonce check, player-id
        // resolution), differing only in what it returned. Delegating keeps
        // there being exactly one place that decides what makes a bearer
        // token authenticated; every existing caller here only ever wanted
        // the PlayerId half of that tuple.
        private async Task<long> TryResolveAuthenticatedPlayerAsync(HttpListenerRequest request)
            => (await TryResolveAuthenticatedPlayerWithMethodAsync(request)).PlayerId;

        private async Task<long> ResolvePlayerIdFromAccountIdAsync(Guid accountId)
        {
            if (_accountIdToPlayerIdCache.TryGetValue(accountId, out long cachedPlayerId))
            {
                return cachedPlayerId;
            }

            await using var db = await _contextFactory.CreateDbContextAsync();
            var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.PlayerGuid == accountId);
            if (player == null)
            {
                return 0L;
            }

            _accountIdToPlayerIdCache[accountId] = player.Id;
            return player.Id;
        }

        // Modul: sibling of TryResolveAuthenticatedPlayerAsync that also
        // hands back the token's AuthMethod ("pw"/"dev") - added for the
        // step-up gate (RequiresPasswordStepUpAsync below) rather than
        // changing the original, which has seven-plus callers that only
        // ever needed the playerId.
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
        /// True only when this session was established by a silent
        /// device-bearer login AND the account actually has a password to
        /// step up TO - the one case that proves nothing about who is
        /// holding the device. False for a real password/OAuth login (or a
        /// JWT that predates the "m" claim, defaulted safe), which never
        /// needs a step-up. A pure guest (PasswordHash still null) has
        /// nothing to step up to, so a stolen DeviceId on a guest account
        /// gains nothing extra from this gate - it is exactly as exposed as
        /// it always was.
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

        /// <summary>
        /// Issues the refresh half of a login, and never fails the login.
        /// </summary>
        /// <remarks>
        /// Modul: A LOGIN THAT SUCCEEDED MUST NOT BE THROWN AWAY BECAUSE THE
        /// SECOND HALF DID NOT. The JWT is already minted and valid for a day
        /// by the time this runs; if the insert fails the player is signed in
        /// exactly as they were before any of this existed, and finds out
        /// tomorrow. Failing the whole login instead would turn a feature that
        /// saves a password prompt into one that prevents a sign-in.
        /// </remarks>
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

        /// <summary>
        /// Trades a refresh token for a fresh JWT, and rotates it.
        /// </summary>
        /// <remarks>
        /// Modul: THIS IS WHAT MAKES THE APP OPEN STRAIGHT INTO THE GAME.
        ///
        /// Before it, the 24-hour JWT meant a player who opened FolkIdle on
        /// Tuesday morning after playing on Monday evening met the login form -
        /// daily, in a game whose entire promise is that it runs while you are
        /// gone.
        ///
        /// EVERY FAILURE IS 401, AND SAYS NOTHING ELSE. Unknown, expired and
        /// replayed are one answer on the wire, because the difference between
        /// them is information about somebody else's session. The client needs
        /// only "this did not work, show the login form", which is precisely
        /// the case B2 also asked to land on the login screen rather than hang.
        /// The reason IS logged, because the replay case is the one anybody
        /// investigating a stolen account will come looking for.
        /// </remarks>
        private async Task HandleAuthRefresh(HttpListenerContext context)
        {
            try
            {
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();

                string body = await ReadBodyAsync(context);

                string rawToken;
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    rawToken = parsed.RootElement.TryGetProperty("refreshToken", out var t)
                        ? (t.GetString() ?? string.Empty)
                        : string.Empty;
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var result = await AuthenticationEngine.RedeemRefreshTokenAsync(authOptions, rawToken);
                if (result.Outcome != AuthenticationEngine.RefreshOutcome.Rotated)
                {
                    if (result.Outcome == AuthenticationEngine.RefreshOutcome.Replayed)
                    {
                        // The only one worth a line in the log: a spent token
                        // came back, so every session on that account was just
                        // revoked. See RedeemRefreshTokenAsync for why.
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

                // Modul: THE NONCE DOES NOT FEED SESSION EVICTION - that is a
                // SEPARATE mechanism (RedisPlayerSessionLock, its own
                // per-connection RedisLockToken, see SubscribeToSessionEviction
                // and the WebSocket handshake's ForceAcquireAndEvictAsync
                // call). An earlier comment here claimed otherwise; it was
                // wrong. This nonce exists so a logout/reset/replay event -
                // none of which are a new login - can invalidate an access
                // token nothing else touches. See IsNonceCurrentAsync.
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

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth refresh error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Signing out: invalidates the refresh token on this device, AND the
        /// live access token, AND any open WebSocket for the account.
        /// </summary>
        /// <remarks>
        /// Answers 204 whatever happened, including for a token that never
        /// existed. There is nothing a sign-out button would do differently on
        /// being told the token was already gone, and distinguishing the cases
        /// would confirm which tokens are live to anybody who can reach the
        /// route.
        ///
        /// The JWT used to be untouched and stay valid for the rest of its day
        /// - a bearer token this server does not store, so nothing checked it
        /// again on the way in. That is fixed by bumping the account's session
        /// nonce (signed into every JWT at login) and evicting the cached
        /// nonce + any live WebSocket, so a request bearing the old token now
        /// fails the nonce check and a connected session is disconnected
        /// immediately, not just prevented from silently refreshing.
        /// </remarks>
        private async Task HandleAuthRevoke(HttpListenerContext context)
        {
            try
            {
                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();

                string body = await ReadBodyAsync(context);

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

                // Still 204. A sign-out that reports a failure invites the
                // player to press it again, and the local half - which is the
                // half that stops this browser being signed in - has already
                // happened by the time this is called.
                context.Response.StatusCode = 204;
            }

            context.Response.Close();
        }

        // Modul: sole controlled entry point for account identity issuance.
        // DeviceId is a client-persisted GUID (see UiLoginWindow on the
        // client) - looked up or auto-provisioned via AuthenticationEngine.
        // LoginOrProvisionAsync, then a fresh SessionNonce is minted and
        // signed into a JWT. THE NONCE DOES NOT FEED SESSION EVICTION - that
        // is a separate mechanism (RedisPlayerSessionLock, its own
        // per-connection RedisLockToken, see SubscribeToSessionEviction and
        // the WebSocket handshake's ForceAcquireAndEvictAsync call). An
        // earlier comment here claimed the nonce was what the Redis eviction
        // check used to detect and kick a stale prior session; it was wrong.
        // This nonce exists so a logout/reset/replay event - none of which
        // are a new login - can invalidate an access token nothing else
        // touches. See IsNonceCurrentAsync.
        // Modul: accepts either deviceId (existing login-or-provision flow,
        // unchanged) or oauthProviderToken (OAuth recovery login, Part 1 of
        // this task). oauthProviderToken is a validated PROOF-OF-OWNERSHIP
        // token, never a bare provider ID - accepting a raw ID directly
        // would let any caller claim any linked account just by supplying
        // its external ID with no proof of ownership at all. Recovery only:
        // if no account is linked to the validated (ProviderType,
        // ExternalProviderId) pair, this returns 404 rather than
        // auto-provisioning a new account - linking is a separate,
        // explicit, authenticated action (see HandleOAuthLink).
        private async Task HandleAuthLogin(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.HasEntityBody)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string deviceId = string.Empty;
                string oauthProviderToken = string.Empty;
                string rememberedDeviceId = string.Empty;
                string email = string.Empty;
                string password = string.Empty;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("oauthProviderToken", out var oauthElement))
                    {
                        oauthProviderToken = oauthElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("deviceId", out var deviceIdElement))
                    {
                        deviceId = deviceIdElement.GetString() ?? string.Empty;
                    }
                    // Modul: Email/Password Auth. A separate field from
                    // deviceId (not a rename) - deviceId still means "log in
                    // or auto-provision a fresh anonymous account" (the
                    // pre-existing behavior, unchanged), while
                    // rememberedDeviceId means "silently resume ONLY if this
                    // device already completed a real Register/email login,
                    // otherwise tell the caller to show the login/register
                    // choice screen" (see UiLoginWindow).
                    if (document.RootElement.TryGetProperty("rememberedDeviceId", out var rememberedElement))
                    {
                        rememberedDeviceId = rememberedElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("email", out var emailElement))
                    {
                        email = emailElement.GetString() ?? string.Empty;
                    }
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

                // Modul: a sign-in stamp only. The login week is claimed by
                // hand from its window since 2026-11-15, because a refresh
                // token renews most sessions and this handler never ran for
                // them (see DailyLoginRewardEngine). Never blocks the login.
                await DailyLoginRewardEngine.NoteSignInAsync(authOptions, accountId);

                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, accountId, sessionNonce);
                _accountCurrentNonce[accountId] = sessionNonce;
                string token = AuthenticationEngine.GenerateJwt(accountId, sessionNonce, authMethod, _jwtSecretKey, out long expiresAtEpoch);

                var refresh = await TryIssueRefreshTokenAsync(authOptions, accountId, authMethod);

                var response = new AuthLoginResponse
                {
                    Token = token,
                    ExpiresAtEpoch = expiresAtEpoch,
                    RefreshToken = refresh.Token,
                    RefreshExpiresAtEpoch = refresh.ExpiresAtEpoch
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth login error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: /api/v1/auth/check-email IS GONE.
        //
        // It answered, unauthenticated and unthrottled, whether a given address
        // has an account here - which is an account ENUMERATION ORACLE: feed it
        // a breach dump and it hands back the subset of addresses that play
        // this game, ready for credential stuffing or a targeted phish. It was
        // built for a convenience nothing ended up using: `isEmailAvailable`
        // had no caller anywhere in the client.
        //
        // Registration still refuses a duplicate address, so nothing a real
        // player does has changed. The difference is that finding out now costs
        // one registration attempt through the auth throttle instead of one
        // cheap POST per address.


        // Modul: Email/Password Auth. Creates a new account (see
        // AuthenticationEngine.RegisterWithEmailAsync) and, on success,
        // immediately issues a JWT exactly like HandleAuthLogin does - a
        // successful registration logs the player straight into the game,
        // it does not require a separate follow-up login call.
        /// <summary>
        /// "I forgot my password."
        ///
        /// ALWAYS ANSWERS 200, whatever happened. Unknown address, known
        /// address, an account with no password, the mail provider being down -
        /// every one of them is reported identically, because any difference
        /// turns this into the account enumeration oracle that
        /// /api/v1/auth/check-email was deleted for. The player is told to
        /// check their email either way, which is also the honest instruction:
        /// if they own the address, the mail is on its way.
        /// </summary>
        private async Task HandleRequestPasswordReset(HttpListenerContext context)
        {
            // Modul: WHETHER THIS SERVER CAN SEND MAIL AT ALL, told to the client.
            //
            // Production ran for weeks with no provider configured, and the
            // screen told every player "a reset link is on its way" - a promise
            // nothing could keep. This answers a question about the SERVER, not
            // about the address: it is the same value for every request, known
            // and unknown accounts alike, so it rebuilds no enumeration oracle.
            bool emailDelivery = _serviceProvider.GetService<Engine.IEmailSender>() is not (null or Engine.DisabledEmailSender);

            try
            {
                string email = string.Empty;

                if (context.Request.HasEntityBody)
                {
                    string body = await ReadBodyAsync(context);
                    try
                    {
                        using var document = System.Text.Json.JsonDocument.Parse(body);
                        if (document.RootElement.TryGetProperty("email", out var emailElement))
                        {
                            email = emailElement.GetString() ?? string.Empty;
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Even a malformed body answers 200 - see above.
                    }
                }

                if (email.Length > 0)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                    long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string? token = await Engine.PasswordResetEngine.BeginResetAsync(db, email, nowEpoch);

                    if (token != null)
                    {
                        // THE TOKEN TRAVELS IN A HASH FRAGMENT. Everything after
                        // the # is never sent to a server, so the link does not
                        // land in this box's access log, in a proxy's, or in a
                        // Referer header on the next page the player opens.
                        string origin = ResolveClientOrigin(context.Request);
                        string resetUrl = origin + "/#reset=" + token;

                        var sender = _serviceProvider.GetRequiredService<Engine.IEmailSender>();
                        await sender.SendAsync(
                            email,
                            "Reset your FolkIdle password",
                            Engine.PasswordResetEngine.BuildEmailBody(resetUrl));
                    }
                }

                await WriteResetRequestAnswerAsync(context, emailDelivery);
            }
            catch (Exception ex)
            {
                // Logged without the address, for the same reason the provider
                // failure path does not log it.
                Console.WriteLine("Password reset request error: " + ex.Message);
                // STILL 200, with the same body. An exception that answered 500
                // for known addresses and 200 for unknown ones would be the
                // oracle again, wearing a status code.
                try
                {
                    await WriteResetRequestAnswerAsync(context, emailDelivery);
                }
                catch
                {
                    context.Response.Close();
                }
            }
        }

        private static async Task WriteResetRequestAnswerAsync(HttpListenerContext context, bool emailDelivery)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { EmailDelivery = emailDelivery });
            context.Response.Close();
        }

        /// <summary>
        /// "Here is the link, and my new password."
        ///
        /// Unlike the request side these outcomes ARE distinguished: the caller
        /// is already holding a 256-bit token, so "that link has expired" tells
        /// them nothing they could not infer, and refusing without a reason
        /// would strand them in front of a form.
        /// </summary>
        private async Task HandleResetPassword(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.HasEntityBody)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string token = string.Empty;
                string newPassword = string.Empty;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("token", out var tokenElement))
                    {
                        token = tokenElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("password", out var passwordElement))
                    {
                        newPassword = passwordElement.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

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
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Password reset error: " + ex.Message);
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        /// <summary>
        /// Where the client lives, for building a link back into it.
        ///
        /// The Origin header ONLY when it is one this server already trusts -
        /// FOLKIDLE_WEB_ORIGINS is the existing CORS allowlist, so reusing it
        /// means a reset link can only ever point somewhere this deployment
        /// already serves. An attacker-supplied Origin is otherwise a way to
        /// have us email somebody a link to the attacker's own site.
        /// </summary>
        private static string ResolveClientOrigin(HttpListenerRequest request)
        {
            string origin = request.Headers["Origin"] ?? string.Empty;
            string configured = Environment.GetEnvironmentVariable("FOLKIDLE_WEB_ORIGINS") ?? string.Empty;
            var allowedOrigins = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (origin.Length > 0)
            {
                foreach (string allowed in allowedOrigins)
                {
                    if (string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        return origin.TrimEnd('/');
                    }
                }
            }

            // Falls back to the FIRST configured origin rather than to the
            // request's - a link nobody can follow is better than one that
            // points at somebody else's server.
            foreach (string allowed in allowedOrigins)
            {
                return allowed.TrimEnd('/');
            }

            return string.Empty;
        }

        private async Task HandleAuthRegister(HttpListenerContext context)
        {
            try
            {
                if (!context.Request.HasEntityBody)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string email = string.Empty;
                string username = string.Empty;
                string password = string.Empty;
                string deviceId = string.Empty;
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("email", out var emailElement))
                    {
                        email = emailElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("username", out var usernameElement))
                    {
                        username = usernameElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("password", out var passwordElement))
                    {
                        password = passwordElement.GetString() ?? string.Empty;
                    }
                    if (document.RootElement.TryGetProperty("deviceId", out var deviceIdElement))
                    {
                        deviceId = deviceIdElement.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var authOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
                var result = await AuthenticationEngine.RegisterWithEmailAsync(authOptions, email, username, password, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);

                if (result.Outcome != EmailRegisterOutcome.Success)
                {
                    context.Response.StatusCode = result.Outcome switch
                    {
                        EmailRegisterOutcome.EmailInUse => 409,
                        EmailRegisterOutcome.UsernameInUse => 409,
                        EmailRegisterOutcome.InvalidEmail => 400,
                        EmailRegisterOutcome.InvalidUsername => 400,
                        EmailRegisterOutcome.InvalidPassword => 400,
                        _ => 500
                    };
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new RegisterErrorResponse { Reason = result.Outcome.ToString() });
                    context.Response.Close();
                    return;
                }

                await DailyLoginRewardEngine.NoteSignInAsync(authOptions, result.AccountId);

                string sessionNonce = AuthenticationEngine.GenerateSessionNonce();
                await AuthenticationEngine.SetCurrentSessionNonceAsync(authOptions, result.AccountId, sessionNonce);
                _accountCurrentNonce[result.AccountId] = sessionNonce;
                string token = AuthenticationEngine.GenerateJwt(result.AccountId, sessionNonce, "pw", _jwtSecretKey, out long expiresAtEpoch);

                var refresh = await TryIssueRefreshTokenAsync(authOptions, result.AccountId, "pw");

                var response = new AuthLoginResponse
                {
                    Token = token,
                    ExpiresAtEpoch = expiresAtEpoch,
                    RefreshToken = refresh.Token,
                    RefreshExpiresAtEpoch = refresh.ExpiresAtEpoch
                };
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auth register error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: irreversibly links the caller's OWN authenticated session
        // (resolved from the Bearer JWT, see TryResolveAuthenticatedPlayerAsync)
        // to an external OAuth identity. Requires an already-authenticated
        // session precisely because linking must bind to "the current
        // active session's AccountId", not to an AccountId the caller could
        // otherwise supply directly in the request body.
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

                string body = await ReadBodyAsync(context);

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

                // Modul: step-up gate, Task 10 - see HandleBillingVerify's
                // identical block. Linking an external identity is just as
                // irreversible/account-changing as a purchase, so a
                // device-bearer session on a password-holding account must
                // re-prove the password before LinkOAuthAccountAsync runs.
                if (await RequiresPasswordStepUpAsync(playerId, authMethod))
                {
                    await using var stepUpDb = await _contextFactory.CreateDbContextAsync();
                    if (suppliedPassword.Length == 0 || !await VerifyStepUpPasswordAsync(stepUpDb, playerId, suppliedPassword))
                    {
                        // Modul: matches HandleBillingVerify's own log line -
                        // this route is already in the AuthThrottle allowlist
                        // (it verified a password for recovery-login purposes
                        // long before this gate existed), but the throttle
                        // alone leaves no record of which player a
                        // brute-force attempt targeted.
                        Console.WriteLine($"Step-up rejected: player {playerId} presented a device-bearer session with a missing or incorrect password on oauth-link.");
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
    }
}

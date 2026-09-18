using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    public enum PasswordResetOutcome
    {
        Success,
        InvalidToken,
        Expired,
        AlreadyUsed,
        InvalidPassword,
    }

    /// <summary>
    /// Getting back into an account you still own.
    ///
    /// WHY THIS EXISTS AT ALL: registration was the only place this server ever
    /// set a password, so a player who forgot theirs had lost the account
    /// permanently - on a live game, with no way to tell them otherwise. That
    /// is a worse bug than anything the password rules were protecting against.
    ///
    /// The four properties that make this safe, each of which is the difference
    /// between a reset flow and a back door:
    ///
    /// 1. **A request tells the caller NOTHING.** Unknown address, known
    ///    address, provider outage - all identical, and all reported as
    ///    success. Anything else rebuilds the account enumeration oracle that
    ///    /api/v1/auth/check-email was just deleted for.
    /// 2. **The token is 32 bytes of CSPRNG and is stored only as a hash.** It
    ///    is a bearer credential: whoever holds it owns the account, so the
    ///    database must not hold it either.
    /// 3. **Single use, and short lived.** A spent link and an old link are
    ///    both refused, so a forwarded email or a shared screenshot stops being
    ///    an account within the hour.
    /// 4. **A new request invalidates the previous one**, so a player who
    ///    clicks "send it again" three times does not leave three live keys
    ///    lying in three inboxes.
    ///
    /// A successful reset also ends any session already signed in: it bumps
    /// PlayerRecord.CurrentSessionNonce, and the caller (HandleResetPassword)
    /// uses the returned account id and nonce to update the in-memory nonce
    /// cache and force-disconnect the live socket, so a JWT already issued
    /// stops validating on its very next authenticated request rather than
    /// riding out its remaining 24-hour lifetime.
    /// </summary>
    public static class PasswordResetEngine
    {
        /// <summary>
        /// One hour. Long enough to walk to a laptop, short enough that a
        /// forwarded or screenshotted link stops mattering the same morning.
        /// </summary>
        public const long TokenLifetimeSeconds = 3600L;

        private const int TokenBytes = 32;

        /// <summary>
        /// The token as the player receives it: 32 CSPRNG bytes, base64url so
        /// it survives a URL untouched.
        /// </summary>
        public static string GenerateToken()
        {
            byte[] raw = RandomNumberGenerator.GetBytes(TokenBytes);
            return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>What goes in the table. See PasswordResetToken on why SHA-256.</summary>
        public static string HashToken(string token)
            => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token ?? string.Empty)));

        /// <summary>
        /// Starts a reset, and returns the token to email - or null when there
        /// is nothing to do.
        ///
        /// THE NULL IS NOT AN ERROR AND MUST NOT BE REPORTED AS ONE. It means
        /// "no account with a password on that address", which is precisely the
        /// fact a caller is not allowed to learn.
        ///
        /// Accounts with no PasswordHash - the anonymous device-provisioned
        /// ones - are skipped too: there is no password to reset, and minting a
        /// token would let anyone who guessed the address SET one on an account
        /// they never owned.
        /// </summary>
        public static async Task<string?> BeginResetAsync(
            FolkIdleDbContext db, string email, long nowEpoch)
        {
            string normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0) return null;

            var player = await db.PlayerRecords
                .FirstOrDefaultAsync(p => p.Email == normalized);

            if (player == null || string.IsNullOrEmpty(player.PasswordHash)) return null;

            // One live ticket at a time. Without this, "send it again" leaves
            // every previous link working.
            var outstanding = await db.PasswordResetTokens
                .Where(t => t.PlayerId == player.Id && t.UsedAtEpoch == 0 && t.ExpiresAtEpoch > nowEpoch)
                .ToListAsync();

            for (int i = 0; i < outstanding.Count; i++)
            {
                outstanding[i].UsedAtEpoch = nowEpoch;
            }

            string token = GenerateToken();
            db.PasswordResetTokens.Add(new PasswordResetToken
            {
                PlayerId = player.Id,
                TokenHash = HashToken(token),
                CreatedAtEpoch = nowEpoch,
                ExpiresAtEpoch = nowEpoch + TokenLifetimeSeconds,
                UsedAtEpoch = 0L,
            });

            await db.SaveChangesAsync();
            return token;
        }

        /// <summary>
        /// Spends a token and sets the new password.
        ///
        /// The outcomes ARE distinguishable here, unlike the request side, and
        /// that is deliberate: by this point the caller is holding a 256-bit
        /// token, so telling them "that link has expired" leaks nothing they
        /// could not already infer, and telling them nothing would strand a
        /// player in front of a form that refuses them without saying why.
        /// </summary>
        public static async Task<(PasswordResetOutcome Outcome, Guid AccountId, string NewNonce)> CompleteResetAsync(
            FolkIdleDbContext db, string token, string newPassword, long nowEpoch)
        {
            if (string.IsNullOrEmpty(token)) return (PasswordResetOutcome.InvalidToken, Guid.Empty, string.Empty);

            // Checked BEFORE the token is spent, so a player who fumbles the
            // new password does not also lose their link.
            if (!PasswordPolicy.IsAcceptable(newPassword)) return (PasswordResetOutcome.InvalidPassword, Guid.Empty, string.Empty);

            string hash = HashToken(token);

            // Looked up BY hash: an index probe on a 256-bit random value, so
            // there is no timing signal worth defending against here the way
            // there is on a password compare.
            var row = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (row == null) return (PasswordResetOutcome.InvalidToken, Guid.Empty, string.Empty);

            if (row.UsedAtEpoch != 0L) return (PasswordResetOutcome.AlreadyUsed, Guid.Empty, string.Empty);
            if (row.ExpiresAtEpoch <= nowEpoch) return (PasswordResetOutcome.Expired, Guid.Empty, string.Empty);

            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == row.PlayerId);
            if (player == null) return (PasswordResetOutcome.InvalidToken, Guid.Empty, string.Empty);

            player.PasswordHash = PasswordHasher.Hash(newPassword);

            // Modul: THE REMEMBERED DEVICE IS CUT LOOSE.
            //
            // DeviceId doubles as the "remember this device" anchor - see
            // TryLoginByDeviceIdAsync, which signs somebody straight in with no
            // password at all. Somebody resetting a password may well be doing
            // it because another person has their machine, and leaving that
            // anchor in place would hand the account straight back.
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

            // Modul: AND SO IS EVERY REFRESH TOKEN, for exactly the reason the
            // DeviceId above is cleared.
            //
            // A refresh token is sixty days of signing in without a password.
            // Somebody resetting a password because another person has been in
            // their account would otherwise change the password and leave that
            // person a working key - which is the whole failure this endpoint
            // exists to undo. The JWT already issued is now cut loose above via
            // the session nonce; the refresh half is revoked here the same way
            // it always was.
            await db.PlayerRefreshTokens
                .Where(t => t.AccountId == player.PlayerGuid && t.RevokedEpoch == 0L)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedEpoch, nowEpoch));

            return (PasswordResetOutcome.Success, player.PlayerGuid, newNonce);
        }

        /// <summary>
        /// The message. Plain text on purpose - an HTML mail from a new sending
        /// domain is markedly more likely to be filtered, and there is nothing
        /// here that needs formatting.
        /// </summary>
        public static string BuildEmailBody(string resetUrl)
        {
            return
                "Somebody asked to reset the password on your FolkIdle account.\n\n" +
                "Open this link within the hour to choose a new one:\n\n" +
                resetUrl + "\n\n" +
                "If that was not you, nothing has happened and you can ignore this - " +
                "your password has not changed and nobody has been let in.\n";
        }
    }
}

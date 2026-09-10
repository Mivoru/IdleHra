namespace FolkIdle.Server.Models
{
    /// <summary>
    /// A long-lived, revocable credential that buys a short-lived JWT and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// Modul: THE 24-HOUR JWT MEANT "LOGGED OUT EVERY DAY" IN A GAME WHOSE
    /// WHOLE PROMISE IS THAT IT RUNS WHILE YOU ARE GONE.
    ///
    /// In a browser tab that is a mild annoyance. On a phone it is the app
    /// asking for a password every single morning, which is the first thing a
    /// store reviewer meets. The three ways out were: lengthen the JWT, add a
    /// refresh token, or a device token exchanged for short JWTs. The first
    /// cannot be revoked and cannot be shortened once issued - a stolen 60-day
    /// JWT is valid for 60 days no matter what anyone does about it. This is
    /// the second, which is the first one's benefit without that property.
    ///
    /// WHAT IS STORED IS A HASH. The row is a verifier, not the credential: a
    /// dump of this table lets nobody sign in as anybody, exactly as with a
    /// password. `TokenHash` is SHA-256 of the raw token, which is 32 bytes of
    /// CSPRNG output - there is no salt because there is no low-entropy input
    /// to protect, and a per-row salt would forbid the indexed lookup this is
    /// read by.
    ///
    /// ROTATED ON EVERY USE. Spending a refresh token revokes it and issues its
    /// successor, so a token that is presented twice is either a lost race or a
    /// stolen credential being replayed. Neither can be told apart from here,
    /// so both revoke the whole family - see
    /// AuthenticationEngine.RedeemRefreshTokenAsync.
    /// </remarks>
    public class PlayerRefreshToken
    {
        public long Id { get; set; }

        /// <summary>
        /// The account, not the player. This is issued by the auth routes,
        /// which deal in AccountId (PlayerRecord.PlayerGuid) - a refresh
        /// happens before anything has resolved a PlayerRecord.
        /// </summary>
        public System.Guid AccountId { get; set; }

        /// <summary>SHA-256 of the raw token. Never the token itself.</summary>
        public byte[] TokenHash { get; set; } = System.Array.Empty<byte>();

        public long IssuedEpoch { get; set; }

        public long ExpiresAtEpoch { get; set; }

        /// <summary>
        /// 0 while the token is live. Set when it is spent, replaced, or
        /// revoked - one column rather than a bool, because "when" is the
        /// question anyone investigating a stolen session will ask.
        /// </summary>
        public long RevokedEpoch { get; set; }
    }
}

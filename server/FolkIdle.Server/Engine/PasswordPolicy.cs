namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What counts as an acceptable password, in one place.
    ///
    /// **LENGTH, AND NOTHING ELSE.** No required digit, no required symbol, no
    /// mixed case. That is NIST SP 800-63B's actual guidance and it is not
    /// laxness: composition rules measurably push people toward "Password1!"
    /// and a sticky note, while length is the only input that reliably costs an
    /// attacker anything. A rule that makes the median password worse is a rule
    /// that loses accounts.
    ///
    /// **EIGHT, up from six.** Six characters is roughly 2 x 10^9 candidates
    /// against a hash this server computes in about 100ms - a targeted offline
    /// crack of one leaked row is hours of a single GPU. Eight is four thousand
    /// times that space for the cost of two keystrokes, and eight is what every
    /// mainstream guideline settles on.
    ///
    /// **ENFORCED AT REGISTRATION ONLY**, which is the only place this server
    /// sets a password. Existing accounts holding a six-character password keep
    /// working: raising a minimum must never lock somebody out of an account
    /// they already have, and there is no change-password flow to walk them
    /// through it. If one is ever added, that is where a re-check belongs.
    ///
    /// **AND A MAXIMUM**, which is about denial of service rather than
    /// security. PBKDF2 at 210,000 iterations over a ten-megabyte string is a
    /// free way to burn a core, and an unauthenticated endpoint accepts it.
    /// 256 is far past any real passphrase.
    /// </summary>
    public static class PasswordPolicy
    {
        public const int MinLength = 8;
        public const int MaxLength = 256;

        public static bool IsAcceptable(string? password)
            => !string.IsNullOrEmpty(password)
            && password.Length >= MinLength
            && password.Length <= MaxLength;
    }
}

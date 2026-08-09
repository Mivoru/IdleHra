using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One outstanding "let me back in" ticket.
    ///
    /// THE TOKEN ITSELF IS NOT IN HERE. Only its SHA-256 hash is stored, for
    /// the same reason PasswordHash exists: a leaked database must not hand
    /// over the accounts it describes. A reset token is a bearer credential -
    /// whoever holds it can take the account - so storing it in the clear would
    /// make this table strictly worse than the password column beside it.
    ///
    /// SHA-256 rather than PBKDF2, and that is deliberate rather than lazy. The
    /// token is 32 bytes from a cryptographic RNG, so there is no dictionary to
    /// run and nothing for a slow hash to buy; the iteration count on a password
    /// exists to compensate for humans choosing "hunter2". Adding 210,000
    /// iterations here would only make a legitimate reset slower.
    /// </summary>
    [Table("password_reset_tokens")]
    public class PasswordResetToken
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>Base64 of the SHA-256 of the token that was emailed.</summary>
        [Required]
        [MaxLength(64)]
        public string TokenHash { get; set; } = string.Empty;

        public long CreatedAtEpoch { get; set; }

        /// <summary>
        /// Short on purpose - see PasswordResetRules. A ticket that is valid
        /// for a week is a week in which a forwarded email is an account.
        /// </summary>
        public long ExpiresAtEpoch { get; set; }

        /// <summary>
        /// When it was spent, or 0. SINGLE USE: the row is kept rather than
        /// deleted so a second attempt with the same link can be told apart
        /// from a link that never existed, and so the reset is auditable.
        /// </summary>
        public long UsedAtEpoch { get; set; }
    }
}

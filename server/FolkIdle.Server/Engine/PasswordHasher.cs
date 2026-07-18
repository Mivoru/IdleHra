using System;
using System.Globalization;
using System.Security.Cryptography;

namespace FolkIdle.Server.Engine
{
    // Modul: Email/Password Auth. PBKDF2-HMACSHA256 password hashing - no
    // external package dependency (Rfc2898DeriveBytes ships in
    // System.Security.Cryptography), matching this codebase's established
    // preference for hand-rolled primitives over a package dependency for a
    // single, well-understood algorithm (see AuthenticationEngine's own
    // hand-rolled JWT implementation). Stored format is
    // "{iterations}.{saltBase64}.{hashBase64}" - the iteration count travels
    // with the hash so it can be raised in the future without invalidating
    // already-stored hashes.
    public static class PasswordHasher
    {
        private const int SaltSizeBytes = 16;
        private const int HashSizeBytes = 32;

        // OWASP 2023 minimum recommendation for PBKDF2-HMAC-SHA256.
        private const int Iterations = 210_000;

        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);
            return Iterations.ToString(CultureInfo.InvariantCulture) + "." + Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
        }

        // Modul: constant-time comparison via CryptographicOperations.
        // FixedTimeEquals - matches AuthenticationEngine.ValidateJwt's own
        // signature-check convention, so a failed password check does not
        // leak timing information about how many leading bytes matched.
        public static bool Verify(string password, string? storedHash)
        {
            if (string.IsNullOrEmpty(storedHash))
            {
                return false;
            }

            string[] parts = storedHash.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int iterations) || iterations <= 0)
            {
                return false;
            }

            byte[] salt;
            byte[] expectedHash;
            try
            {
                salt = Convert.FromBase64String(parts[1]);
                expectedHash = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException)
            {
                return false;
            }

            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
    }
}

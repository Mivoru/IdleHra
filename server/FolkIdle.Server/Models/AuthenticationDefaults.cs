namespace FolkIdle.Server.Models
{
    public static class AuthenticationDefaults
    {
        // Only ever consulted when JWT_SECRET_KEY is unset and
        // DOTNET_ENVIRONMENT is not Production - mirrors
        // ConnectionStringDefaults.LocalDevelopmentFallback's exact guard
        // shape. A fixed, publicly-known key is fine for local development
        // (anyone running a local server already has full DB access anyway);
        // Program.cs throws at startup instead of ever falling back to this
        // in Production.
        public const string LocalDevelopmentFallback = "folkidle-local-development-jwt-secret-key-not-for-production-use";
    }
}

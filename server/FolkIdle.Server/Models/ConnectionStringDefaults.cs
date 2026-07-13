namespace FolkIdle.Server.Models
{
    public static class ConnectionStringDefaults
    {
        // Only ever consulted when FOLKIDLE_DB_CONN is unset. Points at a local-only
        // Postgres instance; never resolves outside a developer's own machine.
        public const string LocalDevelopmentFallback = "Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres";
    }
}

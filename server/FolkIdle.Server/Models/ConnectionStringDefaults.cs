namespace FolkIdle.Server.Models
{
    public static class ConnectionStringDefaults
    {
        // Only ever consulted when FOLKIDLE_DB_CONN is unset. Points at a local-only
        // Postgres instance; never resolves outside a developer's own machine.
        public const string LocalDevelopmentFallback = "Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres";

        /// <summary>
        /// The pool ceiling that keeps this process under the database's own
        /// client limit. Supabase's session pooler allows 15; this leaves room
        /// for the migration and admin connections that do not come from the
        /// pool.
        /// </summary>
        public const int DefaultMaxPoolSize = 12;

        /// <summary>
        /// Caps Npgsql's client-side pool unless the caller already stated one.
        ///
        /// Modul: WITHOUT THIS, BACK-PRESSURE ARRIVES AS AN EXCEPTION.
        ///
        /// Npgsql defaults to 100 connections. The production database refuses
        /// the sixteenth with `XX000 (EMAXCONNSESSION) max clients reached in
        /// session mode`, so the limit was being enforced by the SERVER, at the
        /// moment of use, on whichever unlucky operation asked - which is how a
        /// background worker with no exception handling died and took every
        /// equipment drop in the game with it.
        ///
        /// Capped below the server's ceiling, the same load waits in Npgsql's
        /// queue instead. A slow acquire is recoverable; a throw was not.
        ///
        /// An explicit "Maximum Pool Size" in the connection string always wins
        /// - this is a floor under carelessness, not a policy.
        /// </summary>
        public static string WithBoundedPool(string connectionString, string? overrideMaxPoolSize)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;

            // Modul: ASK THE STRING, NOT THE BUILDER.
            //
            // NpgsqlConnectionStringBuilder.ContainsKey answers for every key it
            // KNOWS, not for the ones actually present, so it returns true for
            // "Maximum Pool Size" on a string that never mentioned it - and this
            // method then returned the input unchanged and capped nothing. Its
            // own tests caught that; production would have shown it as the bug
            // it was written to prevent, months later.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    connectionString,
                    @"max(imum)?\s*pool\s*size",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return connectionString;
            }

            var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);

            int max = DefaultMaxPoolSize;
            if (int.TryParse(overrideMaxPoolSize, out int parsed) && parsed > 0)
            {
                max = parsed;
            }

            builder.MaxPoolSize = max;
            return builder.ConnectionString;
        }
    }
}

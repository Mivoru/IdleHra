using System;
using System.IO;

namespace FolkIdle.Server.Engine
{
    // Modul: loads a store-API credential (a service-account key, shared
    // secret, or public key) from a file path injected via an environment
    // variable - never a hardcoded value, and never the secret VALUE itself
    // in an environment variable. A raw env-var value leaks far more easily
    // than a file that only the container's own filesystem exposes (it is
    // visible via /proc/<pid>/environ, process listings, and crash dumps,
    // none of which expose file contents the same way). This is the
    // standard Kubernetes pattern of mounting a Secret as a volume and
    // pointing an environment variable at the resulting file path -
    // deployment.yaml's existing FOLKIDLE_DB_CONN/REDIS_CONNECTION/
    // JWT_SECRET_KEY wiring injects secret VALUES directly instead, which is
    // acceptable there because those are read exactly once at boot; a
    // store-API credential specifically needs to be re-readable without a
    // pod restart so a rotated credential (the mounted file replaced
    // in place by whatever is rotating the underlying Kubernetes Secret)
    // takes effect on its own, which is what this class's caching window
    // exists for.
    public sealed class SecretRotationManager
    {
        private readonly string _secretFilePathEnvironmentVariable;
        private readonly TimeSpan _cacheDuration;

        private readonly object _lock = new object();
        private string? _cachedValue;
        private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;

        public SecretRotationManager(string secretFilePathEnvironmentVariable, TimeSpan? cacheDuration = null)
        {
            _secretFilePathEnvironmentVariable = secretFilePathEnvironmentVariable;
            _cacheDuration = cacheDuration ?? TimeSpan.FromMinutes(5);
        }

        // Returns the current secret value, re-reading its source file once
        // the cache window has elapsed. Returns null if the environment
        // variable naming the secret's file path is not set, or the file
        // it names does not exist - callers must treat that as "no
        // credential configured" and fail closed (reject the operation
        // requiring it), never fall back to a hardcoded or default value.
        public string? GetCurrentSecret()
        {
            lock (_lock)
            {
                if (_cachedValue != null && DateTimeOffset.UtcNow - _cachedAtUtc < _cacheDuration)
                {
                    return _cachedValue;
                }

                string? path = Environment.GetEnvironmentVariable(_secretFilePathEnvironmentVariable);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    _cachedValue = null;
                    _cachedAtUtc = DateTimeOffset.UtcNow;
                    return null;
                }

                _cachedValue = File.ReadAllText(path).Trim();
                _cachedAtUtc = DateTimeOffset.UtcNow;
                return _cachedValue;
            }
        }

        // Forces the next GetCurrentSecret call to re-read the source file
        // regardless of the cache window - useful for tests, or for an
        // operator-triggered rotation signal that should not wait out the
        // normal cache duration.
        public void InvalidateCache()
        {
            lock (_lock)
            {
                _cachedValue = null;
                _cachedAtUtc = DateTimeOffset.MinValue;
            }
        }
    }
}

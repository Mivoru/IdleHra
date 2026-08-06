using System;
using System.Collections.Concurrent;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// A per-address budget for the authentication endpoints.
    ///
    /// THERE WAS NONE. Eight wrong passwords in a row against a live account
    /// returned eight plain 401s with no delay, no lockout and no counter, so
    /// an attacker had unlimited guesses against any email they knew.
    ///
    /// It is also a denial of service, and that half is worse. Every attempt
    /// runs PBKDF2 at 210,000 iterations - which is exactly right for storing a
    /// password and means each guess costs the server something like a tenth of
    /// a second of CPU. A few hundred requests a second is therefore not a
    /// brute force problem, it is the whole box.
    ///
    /// So the budget counts REQUESTS, not failures. Counting only failures
    /// would leave the CPU cost of a valid-looking flood unbounded, and a
    /// human logging in spends one or two of fifteen.
    ///
    /// In memory on purpose: there is one server process, and a throttle that
    /// needs a round trip to Redis to decide whether to do work has already
    /// done work. If this ever runs at more than one replica the counter wants
    /// to move, and until then a shared store would be ceremony.
    /// </summary>
    public static class AuthThrottle
    {
        public const int MaxRequestsPerWindow = 15;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        private sealed class Budget
        {
            public int Count;
            public long WindowStartedAtTicks;
        }

        private static readonly ConcurrentDictionary<string, Budget> _budgets = new(StringComparer.Ordinal);
        private static long _lastSweepTicks;

        /// <summary>
        /// True when this address may make another authentication request.
        /// </summary>
        public static bool TryConsume(string clientAddress)
        {
            if (string.IsNullOrEmpty(clientAddress))
            {
                // An address we cannot identify is one we cannot budget. Letting
                // it through is the wrong failure: it would be the obvious way
                // around this, and stripping a header is free.
                clientAddress = "unidentified";
            }

            long now = DateTime.UtcNow.Ticks;
            SweepIfDue(now);

            var budget = _budgets.GetOrAdd(clientAddress, _ => new Budget { WindowStartedAtTicks = now });

            lock (budget)
            {
                if (now - budget.WindowStartedAtTicks >= Window.Ticks)
                {
                    budget.WindowStartedAtTicks = now;
                    budget.Count = 0;
                }

                if (budget.Count >= MaxRequestsPerWindow)
                {
                    return false;
                }

                budget.Count++;
                return true;
            }
        }

        /// <summary>
        /// WHOSE address this is, behind the reverse proxy.
        ///
        /// Caddy terminates TLS and proxies to the app, so every connection
        /// arrives from the Docker network's gateway. Keying the budget on that
        /// would put every player in the world in ONE bucket, and the first
        /// attacker would lock out everybody - a throttle that is worse than no
        /// throttle.
        ///
        /// X-Forwarded-For is trusted here because nothing but our own Caddy can
        /// reach the app: it is not published to the host (see the compose
        /// file's `expose`, not `ports`). If the app is ever exposed directly
        /// this becomes a header an attacker sets freely, and the budget becomes
        /// decorative - which is the reason this comment is longer than the
        /// method.
        /// </summary>
        public static string ResolveClientAddress(System.Net.HttpListenerRequest request)
        {
            string forwarded = request.Headers["X-Forwarded-For"] ?? string.Empty;
            string remote = string.Empty;
            try
            {
                remote = request.RemoteEndPoint?.Address?.ToString() ?? string.Empty;
            }
            catch (System.Net.HttpListenerException)
            {
                // A connection that dropped before the address could be read.
                // An unknown caller still gets a budget - see below.
            }

            return ResolveClientAddress(forwarded, remote);
        }

        /// <summary>
        /// The rule itself, as a function of the two strings it depends on -
        /// which is what makes it testable without a fake HttpListenerRequest.
        /// The first attempt at this test reflected into a half-constructed
        /// request object to plant headers, and that is a sign the argument
        /// list is wrong rather than a sign the test needs cleverness.
        /// </summary>
        public static string ResolveClientAddress(string? forwardedForHeader, string? remoteAddress)
        {
            if (!string.IsNullOrEmpty(forwardedForHeader))
            {
                // First entry is the original client; the rest are proxies.
                int comma = forwardedForHeader.IndexOf(',');
                string first = comma >= 0 ? forwardedForHeader.Substring(0, comma) : forwardedForHeader;
                first = first.Trim();
                if (first.Length > 0)
                {
                    return first;
                }
            }

            return remoteAddress ?? string.Empty;
        }

        // Entries are tiny and the traffic is small, but an unbounded dictionary
        // keyed by attacker-chosen addresses is its own slow leak. Swept at most
        // once a window, and only entries whose window has fully elapsed.
        private static void SweepIfDue(long now)
        {
            long last = System.Threading.Interlocked.Read(ref _lastSweepTicks);
            if (now - last < Window.Ticks)
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last)
            {
                return;
            }

            foreach (var pair in _budgets)
            {
                if (now - System.Threading.Volatile.Read(ref pair.Value.WindowStartedAtTicks) >= Window.Ticks * 2)
                {
                    _budgets.TryRemove(pair.Key, out _);
                }
            }
        }

        /// <summary>Test seam - the counter is process-wide static state.</summary>
        public static void ResetForTests() => _budgets.Clear();
    }
}

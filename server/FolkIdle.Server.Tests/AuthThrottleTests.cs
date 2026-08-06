using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    // Modul: the authentication endpoints had no budget at all.
    //
    // Eight wrong passwords in a row against the live server returned eight
    // plain 401s - measured, not assumed - so any known email could be guessed
    // at indefinitely. The second half is worse than the first: every attempt
    // runs PBKDF2 at 210,000 iterations, so a few hundred requests a second is
    // not a login problem, it is the whole machine's CPU.
    //
    // These are unit tests over static process-wide state, so each one resets
    // it first. That is a real smell and the right trade here: a throttle that
    // needs a round trip to a shared store before deciding whether to do work
    // has already done the work.
    public class AuthThrottleTests
    {
        [Fact]
        public void Test_AuthThrottle_AllowsANormalLoginAndStopsAFlood()
        {
            AuthThrottle.ResetForTests();

            // A human logging in spends one or two of these.
            for (int i = 0; i < AuthThrottle.MaxRequestsPerWindow; i++)
            {
                Assert.True(AuthThrottle.TryConsume("198.51.100.7"),
                    $"attempt {i + 1} of the budget must be allowed");
            }

            Assert.False(AuthThrottle.TryConsume("198.51.100.7"),
                "the attempt past the budget must be refused");
        }

        [Fact]
        public void Test_AuthThrottle_BudgetsEachAddressSeparately()
        {
            AuthThrottle.ResetForTests();

            for (int i = 0; i < AuthThrottle.MaxRequestsPerWindow; i++)
            {
                AuthThrottle.TryConsume("198.51.100.7");
            }

            // THE FAILURE THIS PREVENTS IS NOT THE ATTACKER'S. If the budget
            // were shared - which is exactly what happens when the throttle
            // keys on the socket address behind a reverse proxy, because every
            // request then arrives from the proxy - the first attacker would
            // lock every other player out of the game.
            Assert.True(AuthThrottle.TryConsume("203.0.113.9"),
                "one address exhausting its budget must not spend anyone else's");
        }

        [Fact]
        public void Test_AuthThrottle_ReadsTheForwardedClientRatherThanTheProxy()
        {
            // The first entry is the original client; everything after it is a
            // proxy that handled the request on the way in. Behind Caddy the
            // socket address is always the Docker gateway, so keying on it
            // would put every player in one bucket.
            Assert.Equal(
                "203.0.113.9",
                AuthThrottle.ResolveClientAddress("203.0.113.9, 172.18.0.4", "172.18.0.4"));

            // With no proxy in front, the socket is the client.
            Assert.Equal("198.51.100.7", AuthThrottle.ResolveClientAddress(null, "198.51.100.7"));

            // A header present but empty must not resolve to an empty address
            // that everyone shares by accident.
            Assert.Equal("198.51.100.7", AuthThrottle.ResolveClientAddress("   ", "198.51.100.7"));
        }

        [Fact]
        public void Test_AuthThrottle_AnAddressItCannotIdentifyStillHasABudget()
        {
            AuthThrottle.ResetForTests();

            // Stripping a header must not be a way around the budget - an
            // unidentifiable caller shares one bucket rather than being waved
            // through.
            string resolved = AuthThrottle.ResolveClientAddress(null, null);

            for (int i = 0; i < AuthThrottle.MaxRequestsPerWindow; i++)
            {
                Assert.True(AuthThrottle.TryConsume(resolved));
            }

            Assert.False(AuthThrottle.TryConsume(resolved));
        }
    }
}

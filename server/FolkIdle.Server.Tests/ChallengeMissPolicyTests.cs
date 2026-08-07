using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Being on a phone is not cheating.
    ///
    /// The anti-cheat issues a challenge and expects an answer inside fifteen
    /// seconds; four consecutive misses quarantined the account PERMANENTLY,
    /// which stops the simulation entirely and silently. This is an idle game
    /// largely played on phones, and a phone that locks its screen or switches
    /// apps throttles the tab's timers - so a client behaving perfectly cannot
    /// answer in time, and four ordinary interruptions banned it.
    ///
    /// It happened to a real account, twice, and the live log named the
    /// detector: "QUARANTINE applied to player 8 (reason 54, detail 4)".
    ///
    /// The rule now: a cheating client cannot stay silent, because faking state
    /// is pointless unless it also SENDS something. So a miss only counts
    /// against a client that was otherwise talking during the window.
    /// </summary>
    public class ChallengeMissPolicyTests
    {
        private readonly ITestOutputHelper _output;

        public ChallengeMissPolicyTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// The decision the tick makes, in the same shape it makes it: given
        /// how long ago the client last said anything, does an unanswered
        /// challenge count against it?
        /// </summary>
        private static bool CountsAsAMiss(long msSinceLastClientCommand)
            => msSinceLastClientCommand <= AntiCheatTelemetryEngine.ChallengeResponseWindowMs;

        [Fact]
        public void ABackgroundedTabIsNotPenalised()
        {
            // A phone with the screen off has sent nothing for minutes.
            Assert.False(CountsAsAMiss(60_000));
            Assert.False(CountsAsAMiss(AntiCheatTelemetryEngine.ChallengeResponseWindowMs + 1));
        }

        [Fact]
        public void AClientThatIsTalkingButNotAnsweringIsPenalised()
        {
            // Sending commands and ignoring the challenge is the shape of a
            // client that has been modified to skip it.
            Assert.True(CountsAsAMiss(0));
            Assert.True(CountsAsAMiss(AntiCheatTelemetryEngine.ChallengeResponseWindowMs));
        }

        /// <summary>
        /// The window has to be long enough that an ordinary round trip on a
        /// mobile network is not a miss, and the limit high enough that one bad
        /// moment is not a ban. Pinned because these two numbers ARE the policy,
        /// and the last time they were tightened it cost real players.
        /// </summary>
        [Fact]
        public void ThePolicyLeavesRoomForARealNetwork()
        {
            _output.WriteLine(
                $"window {AntiCheatTelemetryEngine.ChallengeResponseWindowMs}ms, " +
                $"limit {AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit} consecutive");

            Assert.True(AntiCheatTelemetryEngine.ChallengeResponseWindowMs >= 10_000,
                "a window under ten seconds detects latency, not cheating");
            Assert.True(AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit >= 3,
                "one or two misses must never be enough to quarantine an account");
        }
    }
}

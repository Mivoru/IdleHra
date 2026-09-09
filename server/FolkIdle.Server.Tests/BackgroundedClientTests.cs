using System;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A BACKGROUNDED PHONE MUST NOT LOOK LIKE A CHEAT.
    ///
    /// The challenge-miss branch quarantines an account after four consecutive
    /// unanswered challenges, and a quarantine halts the tick and rejects every
    /// command. It already carries the guard that makes that safe: a miss only
    /// counts against a client that was otherwise SENDING commands during the
    /// window, because a silent client is a backgrounded one and a cheating
    /// client cannot stay silent - faking state is pointless unless it also
    /// sends something.
    ///
    /// Modul: THE GUARD READ A FLAG ALMOST NOTHING WROTE. LastClientCommandAtMs
    /// was stamped inside the activity-change drain and nowhere else, so a
    /// player who equipped, spent, chatted and fought for ten minutes without
    /// changing activity read as SILENT, while a player who changed activity
    /// once and then locked their phone read as TALKING - the exact inversion
    /// of the intent. It failed open far more often than closed, so nobody was
    /// wrongly banned by it; it simply was not the check its own comment
    /// described. This pins the shape of both halves.
    ///
    /// The escalation itself lives inside SimulationEngine's tick loop, so what
    /// is asserted here is the RULE it applies, against the same constants and
    /// the same clock arithmetic.
    /// </summary>
    public class BackgroundedClientTests
    {
        private readonly ITestOutputHelper _output;
        public BackgroundedClientTests(ITestOutputHelper output) => _output = output;

        /// <summary>Mirrors the miss branch's own test, against the payload field it reads.</summary>
        private static bool CountsAsTalking(TickStatePayload payload, long nowMs)
            => nowMs - payload.LastClientCommandAtMs <= AntiCheatTelemetryEngine.ChallengeResponseWindowMs;

        [Fact]
        public void AClientThatHasSentNothingRecentlyIsNotCountedAgainst()
        {
            var payload = new TickStatePayload { PlayerId = 1 };

            long now = 10_000_000L;
            // Backgrounded for a minute: four times the response window.
            payload.LastClientCommandAtMs = now - AntiCheatTelemetryEngine.ChallengeResponseWindowMs * 4;

            _output.WriteLine(
                $"silent for {(now - payload.LastClientCommandAtMs) / 1000}s against a " +
                $"{AntiCheatTelemetryEngine.ChallengeResponseWindowMs / 1000}s window");

            Assert.False(CountsAsTalking(payload, now));
        }

        [Fact]
        public void AClientSendingCommandsInsideTheWindowIsCountedAgainst()
        {
            var payload = new TickStatePayload { PlayerId = 1 };

            long now = 10_000_000L;
            payload.LastClientCommandAtMs = now - (AntiCheatTelemetryEngine.ChallengeResponseWindowMs / 2);

            Assert.True(CountsAsTalking(payload, now));
        }

        [Fact]
        public void AFreshPayloadIsNotTalking()
        {
            // Modul: a zero stamp is the state a session starts in and the state
            // a reconstructed one comes back in. It must read as SILENT, or a
            // cold-booted account would be one unanswered challenge closer to a
            // quarantine for having just been restored.
            var payload = new TickStatePayload { PlayerId = 1 };

            Assert.Equal(0, payload.LastClientCommandAtMs);
            Assert.False(CountsAsTalking(payload, AntiCheatTelemetryEngine.ChallengeResponseWindowMs + 1));
        }

        [Fact]
        public void FourMissesAreNeededAndTheLimitIsWhatTheEngineUses()
        {
            // Guards the constant rather than restating it: a limit quietly
            // dropped to 1 is how the FIRST version of this feature quarantined
            // real players for a slow network.
            Assert.True(AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit >= 4,
                "a run shorter than four is a latency detector, not a cheat detector");
            Assert.True(AntiCheatTelemetryEngine.ChallengeResponseWindowMs >= 10_000,
                "a window under ten seconds cannot be met by a phone that just woke up");
        }

        /// <summary>
        /// Modul: the server's own Logout and ReloadState must NOT count as the
        /// client talking. They are enqueued by the server - Logout by the
        /// socket-closure block, ReloadState by half a dozen engines - so
        /// treating them as client traffic would make a silent client look busy
        /// at exactly the moment the guard is meant to protect it.
        /// </summary>
        [Fact]
        public void TheServersOwnCommandsAreNotClientTraffic()
        {
            Assert.True(SimulationEngine.IsServerInternalCommand(CommandType.Logout));
            Assert.True(SimulationEngine.IsServerInternalCommand(CommandType.ReloadState));

            // And a real player action is.
            Assert.False(SimulationEngine.IsServerInternalCommand(CommandType.SpendAttributePoint));
            Assert.False(SimulationEngine.IsServerInternalCommand(CommandType.EquipItem));
        }
    }
}

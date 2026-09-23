using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE COMMAND GATE'S ORDER, PINNED BEFORE ANYONE MOVES IT.
    ///
    /// Every non-internal client command passes through four checks in
    /// SimulationEngine.EngineLoop - epoch synchronisation, the command-rate
    /// validator, the anti-cheat payload check and the push/compliance payload
    /// check - after the "was this client talking" stamp. Until these tests
    /// were written nothing in the repository asserted that order: it was held
    /// only by the physical order of four `if` statements inside a very long
    /// loop body. The order is observable only when two checks fail at once,
    /// because the anti-cheat check SHADOW-BANS (the session survives) while
    /// the other three TERMINATE it. So each ordering test sends one packet
    /// that fails two checks and asserts which answer won.
    ///
    /// These drive the real EngineLoop on its real background thread through
    /// InjectVirtualPlayer/InjectBenchmarkCommand, so they also pin that the
    /// loop still CALLS the gate, which a direct test of an extracted gate
    /// cannot see.
    ///
    /// The engine is built with no AntiCheatTelemetryEngine, so a shadow ban
    /// is observable only as "the command was skipped and the session
    /// survived" - which is exactly the distinction these tests need.
    /// </summary>
    [Collection("Postgres collection")]
    public class CommandGateOrderingTests
    {
        private readonly PostgresTestFixture _fixture;

        public CommandGateOrderingTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private SimulationEngine CreateEngine()
        {
            var serviceProvider = _fixture.ServiceProvider;
            var playerRegistry = _fixture.PlayerRegistry;
            var contextFactory = _fixture.DbContextFactory;

            // Never Start()-ed: these tests inject players and commands
            // directly, so no socket is involved.
            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8097/");

            return new SimulationEngine(
                new LootTableEngine(),
                new StateCheckpointManager(serviceProvider),
                networkSystem,
                new ForgeSplicingEngine(serviceProvider),
                new MarketOrderBookEngine(serviceProvider, playerRegistry),
                playerRegistry,
                new GuildContributionEngine(serviceProvider),
                new MarketEscrowEngine(serviceProvider, playerRegistry),
                new MailboxAndBankEngine(serviceProvider, playerRegistry),
                new AffixRerollEngine(serviceProvider),
                new BreedingEngine(serviceProvider, playerRegistry),
                new GuildLogisticsEngine(serviceProvider, playerRegistry),
                new CraftingEngine(contextFactory, playerRegistry, _fixture.RetryingOptions),
                new WorldBossEngine(serviceProvider, playerRegistry),
                new VillageManagementEngine(serviceProvider, playerRegistry),
                new GuildWarEngine(serviceProvider),
                new LegacyStoreEngine(serviceProvider, playerRegistry),
                new GuildLogisticsDepotEngine(serviceProvider, playerRegistry),
                new GuildCombatSimulationEngine(serviceProvider, playerRegistry),
                null!, null!, null!, null!, null!, contextFactory);
        }

        // A suspended payload is never ticked, so nothing but a command can
        // move its ActiveActivityId - which makes that field a clean probe
        // for "this command reached its handler".
        private static TickStatePayload SuspendedPlayer(long playerId, long epoch)
        {
            return new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = epoch,
                ActiveActivityId = 42,
                IsSuspended = true,
                InventorySpaceRemaining = 1000
            };
        }

        // ChangeActivity to idle (0) is accepted by every gate check for a
        // synchronized client and needs no content, no database and no
        // location - the cheapest command with a visible effect.
        private static ClientCommandPacket ProbeToIdle(long epoch)
        {
            return new ClientCommandPacket
            {
                Command = CommandType.ChangeActivity,
                TargetId = 0,
                LogicEpochCounter = epoch
            };
        }

        private static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition()) return;
                await Task.Delay(50);
            }
            Assert.Fail(failureMessage);
        }

        [Fact]
        public async Task WellFormedCommand_FromSynchronizedClient_IsAccepted()
        {
            const long playerId = 970009501L;
            var engine = CreateEngine();
            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(SuspendedPlayer(playerId, epoch: 7L));

                engine.InjectBenchmarkCommand(playerId, ProbeToIdle(epoch: 7L));

                await WaitForConditionAsync(
                    () => engine.GetActivePlayerActiveActivityId(playerId) == 0L,
                    "A synchronized client's well-formed ChangeActivity never reached its handler.");
                Assert.True(engine.IsActivePlayerPresent(playerId));
            }
            finally
            {
                engine.Stop();
            }
        }

        [Fact]
        public async Task StaleEpoch_SeversTheSession()
        {
            const long playerId = 970009502L;
            var engine = CreateEngine();
            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(SuspendedPlayer(playerId, epoch: 500L));

                engine.InjectBenchmarkCommand(playerId, new ClientCommandPacket
                {
                    Command = CommandType.ReportUiContextSwitch,
                    LogicEpochCounter = 0L
                });

                await WaitForConditionAsync(
                    () => !engine.IsActivePlayerPresent(playerId),
                    "A command carrying a stale LogicEpochCounter did not sever the session.");
            }
            finally
            {
                engine.Stop();
            }
        }

        /// <summary>
        /// The live defect CLAUDE.md records: the server's own Logout carries a
        /// zeroed epoch, and the gate used to answer it with
        /// TerminateSessionForSecurity - which removes the player WITHOUT the
        /// flush Logout exists to perform. Both paths remove the player, so
        /// the distinguishing observation is the flush: LastLogoutTimestamp
        /// reaches the database only if the Logout handler ran.
        /// </summary>
        [Fact]
        public async Task ServerInternalLogout_WithZeroedEpoch_IsFlushedNotSevered()
        {
            const long playerId = 970009503L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    LogicEpochCounter = 500L
                });
                await db.SaveChangesAsync();
            }

            var engine = CreateEngine();
            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(SuspendedPlayer(playerId, epoch: 500L));

                engine.InjectBenchmarkCommand(playerId, new ClientCommandPacket
                {
                    Command = CommandType.Logout,
                    LogicEpochCounter = 0L
                });

                await WaitForConditionAsync(
                    () => !engine.IsActivePlayerPresent(playerId),
                    "The server's own Logout never removed the player.");

                long flushedLogout = 0L;
                for (int i = 0; i < 100 && flushedLogout == 0L; i++)
                {
                    await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
                    flushedLogout = await verifyDb.PlayerRecords.AsNoTracking()
                        .Where(p => p.Id == playerId)
                        .Select(p => p.LastLogoutTimestamp)
                        .SingleAsync();
                    if (flushedLogout == 0L) await Task.Delay(50);
                }

                Assert.True(flushedLogout > 0L,
                    "Logout with a zeroed epoch was terminated by the epoch gate instead of reaching its handler - the session was dropped without its flush.");
            }
            finally
            {
                engine.Stop();
            }
        }

        /// <summary>
        /// A command for a player with no _activePlayers entry is skipped
        /// BEFORE the gate: had the gate run, this stale-epoch packet would
        /// have gone through TerminateSessionForSecurity, which unregisters the
        /// player from PlayerSessionRegistry.
        /// </summary>
        [Fact]
        public async Task CommandForInactivePlayer_IsSkippedBeforeTheGate()
        {
            const long activePlayerId = 970009504L;
            const long inactivePlayerId = 970009505L;
            var engine = CreateEngine();
            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(SuspendedPlayer(activePlayerId, epoch: 3L));
                _fixture.PlayerRegistry.RegisterPlayer(inactivePlayerId);

                engine.InjectBenchmarkCommand(inactivePlayerId, new ClientCommandPacket
                {
                    Command = CommandType.ReportUiContextSwitch,
                    LogicEpochCounter = 999999L
                });
                engine.InjectBenchmarkCommand(activePlayerId, ProbeToIdle(epoch: 3L));

                await WaitForConditionAsync(
                    () => engine.GetActivePlayerActiveActivityId(activePlayerId) == 0L,
                    "The command loop did not carry on past a command for an inactive player.");

                Assert.False(engine.IsActivePlayerPresent(inactivePlayerId));
                Assert.True(_fixture.PlayerRegistry.IsPlayerOnline(inactivePlayerId),
                    "A command for a player with no live payload reached the gate and was terminated, instead of being skipped.");
            }
            finally
            {
                _fixture.PlayerRegistry.UnregisterPlayer(inactivePlayerId);
                engine.Stop();
            }
        }

        /// <summary>
        /// THE ORDER GUARD. One packet failing both the epoch check
        /// (terminate) and the anti-cheat payload check (shadow ban). Epoch
        /// runs first, so the session is terminated. Any reorder that puts the
        /// anti-cheat check first leaves the session alive.
        /// </summary>
        [Fact]
        public async Task EpochCheck_RunsBeforeAntiCheatPayloadCheck()
        {
            const long playerId = 970009506L;
            var engine = CreateEngine();
            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(SuspendedPlayer(playerId, epoch: 500L));

                engine.InjectBenchmarkCommand(playerId, new ClientCommandPacket
                {
                    Command = CommandType.ReportUiContextSwitch,
                    LogicEpochCounter = 0L,
                    ChallengeId = 1
                });

                await WaitForConditionAsync(
                    () => !engine.IsActivePlayerPresent(playerId),
                    "A packet failing both the epoch and the anti-cheat payload checks was shadow-banned rather than terminated - the gate's order changed.");
            }
            finally
            {
                engine.Stop();
            }
        }

        /// <summary>
        /// The mirror at the other end of the gate. A synchronized packet
        /// failing both the anti-cheat payload check (shadow ban - the session
        /// survives) and the push/compliance payload check (terminate). The
        /// anti-cheat check runs first, so the session survives; a reorder
        /// that put the push check first would sever it.
        ///
        /// The literal mirror the plan asked about - push AND epoch failing
        /// together - cannot distinguish an order, since both terminate.
        /// </summary>
        [Fact]
        public async Task AntiCheatPayloadCheck_RunsBeforePushCompliancePayloadCheck()
        {
            const long playerId = 970009507L;
            var engine = CreateEngine();
            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(SuspendedPlayer(playerId, epoch: 11L));

                engine.InjectBenchmarkCommand(playerId, new ClientCommandPacket
                {
                    Command = CommandType.ReportUiContextSwitch,
                    LogicEpochCounter = 11L,
                    ChallengeId = 1,
                    TargetLanguageId = 1
                });
                engine.InjectBenchmarkCommand(playerId, ProbeToIdle(epoch: 11L));

                await WaitForConditionAsync(
                    () => !engine.IsActivePlayerPresent(playerId) || engine.GetActivePlayerActiveActivityId(playerId) == 0L,
                    "Neither outcome was observed.");

                Assert.True(engine.IsActivePlayerPresent(playerId),
                    "A packet failing both the anti-cheat and push/compliance payload checks was terminated rather than shadow-banned - the gate's order changed.");
                Assert.Equal(0L, engine.GetActivePlayerActiveActivityId(playerId));
            }
            finally
            {
                engine.Stop();
            }
        }

        // ------------------------------------------------------------------
        // Direct calls to the extracted gate - cheap, no container, no
        // thread. They pin the gate's logic; the behavioural tests above pin
        // that EngineLoop still CALLS it. Both levels are deliberate.
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluate_Synchronized_Proceeds_AndStampsTheClientAsTalking()
        {
            var payload = new TickStatePayload { PlayerId = 1L, LogicEpochCounter = 7L };
            var cmd = new ClientCommandPacket { Command = CommandType.ReportUiContextSwitch, LogicEpochCounter = 7L };

            Assert.Equal(CommandGateVerdict.Proceed, CommandGate.Evaluate(ref payload, ref cmd));
            Assert.NotEqual(0L, payload.LastClientCommandAtMs);
        }

        [Fact]
        public void Evaluate_InternalCommand_WithZeroedEpoch_Proceeds_WithoutStamping()
        {
            foreach (var internalCommand in new[] { CommandType.Logout, CommandType.ReloadState })
            {
                var payload = new TickStatePayload { PlayerId = 1L, LogicEpochCounter = 500L };
                var cmd = new ClientCommandPacket { Command = internalCommand, LogicEpochCounter = 0L };

                Assert.Equal(CommandGateVerdict.Proceed, CommandGate.Evaluate(ref payload, ref cmd));
                Assert.Equal(0L, payload.LastClientCommandAtMs);
            }
        }

        [Fact]
        public void Evaluate_StampHappensBeforeAnyCheck_EvenWhenTerminated()
        {
            var payload = new TickStatePayload { PlayerId = 1L, LogicEpochCounter = 500L };
            var cmd = new ClientCommandPacket { Command = CommandType.ReportUiContextSwitch, LogicEpochCounter = 0L };

            Assert.Equal(CommandGateVerdict.Terminate, CommandGate.Evaluate(ref payload, ref cmd));
            Assert.NotEqual(0L, payload.LastClientCommandAtMs);
        }

        [Fact]
        public void Evaluate_EpochBeforeRateValidator_RateStampUntouchedOnStaleEpoch()
        {
            // ChangeActivity is rate-limited by ValidateCommand, which stamps
            // LastCommandTimestamp when it passes. A stale epoch must stop the
            // gate before that validator ever runs.
            var payload = new TickStatePayload { PlayerId = 1L, LogicEpochCounter = 500L };
            var cmd = new ClientCommandPacket { Command = CommandType.ChangeActivity, LogicEpochCounter = 0L };

            Assert.Equal(CommandGateVerdict.Terminate, CommandGate.Evaluate(ref payload, ref cmd));
            Assert.Equal(0L, payload.LastCommandTimestamp);
        }

        [Fact]
        public void Evaluate_RateValidatorBeforeAntiCheatPayload()
        {
            // Rate-limited (a command 0ms ago) AND carrying a challenge
            // payload: ValidateCommand runs first, so Terminate, not ShadowBan.
            var payload = new TickStatePayload { PlayerId = 1L, LastCommandTimestamp = Environment.TickCount64 };
            var cmd = new ClientCommandPacket { Command = CommandType.ChangeActivity, ChallengeId = 1 };

            Assert.Equal(CommandGateVerdict.Terminate, CommandGate.Evaluate(ref payload, ref cmd));
        }

        [Fact]
        public void Evaluate_EpochBeforeAntiCheatPayload()
        {
            var payload = new TickStatePayload { PlayerId = 1L, LogicEpochCounter = 500L };
            var cmd = new ClientCommandPacket { Command = CommandType.ReportUiContextSwitch, LogicEpochCounter = 0L, ChallengeId = 1 };

            Assert.Equal(CommandGateVerdict.Terminate, CommandGate.Evaluate(ref payload, ref cmd));
        }

        [Fact]
        public void Evaluate_AntiCheatPayloadBeforePushCompliance()
        {
            var payload = new TickStatePayload { PlayerId = 1L };
            var cmd = new ClientCommandPacket { Command = CommandType.ReportUiContextSwitch, ChallengeId = 1, TargetLanguageId = 1 };

            Assert.Equal(CommandGateVerdict.ShadowBan, CommandGate.Evaluate(ref payload, ref cmd));
        }

        [Fact]
        public void Evaluate_PushCompliancePayload_Terminates()
        {
            var payload = new TickStatePayload { PlayerId = 1L };
            var cmd = new ClientCommandPacket { Command = CommandType.ReportUiContextSwitch, TargetLanguageId = 1 };

            Assert.Equal(CommandGateVerdict.Terminate, CommandGate.Evaluate(ref payload, ref cmd));
        }
    }
}

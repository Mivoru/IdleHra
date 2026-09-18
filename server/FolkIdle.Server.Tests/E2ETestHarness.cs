using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The one real recipe in this repo for a headless server driven over
    /// real WebSocket protocol traffic against a real Postgres. Extracted out
    /// of E2EGameLoopTest, which had it twice, so a third copy (the
    /// sustained-load scenario, docs/TASK_BOARD.md #22) does not become a
    /// third source of truth for "how do you wire nineteen engines together".
    /// </summary>
    internal static class E2ETestHarness
    {
        // Modul: Phase 5, Part 1 (E2EGameLoopTest's own history) - active
        // polling instead of a fixed wall-clock delay, because a starved
        // test host can make the tick loop itself fall behind real time.
        // Moved verbatim; behavior is unchanged.
        internal static async Task<bool> WaitForConditionAsync(Func<bool> condition, int timeoutMs, int pollIntervalMs = 100)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(pollIntervalMs);
            }
            return condition();
        }

        internal static string MintTestJwt(Guid accountId)
        {
            return AuthenticationEngine.GenerateJwt(accountId, AuthenticationEngine.GenerateSessionNonce(), "pw", AuthenticationDefaults.LocalDevelopmentFallback, out _);
        }

        // Mirrors WebSocketClient.SendAuthHandshakeAsync's fixed-buffer write
        // pattern - MemoryMarshal.Write needs the JwtToken bytes already
        // placed inside the struct's fixed buffer before it can blit the
        // whole AuthHandshakePacket into a wire-ready byte array.
        internal static unsafe byte[] BuildAuthHandshakeBuffer(string jwt)
        {
            byte[] jwtBytes = System.Text.Encoding.UTF8.GetBytes(jwt);
            var packet = new AuthHandshakePacket
            {
                JwtTokenLength = (ushort)jwtBytes.Length,
                AssetHash = 0,
                PlatformSignature = 0
            };

            byte* target = packet.JwtToken;
            for (int i = 0; i < AuthHandshakePacket.JwtTokenCapacity; i++)
            {
                target[i] = i < jwtBytes.Length ? jwtBytes[i] : (byte)0;
            }

            byte[] buffer = new byte[Marshal.SizeOf<AuthHandshakePacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            return buffer;
        }

        /// <summary>
        /// Every engine SimulationEngine's constructor takes, fully wired,
        /// plus the PlayerSessionRegistry and NetworkBroadcastSystem they
        /// share, plus a constructed-but-not-started CombatLootEngine -
        /// production starts that one with its OWN StartCron(), independent
        /// of SimulationEngine, and neither pre-existing E2E test has ever
        /// started it, which means neither has ever actually observed a
        /// granted equipment row end to end.
        /// </summary>
        internal readonly struct EngineGraph
        {
            public NetworkBroadcastSystem NetworkSystem { get; init; }
            public SimulationEngine SimulationEngine { get; init; }
            public CombatLootEngine LootEngine { get; init; }
            public PlayerSessionRegistry PlayerRegistry { get; init; }
        }

        internal static EngineGraph BuildEngineGraph(
            IServiceProvider serviceProvider,
            IDbContextFactory<FolkIdleDbContext> contextFactory,
            RetryingDbContextOptions retryingDbOptions,
            string listenPrefix)
        {
            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, listenPrefix);
            var lootEngine = new LootTableEngine();
            var checkpointManager = new StateCheckpointManager(serviceProvider);
            var forgeEngine = new ForgeSplicingEngine(serviceProvider);
            var playerRegistry = new PlayerSessionRegistry();
            var marketEngine = new MarketOrderBookEngine(serviceProvider, playerRegistry);
            var guildEngine = new GuildContributionEngine(serviceProvider);
            var escrowEngine = new MarketEscrowEngine(serviceProvider, playerRegistry);
            var mailboxEngine = new MailboxAndBankEngine(serviceProvider, playerRegistry);
            var rerollEngine = new AffixRerollEngine(serviceProvider);
            var breedingEngine = new BreedingEngine(serviceProvider, playerRegistry);
            var guildLogisticsEngine = new GuildLogisticsEngine(serviceProvider, playerRegistry);
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry, retryingDbOptions);
            var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);
            var lootWorker = new CombatLootEngine(serviceProvider, playerRegistry);

            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageManagementEngine, guildWarEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);

            return new EngineGraph
            {
                NetworkSystem = networkSystem,
                SimulationEngine = simulationEngine,
                LootEngine = lootWorker,
                PlayerRegistry = playerRegistry
            };
        }
    }
}

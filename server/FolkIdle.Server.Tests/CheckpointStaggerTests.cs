using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 43, 2c: a reconnect wave must not put every player on the same
    /// checkpoint tick.
    /// </summary>
    [Collection("Postgres collection")]
    public class CheckpointStaggerTests
    {
        private readonly PostgresTestFixture _fixture;

        public CheckpointStaggerTests(PostgresTestFixture fixture) => _fixture = fixture;

        [Fact]
        public void AHundredPlayersAddedOnOneTickReachTheBoundaryOnMoreThanFiftyTicks()
        {
            var serviceProvider = _fixture.ServiceProvider;
            var playerRegistry = new PlayerSessionRegistry();
            var contextFactory = _fixture.DbContextFactory;
            // Never Start()-ed: the engine is only a place to add players to.
            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8099/");
            var engine = new SimulationEngine(
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

            // Two shapes of id: a consecutive run (a fresh cohort) and a
            // spread of large, unrelated ids (the live population).
            var ids = Enumerable.Range(0, 50).Select(i => 984000000L + i)
                .Concat(Enumerable.Range(0, 50).Select(i => 984100000L + i * 7919L))
                .ToArray();

            var boundaryTicks = new HashSet<int>();
            foreach (long id in ids)
            {
                engine.InjectVirtualPlayer(new TickStatePayload { PlayerId = id });
                int start = engine.GetActivePlayerTicksSinceLastFlush(id);
                Assert.InRange(start, 0, StateCheckpointManager.CheckpointBoundaryTicks - 1);
                boundaryTicks.Add(StateCheckpointManager.CheckpointBoundaryTicks - start);
            }

            Assert.True(boundaryTicks.Count > 50, $"only {boundaryTicks.Count} distinct boundary ticks for 100 players");

            // A payload that arrives with its counter already set keeps it.
            engine.InjectVirtualPlayer(new TickStatePayload { PlayerId = 984999999L, TicksSinceLastFlush = StateCheckpointManager.CheckpointBoundaryTicks });
            Assert.Equal(StateCheckpointManager.CheckpointBoundaryTicks, engine.GetActivePlayerTicksSinceLastFlush(984999999L));
        }
    }
}

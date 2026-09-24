using System;
using System.Linq;
using System.Text;
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
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A world boss strike, the way a PHONE sends it.
    ///
    /// Modul: TASK 25, "NO ATTACK HAS EVER LANDED". Every world boss test before
    /// these called `WorldBossEngine.ExecuteAttackAsync` directly, so the path a
    /// real client takes - the JSON codec, the command gate, the dispatch table,
    /// the coordinator's validator and the fire-and-forget `QueueAttack` - had
    /// no test at all, and `WorldBossArmourTests` stayed green through a report
    /// that nobody could strike. These decode the exact JSON the browser sends
    /// and drive the real EngineLoop.
    ///
    /// And every refusal on that path now has to SAY something. Five of them
    /// used to be a silent rollback and two were a disconnect for an honest
    /// client (a window that closed a moment ago, a double-tap).
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossClientPathTests
    {
        private readonly PostgresTestFixture _fixture;

        public WorldBossClientPathTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private const long Epoch = 7L;

        private (SimulationEngine Engine, WorldBossEngine Boss) CreateEngine()
        {
            var serviceProvider = _fixture.ServiceProvider;
            var playerRegistry = _fixture.PlayerRegistry;
            var contextFactory = _fixture.DbContextFactory;
            var boss = new WorldBossEngine(serviceProvider, playerRegistry);

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8098/");
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
                boss,
                new VillageManagementEngine(serviceProvider, playerRegistry),
                new GuildWarEngine(serviceProvider),
                new LegacyStoreEngine(serviceProvider, playerRegistry),
                new GuildLogisticsDepotEngine(serviceProvider, playerRegistry),
                new GuildCombatSimulationEngine(serviceProvider, playerRegistry),
                null!, null!, null!, null!, null!, contextFactory);
            return (engine, boss);
        }

        // Suspended so the tick never runs combat for it - only commands and
        // the notification drains touch it. Larder EMPTY on purpose: the
        // owner dropped the larder rule on 2026-09-24, so a new player with
        // no food must be able to strike.
        private static TickStatePayload Striker(long playerId) => new()
        {
            PlayerId = playerId,
            LogicEpochCounter = Epoch,
            IsSuspended = true,
            InventorySpaceRemaining = 1000,
            CachedEffectiveMilliAttack = 5_000_000,
        };

        /// <summary>Exactly what `connection.send` puts on the socket for `attackWorldBoss`.</summary>
        private static ClientCommandPacket BrowserStrike(int plate, uint predictedDamage = 0)
        {
            string extra = predictedDamage == 0 ? string.Empty : $",\"ClientPredictedDamage\":{predictedDamage}";
            string json = $"{{\"{PacketJsonCodec.TypePropertyName}\":\"{PacketJsonCodec.TypeClientCommand}\"," +
                          $"\"LogicEpochCounter\":{Epoch},\"Command\":{(int)CommandType.AttackWorldBoss}," +
                          $"\"TargetedBossId\":1,\"TargetedPlateIndex\":{plate}{extra}}}";
            Assert.True(PacketJsonCodec.TryDeserialize(Encoding.UTF8.GetBytes(json), out ClientCommandPacket cmd, out var err), err);
            return cmd;
        }

        private async Task ClearAttemptsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"player_world_boss_attempts\" WHERE \"PlayerId\" = {0}", playerId);
        }

        private async Task<PlayerWorldBossAttempt?> AttemptAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerWorldBossAttempts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.PlayerId == playerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
        }

        private async Task<WorldBossSnapshot> SnapshotAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.WorldBossSnapshots.AsNoTracking()
                .SingleAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
        }

        private static bool HasResult(SimulationEngine engine, long playerId, CommandResultCode code)
            => engine.GetActivePlayerCommandResultSlots(playerId).Any(s => s.tick > 0 && s.code == (byte)code);

        private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
            => await E2ETestHarness.WaitForConditionAsync(condition, timeoutMs);

        [Fact]
        public async Task TheBrowsersStrike_LandsARow_MovesTheHp_AndKeepsTheSession()
        {
            const long playerId = 970_025_001L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            long hpBefore = (await SnapshotAsync()).CurrentHp;

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 3));

                Assert.True(await WaitAsync(() => AttemptAsync(playerId).GetAwaiter().GetResult() is { AttemptCount: 1 }),
                    "The browser's strike never produced an attempt row.");
                Assert.True((await SnapshotAsync()).CurrentHp < hpBefore, "The strike wrote a row but the boss did not lose health.");
                Assert.True(await WaitAsync(() => engine.GetActivePlayerWorldBossAttemptCount(playerId) == 1),
                    "The attempt never reached the player's packet.");
                Assert.True(engine.IsActivePlayerPresent(playerId), "A legal strike ended the session.");
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        [Fact]
        public async Task ADoubleTap_IsTwoStrikes_NotADisconnect()
        {
            const long playerId = 970_025_002L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));

                // Both land inside one tick - well under the 100 ms rule that
                // used to terminate the second.
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 1));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 1));

                Assert.True(await WaitAsync(() => AttemptAsync(playerId).GetAwaiter().GetResult() is { AttemptCount: 2 }),
                    "A double-tap did not produce two strikes.");
                Assert.True(engine.IsActivePlayerPresent(playerId), "A double-tap disconnected the player.");
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        [Fact]
        public async Task AStrikeOutsideTheWindow_SaysSo_AndDoesNotDisconnect()
        {
            const long playerId = 970_025_003L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            await boss.CloseManualWindowAsync();
            Assert.False(boss.IsEventActive);

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 0));

                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossNotActive)),
                    "A strike on a closed window was not answered with WorldBossNotActive.");
                Assert.True(engine.IsActivePlayerPresent(playerId));
                Assert.Null(await AttemptAsync(playerId));
            }
            finally
            {
                engine.Stop();
            }
        }

        [Fact]
        public async Task AStrikeOnADeadBoss_SaysSo_AndDoesNotDisconnect()
        {
            const long playerId = 970_025_004L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"WorldBossSnapshots\" SET \"CurrentHp\" = 0 WHERE \"BossInstanceId\" = {0}", (long)WorldBossEngine.ActiveBossInstanceId);
            }
            await boss.EnsureSnapshotAsync();
            Assert.True(boss.IsBossDead());

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 0));

                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossAlreadyDefeated)),
                    "A strike on a dead boss was not answered with WorldBossAlreadyDefeated.");
                Assert.True(engine.IsActivePlayerPresent(playerId));
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        [Fact]
        public async Task AFourthStrike_SaysTheAttemptsAreSpent()
        {
            const long playerId = 970_025_005L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(WorldBossAttackOutcome.Landed,
                    await boss.ExecuteAttackAsync(playerId, WorldBossEngine.ActiveBossInstanceId, 10, 0));
            }

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 0));

                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossNoAttemptsLeft)),
                    "A fourth strike was not answered with WorldBossNoAttemptsLeft.");
                Assert.True(engine.IsActivePlayerPresent(playerId));
                Assert.Equal(3, (await AttemptAsync(playerId))!.AttemptCount);
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        [Fact]
        public async Task ASpentBudget_IsRefusedInMemory_WithoutOpeningATransaction()
        {
            // Opcode 32 is outside the 100 ms rule, so spam after the third
            // strike must not reach ExecuteAttackAsync: the payload says the
            // budget is spent, and no attempt row may appear for this player.
            const long playerId = 970_025_009L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);

            try
            {
                engine.Start();
                var spent = Striker(playerId);
                spent.WorldBossAttemptCount = (byte)WorldBossEngine.MaxAttemptsPerEncounter;
                engine.InjectVirtualPlayer(spent);
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 0));

                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossNoAttemptsLeft)),
                    "A strike with a spent budget was not answered with WorldBossNoAttemptsLeft.");
                Assert.True(engine.IsActivePlayerPresent(playerId));
                Assert.Null(await AttemptAsync(playerId));
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        [Theory]
        [InlineData(null, null, false)]
        [InlineData("0", "Development", false)]
        [InlineData("1", null, true)]
        [InlineData("1", "Development", true)]
        [InlineData("1", "Production", false)]
        [InlineData("1", "production", false)]
        public void DevTools_AreClosedUnlessFlagged_AndAlwaysClosedInProduction(string? flag, string? env, bool expected)
        {
            // The dev window route can wipe every player's attempts; a stray
            // flag in the box's unversioned .env must not open it.
            Assert.Equal(expected, FolkIdle.Server.Network.NetworkBroadcastSystem.DevToolsEnabled(flag, env));
        }

        [Fact]
        public async Task AStrikeAfterTheSessionClosed_SaysSo()
        {
            const long playerId = 970_025_006L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            Assert.Equal(WorldBossAttackOutcome.Landed,
                await boss.ExecuteAttackAsync(playerId, WorldBossEngine.ActiveBossInstanceId, 10, 0));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"player_world_boss_attempts\" SET \"SessionStartEpoch\" = \"SessionStartEpoch\" - {0} WHERE \"PlayerId\" = {1}",
                    WorldBossEngine.BattleSessionCapSeconds + 1, playerId);
            }

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 0));

                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossSessionClosed)),
                    "A strike after the battle session closed was not answered with WorldBossSessionClosed.");
                Assert.True(engine.IsActivePlayerPresent(playerId));
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        [Fact]
        public async Task AStrikeThatThrows_SaysItFailed_InsteadOfVanishing()
        {
            // The scope, the connection and BeginTransaction used to sit
            // OUTSIDE the try, inside a bare Task.Run: a refused connection
            // became an unobserved task exception - no row, no log, no message.
            const long playerId = 970_025_007L;
            var registry = new PlayerSessionRegistry();
            var boss = new WorldBossEngine(new ThrowingServiceProvider(), registry);

            boss.QueueAttack(playerId, WorldBossEngine.ActiveBossInstanceId, 100, 0);

            CommandResultNotification seen = default;
            Assert.True(await WaitAsync(() => registry.CommandResultQueue.TryDequeue(out seen)),
                "A strike whose database work threw reported nothing at all.");
            Assert.Equal(playerId, seen.PlayerId);
            Assert.Equal((byte)CommandResultCode.WorldBossStrikeFailed, seen.ResultCode);
        }

        [Fact]
        public async Task AProtocolViolation_StillTerminates()
        {
            // The softening is for STATE races an honest client can hit. A
            // client that posts its own damage figure is not honest or is
            // older than 2026-09-05, and either way is not spoken to.
            const long playerId = 970_025_008L;
            var (engine, boss) = CreateEngine();
            await boss.OpenManualWindowAsync(900);

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 0, predictedDamage: 5000));

                Assert.True(await WaitAsync(() => !engine.IsActivePlayerPresent(playerId)),
                    "A strike carrying ClientPredictedDamage was not terminated.");
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        private sealed class ThrowingServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                // The Redis lookup in the constructor is optional; everything
                // the attack needs throws, the way a refused pool does.
                if (serviceType == typeof(StackExchange.Redis.IConnectionMultiplexer)) return null;
                throw new InvalidOperationException("EMAXCONNSESSION (simulated)");
            }
        }
    }
}

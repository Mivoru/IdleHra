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

        private (SimulationEngine Engine, WorldBossEngine Boss) CreateEngine(
            FolkIdle.Server.Domain.Combat.WorldBossStrike.BossMinigameMode mode = FolkIdle.Server.Domain.Combat.WorldBossStrike.BossMinigameMode.Off)
        {
            var serviceProvider = _fixture.ServiceProvider;
            var playerRegistry = _fixture.PlayerRegistry;
            var contextFactory = _fixture.DbContextFactory;
            var boss = new WorldBossEngine(serviceProvider, playerRegistry, mode);

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

        // Task 36 Phase 2: with the wheel on, the old plate buttons are a stale
        // bundle. Answered with "update the app" - no row, no disconnect.
        [Fact]
        public async Task UnderTheWheel_Opcode32_SaysUpdate_SpendsNothing_AndKeepsTheSession()
        {
            const long playerId = 970_025_011L;
            var (engine, boss) = CreateEngine(FolkIdle.Server.Domain.Combat.WorldBossStrike.BossMinigameMode.Wheel);
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            long hpBefore = (await SnapshotAsync()).CurrentHp;

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 2));

                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossUpdateRequired)),
                    "Opcode 32 under the wheel was not answered with WorldBossUpdateRequired.");
                Assert.Null(await AttemptAsync(playerId));
                Assert.Equal(hpBefore, (await SnapshotAsync()).CurrentHp);
                Assert.True(engine.IsActivePlayerPresent(playerId), "An old client's strike ended the session.");
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        // Task 36 Phase 2, spec 5.7: the REST order goes through the TICK, which
        // prices it with A x G from the striker's own payload - never from
        // anything the client sent - and a striker with no payload at the floor.
        [Fact]
        public async Task AShieldWheelOrder_IsPricedFromThePayload_OnTheTick()
        {
            const long playerId = 970_025_012L;
            const long absentPlayerId = 970_025_013L;
            var (engine, boss) = CreateEngine(FolkIdle.Server.Domain.Combat.WorldBossStrike.BossMinigameMode.Wheel);
            await ClearAttemptsAsync(playerId);
            await ClearAttemptsAsync(absentPlayerId);
            await boss.OpenManualWindowAsync(900);
            long hpBefore = (await SnapshotAsync()).CurrentHp;

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));

                var order = new FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeOrder
                {
                    PlayerId = playerId,
                    Landings = new[] { new FolkIdle.Server.Domain.Combat.WorldBossStrike.SpearLanding(0, 1, FolkIdle.Server.Domain.Combat.WorldBossStrike.SpearClass.Seam, true) },
                    WeakPlate = 1,
                    Multiplier = 1.5,
                    LandedAs = FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeResult.Landed,
                };
                boss.Submit(order);
                var outcome = await order.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

                // Striker's CachedEffectiveMilliAttack is 5,000,000: A = 5,000,
                // no Giantslayer. 1.5 x 3.0 = 4.5.
                Assert.Equal(FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeResult.Landed, outcome.Result);
                Assert.Equal(22_500, outcome.Damage);
                Assert.Equal(hpBefore - 22_500, (await SnapshotAsync()).CurrentHp);
                Assert.True(await WaitAsync(() => engine.GetActivePlayerWorldBossAttemptCount(playerId) == 1),
                    "The wheel strike never reached the player's packet.");

                // No game session, nothing to price the blow with: Failed, nothing spent.
                var orphan = new FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeOrder
                {
                    PlayerId = absentPlayerId,
                    Landings = Array.Empty<FolkIdle.Server.Domain.Combat.WorldBossStrike.SpearLanding>(),
                    WeakPlate = 0,
                    Multiplier = 1.0,
                    LandedAs = FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeResult.Landed,
                };
                // Security review, 2026-09-26: no game session is priced at the
                // 1,000 floor and SPENT - never "nothing spent", which let a
                // script discard a run after reading its throws.
                boss.Submit(orphan);
                var orphanOutcome = await orphan.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeResult.Landed, orphanOutcome.Result);
                Assert.Equal(WorldBossEngine.MinStrikeDamage, orphanOutcome.Damage);
                Assert.Equal(1, (await AttemptAsync(absentPlayerId))!.AttemptCount);
            }
            finally
            {
                engine.Stop();
                await boss.CloseManualWindowAsync();
            }
        }

        [Fact]
        public async Task ADoubleTap_IsOneStrikeAndAnAnswer_NotADisconnect()
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
                // used to terminate the second. With one strike a day
                // (2026-09-25) the first lands and the second is ANSWERED, never
                // punished: the property this test guards is still "a double-tap
                // is not a disconnect".
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 1));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 1));

                Assert.True(await WaitAsync(() => AttemptAsync(playerId).GetAwaiter().GetResult() is { AttemptCount: 1 }),
                    "A double-tap did not land its first strike.");
                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossNoAttemptsLeft)),
                    "The second tap of a double-tap was not answered with WorldBossNoAttemptsLeft. Results seen: "
                    + string.Join(",", engine.GetActivePlayerCommandResultSlots(playerId).Where(s => s.tick > 0).Select(s => s.code)));
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
        public async Task ASecondStrikeTheSameDay_SaysTheStrikeIsSpent()
        {
            const long playerId = 970_025_005L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            Assert.Equal(WorldBossAttackOutcome.Landed,
                await boss.ExecuteAttackAsync(playerId, WorldBossEngine.ActiveBossInstanceId, 10, 0));

            try
            {
                engine.Start();
                engine.InjectVirtualPlayer(Striker(playerId));
                engine.InjectBenchmarkCommand(playerId, BrowserStrike(plate: 0));

                Assert.True(await WaitAsync(() => HasResult(engine, playerId, CommandResultCode.WorldBossNoAttemptsLeft)),
                    "A second strike the same day was not answered with WorldBossNoAttemptsLeft.");
                Assert.True(engine.IsActivePlayerPresent(playerId));
                Assert.Equal(WorldBossEngine.MaxAttemptsPerDay, (await AttemptAsync(playerId))!.AttemptCount);
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
            // strike of the day must not reach ExecuteAttackAsync: the payload says the
            // budget is spent, and no attempt row may appear for this player.
            const long playerId = 970_025_009L;
            var (engine, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);

            try
            {
                engine.Start();
                var spent = Striker(playerId);
                spent.WorldBossAttemptCount = (byte)WorldBossEngine.MaxAttemptsPerDay;
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

        // Modul: ONE STRIKE A DAY (owner, 2026-09-25) replaced "three per
        // encounter inside a 300-second session". The count belongs to a UTC
        // day: a second strike the same day is answered NoAttemptsLeft, and
        // the same row the next day starts again at 0.
        [Fact]
        public async Task OneStrikeADay_TheSecondIsRefused_AndTomorrowRefills()
        {
            const long playerId = 970_025_006L;
            var (_, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            try
            {
                Assert.Equal(WorldBossAttackOutcome.Landed,
                    await boss.ExecuteAttackAsync(playerId, WorldBossEngine.ActiveBossInstanceId, 10, 0));
                Assert.Equal(WorldBossAttackOutcome.NoAttemptsLeft,
                    await boss.ExecuteAttackAsync(playerId, WorldBossEngine.ActiveBossInstanceId, 10, 0));

                // Yesterday's strike, as far as the row knows.
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"player_world_boss_attempts\" SET \"AttemptDateKey\" = \"AttemptDateKey\" - 1 WHERE \"PlayerId\" = {0}", playerId);
                }

                Assert.Equal(WorldBossAttackOutcome.Landed,
                    await boss.ExecuteAttackAsync(playerId, WorldBossEngine.ActiveBossInstanceId, 10, 0));
                var row = await AttemptAsync(playerId);
                Assert.NotNull(row);
                Assert.Equal(1, row!.AttemptCount);
                Assert.Equal(WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), row.AttemptDateKey);
            }
            finally
            {
                await boss.CloseManualWindowAsync();
            }
        }

        [Fact]
        public async Task ThereIsNoBattleSessionAnyMore()
        {
            // An attempt row whose "session" began long ago used to be refused
            // with SessionClosed. Nothing reads SessionStartEpoch now.
            const long playerId = 970_025_010L;
            var (_, boss) = CreateEngine();
            await ClearAttemptsAsync(playerId);
            await boss.OpenManualWindowAsync(900);
            try
            {
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    db.PlayerWorldBossAttempts.Add(new FolkIdle.Server.Models.PlayerWorldBossAttempt
                    {
                        PlayerId = playerId,
                        BossInstanceId = WorldBossEngine.ActiveBossInstanceId,
                        AttemptCount = 0,
                        SessionStartEpoch = DateTimeOffset.UtcNow.AddHours(-5).ToUnixTimeSeconds(),
                        AttemptDateKey = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    });
                    await db.SaveChangesAsync();
                }

                Assert.Equal(WorldBossAttackOutcome.Landed,
                    await boss.ExecuteAttackAsync(playerId, WorldBossEngine.ActiveBossInstanceId, 10, 0));
            }
            finally
            {
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
                if (serviceType == typeof(FolkIdle.Server.Domain.Combat.WorldBossStrike.BossMinigameSettings)) return null;
                throw new InvalidOperationException("EMAXCONNSESSION (simulated)");
            }
        }
    }
}

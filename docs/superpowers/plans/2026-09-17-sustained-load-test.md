# Sustained-load test combining combat, gathering, checkpoints, market and reconnects

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write the test the task board calls "confirmed missing" — a scenario
that spins up N concurrent simulated players who combat, gather, force
checkpoints, list market orders and reconnect on overlapping schedules,
against a connection pool deliberately bounded far below N, and asserts the
three symptoms measured once for real during the loot-starvation incident:
no dropped loot, no starved queue, checkpoint age stays bounded. This is the
test that would have caught `CombatLootEngine`'s worker death and the
gathering-starvation follow-up before a player did.

**Architecture:** `E2EGameLoopTest.cs` already has the only working recipe in
this repo for a real, in-process, real-Postgres server driven over real
WebSocket protocol traffic: build a `ServiceProvider` around a Testcontainers
Postgres, wire all ~19 domain engines into one `SimulationEngine`, start
`NetworkBroadcastSystem` + `SimulationEngine`, connect `ClientWebSocket`s,
send the real `AuthHandshakePacket`/`ClientCommandPacket` wire structs, and
read back real `StateUpdatePacket`s. `StressTestConcurrentMultiplexing`
already proves that recipe scales to 500 concurrent sockets. This plan does
not reinvent that — it (1) extracts the ~60 lines of engine-graph bootstrap
duplicated between `E2EGameLoopTest`'s own two tests into a shared internal
helper, adding the one engine that recipe has always been missing
(`CombatLootEngine` — production starts it as its own `StartCron()`
independent of `SimulationEngine`, and neither existing E2E test starts it at
all, which is why neither has ever actually observed a granted item), and (2)
uses that helper from a new sibling test that points the whole graph at a
connection string bounded to a handful of pooled connections via
`ConnectionStringDefaults.WithBoundedPool` — the exact function
`ConnectionStringDefaults.cs`'s own doc comment says exists because Npgsql's
100-connection default let one `EMAXCONNSESSION` throw kill the loot worker
for the life of the process.

**Tech Stack:** C# / .NET 8, xUnit + Testcontainers (a real Postgres 16
container per test class, matching `E2EGameLoopTest`'s own pattern — not
`PostgresTestFixture`, whose shared seeded database and shared
`PlayerSessionRegistry` are wrong for a test that wants to control its own
pool size and its own player set from an empty database).

**Spec:** `docs/TASK_BOARD.md`, section "## 22. No sustained-load test
combining the systems that actually interact in production" is the authority
this plan argues from. `CLAUDE.md`'s "A background worker that can throw is a
feature that can vanish" and "An unbounded drain in a worker loop is a
starvation bug" describe the two incidents this test exists to have caught;
its connection-pool-bounding note points at `ConnectionStringDefaults
.WithBoundedPool`, confirmed present and unit-tested (`ConnectionPoolBoundTests.cs`)
during this plan's research.

## Global Constraints

- **Reuse the harness, do not rebuild it.** Every engine construction line in
  the new test must come from the Task 1 helper, not a third copy-paste of
  the ~19-engine block. If a new engine is added to the graph in the future,
  fixing it in one place (the helper) should be enough for all three tests
  (`Test_E2E_ClosedLoopVerification`, `StressTestConcurrentMultiplexing`, and
  this plan's new sustained-load test) to pick it up.
- **Task 1 must not change the observable behavior of either existing
  `E2EGameLoopTest` test.** It is a pure extraction. Run both existing tests
  before and after and confirm they still pass — this is the plan's only
  correctness check for that task, mirroring how the audit-fixes plan treated
  a breaking signature change (run the full suite once to prove nothing else
  was missed).
- **The bounded pool must be the SAME connection string used for both the
  plain `IDbContextFactory` and the `RetryingDbContextOptions`.** This is
  what `Program.cs` does in production (`connectionString =
  ConnectionStringDefaults.WithBoundedPool(...)` computed once, then handed
  to both `AddDbContextFactory` and the retry-configured `DbContextOptionsBuilder`
  a few lines later) — pointing only one of the two at a bounded string would
  not recreate the incident shape, it would just move where the unbounded
  path absorbs the load.
- **Do not register a `RedisSessionCache`/`IConnectionMultiplexer` in this
  test's `ServiceProvider`.** `E2EGameLoopTest`'s existing tests already omit
  Redis, and `StateCheckpointManager.TrackState` reads that absence directly:
  with `_redisSessionCache == null`, `redisUnavailable` is unconditionally
  true, so any payload with a nonzero `RedisPendingGoldDelta` (which is how
  combat/auto-salvage gold arrives — see CLAUDE.md, "Two gold paths") forces
  a full `FlushState` on its very next `TrackState` call. That is a real,
  already-built-in, high-frequency checkpoint-forcing mechanism this plan
  relies on rather than inventing a new one — adding Redis back would remove
  it (gold would take the frame instead and only checkpoint every five
  minutes), which is the opposite of what a *sustained-load* test wants.
- **Every simulated session is its own `PlayerRecord` row and its own
  `ClientWebSocket`.** No test in this plan shares the collection-wide
  `PostgresTestFixture`/its shared `PlayerSessionRegistry` — matching the
  gold-sync and audit-fixes plans' established convention, and doubly true
  here since this test's entire point is to own its connection string.
- **CI budget.** Target under 4 minutes of wall-clock for the new test
  (`E2EGameLoopTest`'s own combat-resolve wait already budgets up to 90s for
  a single kill under CPU pressure; this test runs N sessions concurrently,
  not serially, so it should not need N times that). If the traffic-generation
  window plus the drain-timeout safety net together exceed that, shrink N or
  the window before shrinking the pool bound — the pool bound is the thing
  under test.

---

### Task 1: Extract the shared E2E harness (engine graph + auth helpers)

**Files:**
- New: `server/FolkIdle.Server.Tests/E2ETestHarness.cs`
- Modify: `server/FolkIdle.Server.Tests/E2EGameLoopTest.cs` — remove the three
  private static helpers (`WaitForConditionAsync` line 84,  `MintTestJwt` line
  95, `BuildAuthHandshakeBuffer` line 104) and the duplicated engine-graph
  block in both `Test_E2E_ClosedLoopVerification` (lines 162–197) and
  `StressTestConcurrentMultiplexing` (lines 478–502); call the new helper
  instead. Line numbers are from this plan's research pass — confirm against
  the live file before editing, they will have drifted if Task 1 of any other
  in-flight plan lands first.

**Interfaces:**
- Produces: `E2ETestHarness.WaitForConditionAsync`, `E2ETestHarness.MintTestJwt`,
  `E2ETestHarness.BuildAuthHandshakeBuffer` — same signatures as the methods
  they replace, `internal static` so both test classes in this assembly can
  call them.
- Produces: `E2ETestHarness.BuildEngineGraph(IServiceProvider serviceProvider,
  IDbContextFactory<FolkIdleDbContext> contextFactory, RetryingDbContextOptions
  retryingDbOptions, string listenPrefix) -> E2ETestHarness.EngineGraph` — a
  record/struct holding every engine + the `SimulationEngine` + the
  `PlayerSessionRegistry`, fully constructed and wired exactly as the two
  existing tests do it today, PLUS a constructed (not started)
  `CombatLootEngine`, which neither existing test builds at all. The caller
  still owns `.Start()`/`.StartCron()` and `GlobalEngineState
  .IsColdBootRecoveryComplete` — this helper only removes the duplication in
  *construction*, not lifecycle, so each test keeps deciding what to start and
  when (Task 2's sustained-load test is the first caller that needs
  `CombatLootEngine.StartCron()`; the two existing tests must keep NOT calling
  it, since that is a lifecycle change this task must not make for them).

- [ ] **Step 1: Write `E2ETestHarness.cs`**

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// real WebSocket protocol traffic against a real Postgres. Extracted
    /// out of E2EGameLoopTest, which had it twice, so a third copy (the
    /// sustained-load scenario) does not become a third source of truth for
    /// "how do you wire nineteen engines together".
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
            return AuthenticationEngine.GenerateJwt(accountId, AuthenticationEngine.GenerateSessionNonce(), AuthenticationDefaults.LocalDevelopmentFallback, out _);
        }

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
```

Confirm the `SimulationEngine` constructor's parameter list and the
`AntiCheatTelemetryEngine`/push/compliance/billing `null!` positions against
the live file before pasting — `Test_E2E_ClosedLoopVerification` wires a real
`AntiCheatTelemetryEngine` (because its receive loop answers the anti-cheat
challenge) while `StressTestConcurrentMultiplexing` passes `null!` for it too.
`BuildEngineGraph` above matches the `StressTestConcurrentMultiplexing` shape
(no anti-cheat engine) since neither existing call site needs the
challenge-response dance changed — if `Test_E2E_ClosedLoopVerification`'s
call site still needs its own real `AntiCheatTelemetryEngine` wired in after
the helper returns, that engine has a `RegisterAntiCheatTelemetryEngine`
setter on `NetworkBroadcastSystem` it can still call, same as today.

- [ ] **Step 2: Rewire `E2EGameLoopTest.cs` to use the helper**

Replace the private static helpers and both duplicated engine-graph blocks
with calls to `E2ETestHarness.WaitForConditionAsync`,
`E2ETestHarness.MintTestJwt`, `E2ETestHarness.BuildAuthHandshakeBuffer`, and
`E2ETestHarness.BuildEngineGraph(...)` followed by
`networkSystem.RegisterAntiCheatTelemetryEngine(antiCheatTelemetryEngine)`
where `Test_E2E_ClosedLoopVerification` needs it. Do not change anything
about what either test asserts, sends, or waits for — only how the engine
graph is constructed.

- [ ] **Step 3: Run both existing E2E tests and confirm no behavior changed**

Stop the running server first (CLAUDE.md's stale-build rule). Run:

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~E2EGameLoopTest"`

Expected: `Test_E2E_ClosedLoopVerification`, `StressTestConcurrentMultiplexing`,
and `Test_E2E_MarketBrowser_PaginatedListingsAndBoundsValidation` all still
pass, with the same log shape they had before (level-up observed, throttler
drops observed, stress-test terminated/kept-open counts printed).

- [ ] **Step 4: Commit**

```bash
git add server/FolkIdle.Server.Tests/E2ETestHarness.cs server/FolkIdle.Server.Tests/E2EGameLoopTest.cs
git commit -m "test(e2e): extract the shared engine-graph harness out of E2EGameLoopTest"
```

---

### Task 2: The sustained-load scenario

**Files:**
- New: `server/FolkIdle.Server.Tests/SustainedLoadTests.cs`

**Interfaces:**
- Consumes: `E2ETestHarness.BuildEngineGraph`/`WaitForConditionAsync`/
  `MintTestJwt`/`BuildAuthHandshakeBuffer` from Task 1.
- Consumes: `ConnectionStringDefaults.WithBoundedPool` (unchanged;
  `server/FolkIdle.Server/Models/ConnectionStringDefaults.cs`).
- Produces: nothing another task depends on.

**Why a new sibling file, not a third test inside `E2EGameLoopTest.cs`:**
this scenario needs its OWN bounded-pool connection string, and a single file
mixing an intentionally-starved pool with the two existing unbounded-pool
tests is exactly the kind of thing that gets copy-pasted wrong later (someone
reuses the bounded string, or reuses the unbounded one, in the wrong test).
`CronWorkerGuardTests.cs`, `LootWorkerResilienceTests.cs` and
`ConnectionPoolBoundTests.cs` already establish "one file per concern" for
this exact incident family — this is the fourth.

**Chosen numbers, and why:**
- **Pool bound = 5.** Production's own floor (`ConnectionStringDefaults
  .DefaultMaxPoolSize`) is 12, chosen to fit under Supabase's real 15-client
  ceiling. This test deliberately goes tighter than production — the fix
  shape asks for the pool to be "bounded artificially low to force
  contention on purpose," and 5 guarantees that FlushState/loot-write/
  market-escrow/gathering-grant operations queue on Npgsql's own wait list
  rather than occasionally finding a free connection by luck, while staying
  high enough that a burst of simultaneous writes drains in low single-digit
  seconds rather than piling up against the retry policy's own ceiling
  (`maxRetryCount: 6`, `maxRetryDelay: 8s` — a badly-undersized pool of 1–2
  risks compounding that into a test-blowing multi-minute stall; 5 does not).
- **N = 40 concurrent sessions.** An 8x overcommit against the pool bound —
  enough that the pool is never idle for the length of the run (every
  session issues a pool-touching command roughly every few seconds; even a
  handful of sessions competing for 5 connections keeps the queue non-empty),
  while small enough that connect+handshake for all 40 completes in a couple
  of seconds (`StressTestConcurrentMultiplexing` already proves 500 raw
  socket connects is cheap — 40 is a small fraction of that, so the WebSocket
  layer is never this test's bottleneck, only the DB pool is).
- **Traffic window = 60 real seconds, drain safety net = 60 more.** Long
  enough for each of the 40 sessions to land several forced checkpoints (see
  below) and several combat kills / gathering ticks under contention; short
  enough that the whole test stays inside this plan's ~4-minute CI budget.
- **"Checkpoint age stays bounded" = every `StateUpdatePacket
  .TicksSinceLastFlush` observed for any session, at any point in the run,
  is strictly less than `StateCheckpointManager.CheckpointBoundaryTicks`
  (3000 ticks / 300 real seconds)** — the system's own documented contract
  ("how far behind the database a live payload is allowed to fall"), not an
  invented number. The test also prints the observed MAX `TicksSinceLastFlush`
  per session (CLAUDE.md: "a number a test PRINTS is not a number a test
  CHECKS" — the hard assert is against the named constant; the print is only
  for a human to see how much headroom the run actually had).

- [ ] **Step 1: The checkpoint-forcing and market-contention mechanisms this
  test relies on (read before writing code)**

Two mechanisms already in the engine force a synchronous `FlushState`/
checkpoint without waiting for the 5-minute boundary, and this test uses both
rather than inventing a third:

1. **`CommandType.SpendAttributePoint`** (`SimulationEngine.cs`, the
   `SpendAttributePoint` branch) sets `currentPayload.TicksSinceLastFlush =
   StateCheckpointManager.CheckpointBoundaryTicks` on success, which forces
   `TrackState` to checkpoint on the very next tick. Requires
   `PlayerRecords.UnspentAttributePoints` seeded well above however many
   times the schedule below spends one (seed 10000 per player; spend 1 STR
   point, `TargetId = 0, LimitPrice = 1`, per scheduled tick).
2. **`CommandType.MarketListItem`** (`SimulationEngine.cs`, the
   `MarketListItem`/`MarketBuyItem` branch) calls
   `_checkpointManager.FlushStateAndAdvance(ref currentPayload)`
   synchronously on the tick thread BEFORE dispatching the escrow write, and
   the escrow write itself (`MarketEscrowEngine.ListItemAsync`) is the
   "market contention" leg of the scenario. `ValidateMarketCommands`
   (`ClientCommandValidator.cs`) only requires `price > 0` and `targetId >
   0` — confirm those bounds against the live file, they are shallow gate
   checks, not ownership checks — so each session needs one real, owned
   `EquipmentInstance` row seeded before the run to list (seed
   `BaseItemId = "eq_steel_claymore_melee_weapon_slot_base"`, `QualityTier =
   1`, `AffixPayload = "{}"`, capture the generated `Id` to use as
   `TargetId`).

Both mechanisms run with NO `RedisSessionCache` registered (see Global
Constraints), so combat's own gold (`RedisPendingGoldDelta`, via
auto-salvage) ALSO forces a checkpoint on nearly every kill — the scenario
gets a third, un-scheduled source of checkpoint pressure for free from
fighting sessions.

- [ ] **Step 2: Write `SustainedLoadTests.cs` — setup**

```csharp
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Testcontainers.PostgreSql;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The scenario docs/TASK_BOARD.md #22 calls "confirmed missing": N
    /// concurrent sessions combat/gather/checkpoint/list-on-market/reconnect
    /// on overlapping schedules, against a connection pool bounded far below
    /// N - the same shape as the incident that killed CombatLootEngine's
    /// drain for the whole live server (CLAUDE.md, "A background worker that
    /// can throw is a feature that can vanish"; the follow-up gathering
    /// starvation, "An unbounded drain in a worker loop is a starvation
    /// bug"). Shares the "Postgres collection" purely to serialize against
    /// E2EGameLoopTest/HardenedEngineIntegrationTests for the same reason
    /// E2EGameLoopTest already documents - long-lived background engine
    /// threads and the shared static GlobalEngineState do not survive
    /// cross-class parallelism.
    /// </summary>
    [Collection("Postgres collection")]
    public class SustainedLoadTests : IAsyncLifetime
    {
        private const int PoolBound = 5;
        private const int SessionCount = 40;
        private const int TrafficWindowSeconds = 60;
        private const int DrainTimeoutSeconds = 60;

        private PostgreSqlContainer? _dbContainer;
        private bool _dockerAvailable;
        private readonly ITestOutputHelper _o;

        public SustainedLoadTests(ITestOutputHelper o) => _o = o;

        public async Task InitializeAsync()
        {
            ContentRegistry.Initialize();
            ActiveSkillEngine.Initialize();

            try
            {
                _dbContainer = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await _dbContainer.StartAsync();
                _dockerAvailable = true;
            }
            catch (DotNet.Testcontainers.Builders.DockerUnavailableException ex)
            {
                Console.WriteLine($"WARNING: Docker unavailable for sustained-load test. Details: {ex.Message}");
                _dockerAvailable = false;
            }
        }

        public async Task DisposeAsync()
        {
            GlobalEngineState.IsColdBootRecoveryComplete = false;
            if (_dbContainer != null) await _dbContainer.DisposeAsync().AsTask();
        }
    }
}
```

- [ ] **Step 3: The test method — bounded-pool wiring and player seeding**

```csharp
        [Fact]
        public async Task Test_SustainedLoad_NoDroppedLootNoStarvedQueueBoundedCheckpointAge()
        {
            if (!_dockerAvailable || _dbContainer == null)
            {
                Console.WriteLine("WARNING: Skipping sustained-load test because Docker is unavailable.");
                return;
            }

            // Modul: the SAME bounded string for both the plain factory and
            // the retry-configured options - Program.cs computes it once and
            // hands it to both for exactly this reason (see Global
            // Constraints). Using the raw container string for either would
            // give that operation an unbounded 100-connection pool and
            // defeat the whole point of the test.
            string boundedConnectionString = ConnectionStringDefaults.WithBoundedPool(
                _dbContainer.GetConnectionString(), PoolBound.ToString());

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options => options.UseNpgsql(boundedConnectionString));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            var retryOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(boundedConnectionString, npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(retryOptions));

            var serviceProvider = services.BuildServiceProvider();
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var retryingDbOptions = serviceProvider.GetRequiredService<RetryingDbContextOptions>();

            // Migrations run over the SAME bounded pool - deliberate. A
            // migration is a real connection acquire too, and production's
            // own bounded string covers it (see ConnectionStringDefaults'
            // own doc comment: the bound "leaves room for the migration and
            // admin connections that do not come from the pool" - meaning
            // room within the ceiling, not a separate unbounded channel).
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var graph = E2ETestHarness.BuildEngineGraph(serviceProvider, contextFactory, retryingDbOptions, "http://localhost:8090/");

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            graph.NetworkSystem.Start();
            graph.SimulationEngine.Start();
            graph.LootEngine.StartCron();

            // Half the sessions fight, half gather - both feed CombatLootEngine's
            // two queues (DropRequestQueue / GatheringGrantQueue), which is the
            // exact split that starved production (fighters produce loot
            // requests, gatherers produce grants at a much higher rate per
            // action - see CombatLootEngine.CoalesceGatheringGrants's own doc
            // comment on why gathering is the one that starves the worker).
            var accountIds = new Guid[SessionCount];
            var equipmentIdToList = new long[SessionCount];
            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                for (int i = 0; i < SessionCount; i++)
                {
                    accountIds[i] = Guid.NewGuid();
                    long playerId = 990_000_000L + i;
                    db.PlayerRecords.Add(new PlayerRecord
                    {
                        Id = playerId,
                        PlayerGuid = accountIds[i],
                        AuthenticatorToken = Guid.NewGuid(),
                        CurrentLevel = 0,
                        CurrentXp = 0,
                        UnspentAttributePoints = 10000
                    });

                    var listing = new EquipmentInstance
                    {
                        PlayerId = playerId,
                        BaseItemId = "eq_steel_claymore_melee_weapon_slot_base",
                        QualityTier = 1,
                        AffixPayload = "{}"
                    };
                    db.EquipmentInstances.Add(listing);
                    await db.SaveChangesAsync();
                    equipmentIdToList[i] = listing.Id;
                }
            }
```

Verify `EquipmentInstance.Id`'s exact property name and that it is populated
by `SaveChangesAsync()` (identity column) against the live model before
pasting — every other seeded test in this repo relies on the same behavior,
so this should be a non-issue, but confirm rather than assume for a plan this
size.

- [ ] **Step 4: Connect all N sessions and start the per-session schedule**

```csharp
            var receivedBySession = new ConcurrentDictionary<int, ConcurrentQueue<StateUpdatePacket>>();
            var loginConfirmed = new TaskCompletionSource[SessionCount];
            var sessionTasks = new Task[SessionCount];
            var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(TrafficWindowSeconds));

            for (int i = 0; i < SessionCount; i++)
            {
                int idx = i;
                long playerId = 990_000_000L + idx;
                bool isFighter = idx % 2 == 0;
                receivedBySession[idx] = new ConcurrentQueue<StateUpdatePacket>();
                loginConfirmed[idx] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                sessionTasks[idx] = Task.Run(async () =>
                {
                    var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri("ws://localhost:8090/"), CancellationToken.None);
                    byte[] authBuffer = E2ETestHarness.BuildAuthHandshakeBuffer(E2ETestHarness.MintTestJwt(accountIds[idx]));
                    await ws.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                    var recvCts = new CancellationTokenSource();
                    var receiveTask = Task.Run(async () =>
                    {
                        var buf = new byte[1024];
                        while (!recvCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), recvCts.Token);
                                if (result.MessageType == WebSocketMessageType.Close) break;
                                if (result.Count >= Marshal.SizeOf<StateUpdatePacket>())
                                {
                                    var state = MemoryMarshal.Read<StateUpdatePacket>(new ReadOnlySpan<byte>(buf, 0, result.Count));
                                    receivedBySession[idx].Enqueue(state);
                                    loginConfirmed[idx].TrySetResult();
                                }
                            }
                            catch { break; }
                        }
                    });

                    await Task.WhenAny(loginConfirmed[idx].Task, Task.Delay(TimeSpan.FromSeconds(10)));

                    // Enter combat or gathering immediately, then alternate a
                    // checkpoint-forcing SpendAttributePoint and a market
                    // listing every ~8s until the traffic window ends.
                    var enterActivity = new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = isFighter ? 55 : 1001 };
                    await SendCommandAsync(ws, enterActivity);

                    int cycle = 0;
                    while (!runCts.IsCancellationRequested)
                    {
                        await Task.Delay(4000);
                        if (runCts.IsCancellationRequested) break;

                        if (cycle % 2 == 0)
                        {
                            await SendCommandAsync(ws, new ClientCommandPacket
                            {
                                Command = CommandType.SpendAttributePoint,
                                TargetId = 0,
                                LimitPrice = 1
                            });
                        }
                        else
                        {
                            await SendCommandAsync(ws, new ClientCommandPacket
                            {
                                Command = CommandType.MarketListItem,
                                TargetId = equipmentIdToList[idx],
                                LimitPrice = 100
                            });
                        }
                        cycle++;
                    }

                    // Reconnect leg: close cleanly, wait briefly for the
                    // Logout to be processed (NetworkBroadcastSystem's
                    // socket-closure block enqueues a real CommandType.Logout
                    // - see SimulationEngine's IsEpochExemptCommand), then
                    // open a fresh socket with a fresh JWT and confirm
                    // progress carried across the reconnect rather than
                    // resetting.
                    recvCts.Cancel();
                    try { await receiveTask; } catch { }
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "sustained-load reconnect", CancellationToken.None);
                    ws.Dispose();
                    await Task.Delay(500);

                    var reconnectSocket = new ClientWebSocket();
                    await reconnectSocket.ConnectAsync(new Uri("ws://localhost:8090/"), CancellationToken.None);
                    byte[] reAuth = E2ETestHarness.BuildAuthHandshakeBuffer(E2ETestHarness.MintTestJwt(accountIds[idx]));
                    await reconnectSocket.SendAsync(new ArraySegment<byte>(reAuth), WebSocketMessageType.Binary, true, CancellationToken.None);

                    var postReconnectBuf = new byte[1024];
                    using var postReconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        var result = await reconnectSocket.ReceiveAsync(new ArraySegment<byte>(postReconnectBuf), postReconnectCts.Token);
                        if (result.Count >= Marshal.SizeOf<StateUpdatePacket>())
                        {
                            var postState = MemoryMarshal.Read<StateUpdatePacket>(new ReadOnlySpan<byte>(postReconnectBuf, 0, result.Count));
                            receivedBySession[idx].Enqueue(postState);
                        }
                    }
                    catch { }
                    finally
                    {
                        reconnectSocket.Dispose();
                    }
                });
            }

            await Task.WhenAll(sessionTasks);
```

`SendCommandAsync` is a small local helper mirroring the existing
`MemoryMarshal.Write`/`SendAsync` pattern used throughout `E2EGameLoopTest`
for every command send — add it as a private static method on this class
rather than inlining the marshal boilerplate at every call site.

- [ ] **Step 5: Assertions**

```csharp
            // A) No starved queue - the worker must have drained both of its
            // queues by the time traffic generation stops, not merely be
            // making progress. This is the exact symptom that shipped:
            // "kills, XP, gold, the codex and gathering all kept working"
            // while these two queues grew forever.
            bool drained = await E2ETestHarness.WaitForConditionAsync(
                () => CombatLootEngine.DropRequestQueue.IsEmpty && CombatLootEngine.GatheringGrantQueue.IsEmpty,
                timeoutMs: DrainTimeoutSeconds * 1000);
            _o.WriteLine($"Loot queue drained: {drained}. DropRequestQueue={CombatLootEngine.DropRequestQueue.Count}, GatheringGrantQueue={CombatLootEngine.GatheringGrantQueue.Count}");
            Assert.True(drained, "loot/gathering queues never emptied - the worker fell behind and never caught up");

            // B) Checkpoint age stays bounded - the system's own contract
            // (CheckpointBoundaryTicks), checked against every packet from
            // every session, not just the last one.
            int worstTicksSinceLastFlush = 0;
            foreach (var kv in receivedBySession)
            {
                int sessionMax = kv.Value.Select(p => p.TicksSinceLastFlush).DefaultIfEmpty(0).Max();
                worstTicksSinceLastFlush = Math.Max(worstTicksSinceLastFlush, sessionMax);
                Assert.True(sessionMax < StateCheckpointManager.CheckpointBoundaryTicks,
                    $"session {kv.Key} observed TicksSinceLastFlush={sessionMax}, at or past the {StateCheckpointManager.CheckpointBoundaryTicks}-tick contract, under a pool bounded to {PoolBound}");
            }
            _o.WriteLine($"Worst observed TicksSinceLastFlush across {SessionCount} sessions: {worstTicksSinceLastFlush} (contract ceiling {StateCheckpointManager.CheckpointBoundaryTicks})");

            // C) No dropped loot - every fighting session actually received
            // SOMETHING (equipment or material), and every gathering session
            // received materials. Checked against the combined total rather
            // than equipment alone: 200 kills at a 15% gear / 35% material
            // chance were needed for LootWorkerResilienceTests to reliably
            // avoid a zero on gear alone - this scenario runs far fewer
            // kills per session in 60 contended seconds, so asserting on
            // "gear OR materials landed" is the low-flake form of the same
            // check (both landing zero has probability on the order of
            // 1e-5 or less at even a modest kill count).
            await using var verifyDb = await contextFactory.CreateDbContextAsync();
            var barren = new System.Collections.Generic.List<string>();
            for (int i = 0; i < SessionCount; i++)
            {
                long playerId = 990_000_000L + i;
                int gear = await verifyDb.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == playerId);
                long mats = await verifyDb.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == playerId).SumAsync(c => (long?)c.Quantity) ?? 0L;
                if (gear <= 1 && mats == 0) // gear starts at 1 (the seeded listing) - a fighter/gatherer with no NEW gear and no materials got nothing
                {
                    barren.Add($"player {playerId} ({(i % 2 == 0 ? "fighter" : "gatherer")}): gear={gear} materials={mats}");
                }
            }
            _o.WriteLine(barren.Count == 0 ? "every session received loot or materials" : string.Join("; ", barren));
            Assert.True(barren.Count == 0, "at least one session under sustained load received neither equipment nor materials: " + string.Join("; ", barren));

            // D) Reconnect carried progress forward - every session's
            // post-reconnect packet shows XP/gold/points at least as high as
            // anything observed before the disconnect, proving the
            // checkpoint/logout flush survived contention rather than
            // silently losing the session's last few seconds.
            foreach (var kv in receivedBySession)
            {
                var all = kv.Value.ToList();
                if (all.Count < 2) continue; // a session that never got even a login+reconnect pair can't be checked this way
                var preDisconnectMaxXp = all.SkipLast(1).Select(p => p.CurrentXp).DefaultIfEmpty(0L).Max();
                var postReconnectXp = all.Last().CurrentXp;
                Assert.True(postReconnectXp >= preDisconnectMaxXp,
                    $"session {kv.Key} lost XP across reconnect: pre-disconnect max {preDisconnectMaxXp}, post-reconnect {postReconnectXp}");
            }

            graph.SimulationEngine.Stop();
            graph.NetworkSystem.Stop();
```

`SkipLast(1)` assumes the reconnect packet enqueued in Step 4 is genuinely
the last item in that session's queue, which holds because nothing else
enqueues into `receivedBySession[idx]` after the reconnect leg starts —
confirm this ordering assumption still holds if Step 4 is restructured during
implementation.

- [ ] **Step 6: Run it**

Stop the running server first. Run:

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj --filter "FullyQualifiedName~SustainedLoadTests"`

Expected: passes, with `_o.WriteLine` output showing the drain confirmation,
the worst observed `TicksSinceLastFlush` well under 3000, and "every session
received loot or materials." If it does NOT pass on the current codebase,
that is this plan's actual deliverable working as intended — read the
failure before changing the test to make it pass; the task board's own
framing is that no test has ever exercised this combination, so a real
finding here is plausible and should be reported, not silently patched away
with a longer timeout.

- [ ] **Step 7: Run the full server suite once**

`dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`

Expected: all pass, including `CronWorkerGuardTests` (unaffected —
`CombatLootEngine` is unchanged) and both Task 1 `E2EGameLoopTest` tests.

- [ ] **Step 8: Commit**

```bash
git add server/FolkIdle.Server.Tests/SustainedLoadTests.cs
git commit -m "test(load): a sustained-load scenario against a deliberately bounded connection pool"
```

---

## Not in this plan

This plan does not touch `CombatLootEngine`, `StateCheckpointManager`,
`MarketEscrowEngine`, or `ConnectionStringDefaults` themselves — it is a test
only. If Step 6 surfaces a real regression (a queue that does not drain, a
checkpoint age that exceeds the contract, loot actually lost), fixing that is
a separate, scoped follow-up plan once the failure is understood, not a
same-session patch to make the new test green. Offline catch-up (mentioned in
the task board's system list alongside combat/gathering/checkpoints) is not
driven by this scenario — it requires a real elapsed-time gap between login
and a prior session's `LastLogoutTimestamp`, which `OfflineSimulationEngine`
resolves at `LoadPlayerState` time rather than during an active session, and
does not interact with the connection-pool-contention shape this test is
built to force the same way the five systems above do; a dedicated
offline-catch-up-under-contention scenario is a reasonable future addition
but was left out here to keep this test's runtime and failure surface
focused on the incident it was written to have caught.

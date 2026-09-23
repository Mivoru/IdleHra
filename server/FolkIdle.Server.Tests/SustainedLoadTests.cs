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

        private static async Task SendCommandAsync(ClientWebSocket ws, ClientCommandPacket cmd)
        {
            byte[] buffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), cmd);
            await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

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
            // hands it to both for exactly this reason (see the plan's
            // Global Constraints). Using the raw container string for either
            // would give that operation an unbounded 100-connection pool and
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

            var graph = E2ETestHarness.BuildEngineGraph(serviceProvider, contextFactory, retryingDbOptions, "http://localhost:8095/");

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            graph.NetworkSystem.Start();
            graph.SimulationEngine.Start();
            graph.LootEngine.StartCron();

            // Half the sessions fight, half gather - both feed CombatLootEngine's
            // two queues (DropRequestQueue / GatheringGrantQueue), which is the
            // exact split that starved production (fighters produce loot
            // requests, gatherers produce grants at a much higher rate per
            // action).
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
                        UnspentAttributePoints = 10000,
                        // Modul: MarketEscrowEngine.ListItemAsync's very first gate
                        // is a guild trade license (player.GuildId <= 0 rejects
                        // before any equipment/price check runs at all) - a plain
                        // field compare, no Guilds table join, so any positive
                        // value satisfies it without needing a real guild row.
                        // Without this every MarketListItem in this scenario
                        // would reject at that gate and never reach the escrow
                        // write this scenario means to contend over.
                        GuildId = 1L,
                        // Modul: task 24. Fighters used to target monster 55,
                        // a pre-canon Forest Rat whose loot table is
                        // deliberately empty (ContentRegistry, "LootTableId
                        // 1-90 ... resolve to an empty table"), so assertion C
                        // could never pass for them however well combat ran.
                        // They fight the first canon monster now, and it bites
                        // back: with an empty larder a bare character halts
                        // OutOfFood and then dies before its first kill. The
                        // larder is what sustains a fight in this game, so the
                        // scenario stocks one, as a real player would.
                        LarderSlot1ItemId = ContentRegistry.RawFishItemIds.First(),
                        LarderSlot1Count = 500,
                        // And an unarmed level-0 character takes about a
                        // minute per Field Mouse - one kill in the whole
                        // window, too few for a drop to be expected. Might is
                        // raised so a fighter lands several kills; the subject
                        // here is the loot pipeline under load, not pacing.
                        BaseStrength = 400
                    });

                    // Modul: a registered account always owns at least one
                    // characters row (SlotIndex 0) - StateCheckpointManager.
                    // LoadPlayerState's "no eligible character" default
                    // (ActiveActivityId = 0) exists specifically for a
                    // player with none, and this scenario means to model a
                    // real account under load, not that edge case.
                    db.CharacterRecords.Add(new CharacterRecord
                    {
                        Id = Guid.NewGuid(),
                        PlayerId = playerId,
                        AgePhase = 1,
                        SlotIndex = 0
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

            var receivedBySession = new ConcurrentDictionary<int, ConcurrentQueue<StateUpdatePacket>>();
            var loginConfirmed = new TaskCompletionSource[SessionCount];
            var sessionTasks = new Task[SessionCount];
            var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(TrafficWindowSeconds));

            // Modul: A REAL CLIENT ECHOES THE SERVER'S OWN EPOCH, AND A
            // SIMULATED ONE THAT DOESN'T GETS TerminateSessionForSecurity'D
            // MID-RUN.
            //
            // ValidateEpochSynchronization rejects any command whose
            // LogicEpochCounter drifts more than 5 from the live payload's -
            // and FlushStateAndAdvance bumps that counter by 1 on every
            // committed checkpoint. With no Redis registered (Global
            // Constraints), a fighting session's own auto-salvage gold forces
            // a checkpoint on nearly every kill, and SpendAttributePoint/
            // MarketListItem force one explicitly - so a command stream that
            // always sends LogicEpochCounter = 0 (the struct default) drifts
            // past tolerance within seconds and every session gets
            // disconnected out from under the test, mid-window, with a
            // WebSocketException rather than a real assertion failure. Track
            // the epoch the server last reported per session and echo it,
            // exactly as the real client does.
            var currentEpoch = new long[SessionCount];

            for (int i = 0; i < SessionCount; i++)
            {
                int idx = i;
                bool isFighter = idx % 2 == 0;
                receivedBySession[idx] = new ConcurrentQueue<StateUpdatePacket>();
                loginConfirmed[idx] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                sessionTasks[idx] = Task.Run(async () =>
                {
                    var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri("ws://localhost:8095/"), CancellationToken.None);
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
                                    Interlocked.Exchange(ref currentEpoch[idx], state.LogicEpochCounter);
                                    loginConfirmed[idx].TrySetResult();
                                }
                            }
                            catch { break; }
                        }
                    });

                    await Task.WhenAny(loginConfirmed[idx].Task, Task.Delay(TimeSpan.FromSeconds(10)));

                    // Enter combat or gathering immediately, then alternate a
                    // checkpoint-forcing SpendAttributePoint and a market
                    // listing every ~4s until the traffic window ends.
                    var enterActivity = new ClientCommandPacket
                    {
                        Command = CommandType.ChangeActivity,
                        TargetId = isFighter ? ContentRegistry.FirstCanonicalMonsterId : 1001,
                        LogicEpochCounter = Interlocked.Read(ref currentEpoch[idx])
                    };
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
                                LimitPrice = 1,
                                LogicEpochCounter = Interlocked.Read(ref currentEpoch[idx])
                            });
                        }
                        else
                        {
                            await SendCommandAsync(ws, new ClientCommandPacket
                            {
                                Command = CommandType.MarketListItem,
                                TargetId = equipmentIdToList[idx],
                                LimitPrice = 100,
                                LogicEpochCounter = Interlocked.Read(ref currentEpoch[idx])
                            });
                        }
                        cycle++;
                    }

                    // Reconnect leg: close cleanly, wait briefly for the
                    // Logout to be processed (NetworkBroadcastSystem's
                    // socket-closure block enqueues a real CommandType.Logout,
                    // exempt from the epoch gate and flushed synchronously -
                    // see SimulationEngine.IsEpochExemptCommand and the
                    // Logout branch), then open a fresh socket with a fresh
                    // JWT and confirm progress carried across the reconnect
                    // rather than resetting.
                    // Modul: cancelling the CancellationToken a pending
                    // ReceiveAsync is awaiting on does not "gracefully stop"
                    // it - .NET's ManagedWebSocket treats a cancelled receive
                    // as an ABORT, moving the socket to WebSocketState.Aborted
                    // rather than Open/CloseReceived/CloseSent. CloseAsync
                    // only accepts those three states and throws on Aborted,
                    // which is not a server defect (nothing on the wire
                    // caused it) - it is this harness racing its own receive
                    // loop's cancellation against a graceful close it can no
                    // longer perform. This scenario does not need a clean
                    // close handshake, only a fresh socket afterward with a
                    // fresh JWT - matching the tolerance already used for the
                    // post-reconnect read below.
                    recvCts.Cancel();
                    try { await receiveTask; } catch { }
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "sustained-load reconnect", CancellationToken.None); } catch { }
                    ws.Dispose();
                    await Task.Delay(500);

                    var reconnectSocket = new ClientWebSocket();
                    await reconnectSocket.ConnectAsync(new Uri("ws://localhost:8095/"), CancellationToken.None);
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

            // C) No dropped loot - the fighting sessions (at least half - see the
            // note at the assertion) received equipment or material, and every
            // gathering session received materials.
            //
            // Modul: EXCLUDE "gold" FROM THE MATERIALS SUM, DELIBERATELY.
            //
            // CommodityRecords is also where gold lives (StateCheckpointManager
            // credits CommodityRecords["gold"] on every flush - see
            // AuthenticationEngine's seeding comment and the checkpoint's own
            // "gold" writes), and this scenario forces a flush on nearly every
            // tick (no Redis registered, SpendAttributePoint's boundary trick,
            // MarketListItem's synchronous FlushStateAndAdvance). Summing every
            // CommodityRecords row unfiltered would make this assertion pass
            // even with CombatLootEngine completely dead, because gold keeps
            // flowing on an entirely separate path - which is exactly the
            // historical symptom this test exists to catch ("kills, XP, gold,
            // the codex and gathering all kept working" while equipment and
            // materials did not). The check has to look at what the LOOT
            // WORKER actually grants, not at gold.
            // What each session was last seen DOING, so a barren session
            // explains itself: idle with a halt reason, or fighting with no
            // XP, are two different defects.
            foreach (var kv in receivedBySession.OrderBy(k => k.Key))
            {
                var seen = kv.Value.ToList();
                if (seen.Count == 0) { _o.WriteLine($"session {kv.Key}: no packets"); continue; }
                var last = seen[^1];
                _o.WriteLine($"session {kv.Key}: packets={seen.Count} activities=[{string.Join(",", seen.Select(p => p.ActiveActivityId).Distinct())}] halts=[{string.Join(",", seen.Select(p => p.ActivityHaltReason).Distinct())}] minHp={seen.Min(p => p.PlayerHp)} xp={seen.Max(p => p.CurrentXp)}");
            }

            await using var verifyDb = await contextFactory.CreateDbContextAsync();
            var barren = new System.Collections.Generic.List<string>();
            for (int i = 0; i < SessionCount; i++)
            {
                long playerId = 990_000_000L + i;
                long seededListing = equipmentIdToList[i];
                int gear = await verifyDb.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == playerId && e.Id != seededListing);
                long mats = await verifyDb.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId != "gold")
                    .SumAsync(c => (long?)c.Quantity) ?? 0L;
                // Modul: the seeded listing is excluded by id rather than
                // allowed for as "gear <= 1". A successful MarketListItem moves
                // it into escrow, so a fighter whose one drop was equipment
                // read gear=1 and was counted barren - measured, "gear=1
                // materials=0" in a failing run. A session with no gear beyond
                // the seed and no non-gold materials got nothing from the loot
                // worker.
                if (gear == 0 && mats == 0)
                {
                    int codexKills = await verifyDb.MonsterCodexEntries.AsNoTracking().Where(c => c.PlayerId == playerId).SumAsync(c => (int?)c.KillCount) ?? 0;
                    barren.Add($"player {playerId} ({(i % 2 == 0 ? "fighter" : "gatherer")}): gear={gear} materials={mats} codexKills={codexKills}");
                }
            }
            _o.WriteLine(barren.Count == 0 ? "every session received loot or materials" : string.Join("; ", barren));

            // Modul: gatherers and fighters are held to different standards
            // because one is deterministic and the other is dice.
            //
            // A harvest grants on every cycle, so a gatherer with nothing got
            // nothing from the worker - no allowance. A kill drops a material
            // 35% of the time and equipment 15%, so it yields NOTHING about
            // 55% of the time, and a fighter lands only ~4-5 kills in this
            // window: each one comes up empty-handed 5-9% of the time on a
            // perfectly healthy server. "Every fighter got loot" therefore
            // passed about a third of the time - measured 2026-09-23, green
            // alone and red in the full suite on identical kill counts.
            //
            // What this assertion exists to catch - the worker dead, or
            // starved by the gathering queue - zeroes EVERY fighter at once.
            // So fighters are held to "at least half": ~91% are expected to
            // land something, and a dead or starved drain delivers 0%.
            var barrenGatherers = barren.Where(b => b.Contains("(gatherer)")).ToList();
            int barrenFighters = barren.Count - barrenGatherers.Count;
            int fighterCount = (SessionCount + 1) / 2;
            Assert.True(barrenGatherers.Count == 0, "a gathering session under sustained load received no materials: " + string.Join("; ", barrenGatherers));
            Assert.True(barrenFighters * 2 <= fighterCount,
                $"{barrenFighters} of {fighterCount} fighting sessions received no loot - far beyond drop-rate variance, so the loot worker is dead or starved: " + string.Join("; ", barren));

            // D) Reconnect carried progress forward - every session's
            // post-reconnect packet shows XP at least as high as anything
            // observed before the disconnect, proving the checkpoint/logout
            // flush survived contention rather than silently losing the
            // session's last few seconds.
            foreach (var kv in receivedBySession)
            {
                var all = kv.Value.ToList();
                if (all.Count < 2) continue; // a session that never got even a login+reconnect pair can't be checked this way
                // Modul: (level, XP) as a pair. CurrentXp is XP INTO the
                // current level and restarts at every level-up, so comparing
                // it alone reported a lost-progress failure for any session
                // that levelled up - which never ran while assertion C failed
                // above it, and fired the moment fighters could land kills.
                var preDisconnectMax = all.SkipLast(1)
                    .Select(p => (p.CurrentLevel, p.CurrentXp))
                    .DefaultIfEmpty((CurrentLevel: 0, CurrentXp: 0L))
                    .Max();
                var postReconnect = (all.Last().CurrentLevel, all.Last().CurrentXp);
                Assert.True(postReconnect.CompareTo(preDisconnectMax) >= 0,
                    $"session {kv.Key} lost progress across reconnect: pre-disconnect max level {preDisconnectMax.CurrentLevel} xp {preDisconnectMax.CurrentXp}, post-reconnect level {postReconnect.CurrentLevel} xp {postReconnect.CurrentXp}");
            }

            graph.SimulationEngine.Stop();
            graph.NetworkSystem.Stop();
        }
    }
}

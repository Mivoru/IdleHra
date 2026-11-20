using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    // Modul: moved out of NetworkBroadcastSystem.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md). Routing stays in RouteCoreAsync.
    public partial class NetworkBroadcastSystem
    {
        // Modul: hand-rolled Prometheus text-exposition-format metrics
        // endpoint (no prometheus-net or other external dependency, per this
        // task's explicit constraint). Unauthenticated, matching the
        // existing /health/* endpoints - Prometheus scraping is expected to
        // happen from inside the cluster network, not across the public
        // internet. TickDurationBucketCount*/TickDurationSumMicros read directly
        // off SimulationEngine.GetMetrics() (a ref struct accessor, no
        // allocation) if a SimulationEngine has been registered; the write
        // queue length comes from SCARD against RedisSessionCache.
        // DirtyPlayersSetKey (see RedisWriteBehindEngine.FlushNowAsync,
        // which drains that same Redis set), defaulting to 0 if Redis is
        // unavailable rather than failing the whole scrape.
        private async Task HandleMetrics(HttpListenerContext context)
        {
            try
            {
                int activeSessions = _connectedClients.Count;

                long tickCount = 0;
                long tickSumMicros = 0;
                long bucket10 = 0, bucket25 = 0, bucket50 = 0, bucket100 = 0, bucket250 = 0, bucketInf = 0;
                long ticksDropped = 0, catchUpTicks = 0;
                if (_simulationEngine != null)
                {
                    EngineMetricsPayload metrics = _simulationEngine.GetMetrics();
                    tickCount = metrics.TotalTicksProcessed;
                    ticksDropped = metrics.TicksDropped;
                    catchUpTicks = metrics.CatchUpTicks;
                    tickSumMicros = metrics.TickDurationSumMicros;
                    bucket10 = metrics.TickDurationBucketCount10Ms;
                    bucket25 = metrics.TickDurationBucketCount25Ms;
                    bucket50 = metrics.TickDurationBucketCount50Ms;
                    bucket100 = metrics.TickDurationBucketCount100Ms;
                    bucket250 = metrics.TickDurationBucketCount250Ms;
                    bucketInf = metrics.TickDurationBucketCountInf;
                }

                // Task 43: a rolling-window p99 (see GetRecentTickPercentiles)
                // and the checkpoint writer's back-pressure.
                var recentTicks = _simulationEngine?.GetRecentTickPercentiles();
                var checkpointWriter = _simulationEngine?.CheckpointManager.Writer;

                long writeQueueLength = 0;
                var redis = _serviceProvider.GetService<StackExchange.Redis.IConnectionMultiplexer>();
                if (redis != null && redis.IsConnected)
                {
                    try
                    {
                        writeQueueLength = await redis.GetDatabase().SetLengthAsync(RedisSessionCache.DirtyPlayersSetKey);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Metrics: failed to read write-behind queue length: {ex.Message}");
                    }
                }

                var body = new System.Text.StringBuilder();
                body.Append("# HELP folkidle_active_sessions_total Current number of connected WebSocket sessions.\n");
                body.Append("# TYPE folkidle_active_sessions_total gauge\n");
                body.Append("folkidle_active_sessions_total ").Append(activeSessions).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_tick_duration_milliseconds Duration of the 10Hz simulation tick loop.\n");
                body.Append("# TYPE folkidle_tick_duration_milliseconds histogram\n");
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"10\"} ").Append(bucket10).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"25\"} ").Append(bucket25).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"50\"} ").Append(bucket50).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"100\"} ").Append(bucket100).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"250\"} ").Append(bucket250).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_bucket{le=\"+Inf\"} ").Append(bucketInf).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_sum ").Append((tickSumMicros / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                body.Append("folkidle_tick_duration_milliseconds_count ").Append(tickCount).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_ticks_catch_up_total Ticks started immediately because the fixed schedule was behind.\n");
                body.Append("# TYPE folkidle_ticks_catch_up_total counter\n");
                body.Append("folkidle_ticks_catch_up_total ").Append(catchUpTicks).Append('\n');
                body.Append("# HELP folkidle_ticks_dropped_total Ticks owed beyond the catch-up cap and never run.\n");
                body.Append("# TYPE folkidle_ticks_dropped_total counter\n");
                body.Append("folkidle_ticks_dropped_total ").Append(ticksDropped).Append('\n');
                // Modul: WHICH OFFLINE MODEL PAID THE LOGOUTS (2026-11-03). The
                // first rebirth experiment ran on the old expected-value model
                // because nothing set FOLKIDLE_OFFLINE_SIM, and nothing could
                // tell afterwards. The server reports what it actually uses,
                // so a bot run's summary.md states it instead of assuming it.
                body.Append("# HELP folkidle_offline_sim 1 when away combat is fought by the per-swing simulator (FOLKIDLE_OFFLINE_SIM=1), 0 for the old model.\n");
                body.Append("# TYPE folkidle_offline_sim gauge\n");
                body.Append("folkidle_offline_sim ").Append(FolkIdle.Server.Engine.OfflineSimulationEngine.UseCombatSimulator ? 1 : 0).Append('\n');
                body.Append('\n');
                if (recentTicks is { } rt)
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    body.Append("# HELP folkidle_tick_duration_recent_milliseconds Tick duration over the last ").Append(SimulationEngine.RecentTickWindow).Append(" ticks.\n");
                    body.Append("# TYPE folkidle_tick_duration_recent_milliseconds summary\n");
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"0.5\"} ").Append(rt.P50Ms.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"0.95\"} ").Append(rt.P95Ms.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"0.99\"} ").Append(rt.P99Ms.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds{quantile=\"1\"} ").Append(rt.MaxMs.ToString("0.###", inv)).Append('\n');
                    body.Append("folkidle_tick_duration_recent_milliseconds_count ").Append(rt.Samples).Append('\n');
                    body.Append('\n');
                }
                DevRouteTiming.AppendMetrics(body);
                FolkIdle.Server.Domain.Shared.SimLockstep.Crons?.AppendMetrics(body);
                if (checkpointWriter != null)
                {
                    body.Append("# HELP folkidle_checkpoint_queue_depth Checkpoint jobs queued on CheckpointWriter.\n");
                    body.Append("# TYPE folkidle_checkpoint_queue_depth gauge\n");
                    body.Append("folkidle_checkpoint_queue_depth ").Append(checkpointWriter.QueueDepth).Append('\n');
                    body.Append("# TYPE folkidle_checkpoint_flushes_committed_total counter\n");
                    body.Append("folkidle_checkpoint_flushes_committed_total ").Append(checkpointWriter.FlushesCommitted).Append('\n');
                    body.Append("# TYPE folkidle_checkpoint_flushes_failed_total counter\n");
                    body.Append("folkidle_checkpoint_flushes_failed_total ").Append(checkpointWriter.FlushesFailed).Append('\n');
                    body.Append("# HELP folkidle_checkpoint_dead_letters_total CHECKPOINT-DEADLETTER lines written (grep the server log).\n");
                    body.Append("# TYPE folkidle_checkpoint_dead_letters_total counter\n");
                    body.Append("folkidle_checkpoint_dead_letters_total ").Append(checkpointWriter.DeadLetters).Append('\n');
                    body.Append('\n');
                }
                body.Append("# HELP folkidle_database_write_queue_length Players with state pending Redis write-behind flush.\n");
                body.Append("# TYPE folkidle_database_write_queue_length gauge\n");
                body.Append("folkidle_database_write_queue_length ").Append(writeQueueLength).Append('\n');

                // Modul: task 41. The outbox's one remaining way to lose an
                // event is a full queue (512, drop-oldest), so that drop is
                // counted here - an uncounted drop would be the silent loss
                // the outbox was built to end. The depth is summed across
                // live sessions; a steadily high value means peers that are
                // not keeping up.
                long outboxDepth = 0;
                foreach (var kvp in _connectedClients)
                {
                    outboxDepth += kvp.Value.PendingEventCount;
                }
                body.Append('\n');
                body.Append("# HELP folkidle_forced_disconnects_total Sessions ended by ForceDisconnect (validator refusals, anti-cheat, rollbacks). The app log says which handler: grep [kick].\n");
                body.Append("# TYPE folkidle_forced_disconnects_total counter\n");
                body.Append("folkidle_forced_disconnects_total ").Append(ForcedDisconnectsTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_outbox_events_dropped_total Event frames (loot, combat, chat) dropped because a session outbox was full.\n");
                body.Append("# TYPE folkidle_outbox_events_dropped_total counter\n");
                body.Append("folkidle_outbox_events_dropped_total ").Append(WebSocketSession.EventsDroppedTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_outbox_queue_depth Event frames waiting in session outboxes, summed over connected sessions.\n");
                body.Append("# TYPE folkidle_outbox_queue_depth gauge\n");
                body.Append("folkidle_outbox_queue_depth ").Append(outboxDepth).Append('\n');

                // Modul: task 46 (audit item 8, "8a - measure"). See
                // StateFrameMetrics for what these count and why. Read
                // straight off the static counters, the same pattern as
                // folkidle_outbox_events_dropped_total above.
                body.Append('\n');
                body.Append("# HELP folkidle_state_frame_bytes_total Bytes of state frame (JSON serialize output, or the binary struct size) offered to a session, summed since start.\n");
                body.Append("# TYPE folkidle_state_frame_bytes_total counter\n");
                body.Append("folkidle_state_frame_bytes_total ").Append(StateFrameMetrics.BytesTotal).Append('\n');
                body.Append("# HELP folkidle_ws_deflate_input_bytes_total Bytes of text frames handed to a session deflater (task 46), summed since start.\n");
                body.Append("# TYPE folkidle_ws_deflate_input_bytes_total counter\n");
                body.Append("folkidle_ws_deflate_input_bytes_total ").Append(FrameDeflater.InputBytesTotal).Append('\n');
                body.Append("# HELP folkidle_ws_deflate_output_bytes_total Bytes those deflaters put on the wire, summed since start.\n");
                body.Append("# TYPE folkidle_ws_deflate_output_bytes_total counter\n");
                body.Append("folkidle_ws_deflate_output_bytes_total ").Append(FrameDeflater.OutputBytesTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_state_frames_total State frames (JSON or binary) offered to a session, summed since start.\n");
                body.Append("# TYPE folkidle_state_frames_total counter\n");
                body.Append("folkidle_state_frames_total ").Append(StateFrameMetrics.FramesTotal).Append('\n');
                body.Append('\n');
                body.Append("# HELP folkidle_state_frame_serialize_microseconds Time PacketJsonCodec.SerializeToUtf8 took to build one JSON state frame. Binary-path frames are not timed - see StateFrameMetrics.\n");
                body.Append("# TYPE folkidle_state_frame_serialize_microseconds histogram\n");
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"100\"} ").Append(StateFrameMetrics.Bucket100Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"250\"} ").Append(StateFrameMetrics.Bucket250Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"500\"} ").Append(StateFrameMetrics.Bucket500Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"1000\"} ").Append(StateFrameMetrics.Bucket1000Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"2500\"} ").Append(StateFrameMetrics.Bucket2500Us).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_bucket{le=\"+Inf\"} ").Append(StateFrameMetrics.BucketInfUs).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_sum ").Append(StateFrameMetrics.SerializeSumMicroseconds).Append('\n');
                body.Append("folkidle_state_frame_serialize_microseconds_count ").Append(StateFrameMetrics.SerializeCount).Append('\n');

                byte[] payload = System.Text.Encoding.UTF8.GetBytes(body.ToString());
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain; version=0.0.4";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Metrics endpoint error: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        internal static bool DevToolsEnabled()
            => DevToolsEnabled(
                Environment.GetEnvironmentVariable("FOLKIDLE_DEV_TOOLS"),
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));

        // Modul: the flag alone was a convention - the box's real .env is not
        // versioned, and one stray FOLKIDLE_DEV_TOOLS=1 there would let any
        // signed-in guest open or close the world boss window and wipe every
        // player's attempts. Production (ops/oracle sets DOTNET_ENVIRONMENT)
        // refuses the tools whatever the flag says.
        internal static bool DevToolsEnabled(string? flag, string? dotnetEnvironment)
            => flag == "1"
               && !string.Equals(dotnetEnvironment, "Production", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Dev-box tools. Every route here answers 404 unless FOLKIDLE_DEV_TOOLS=1.
        ///
        /// Modul: NOT under /api/v1/admin/, because the dev fixture is not an
        /// admin by IsAdmin's rule (username Mivoru or one e-mail), and a gate the
        /// fixture fails is a tool exercise.mjs cannot use. The environment
        /// variable is the gate instead, and it fails CLOSED: run-dev.ps1 sets
        /// it, ops/oracle does not, so production 404s. It exists because the
        /// world boss window is calendar-driven and nothing could strike on the
        /// other 13-16 days of a month (task 25).
        /// </summary>
        private async Task HandleDevEndpoints(HttpListenerContext context, string requestPath)
        {
            try
            {
                if (!DevToolsEnabled())
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                // Modul: THE DEEP'S WAY IN (task 37). Descending needs a run at
                // the bottom of floor 8, which no fixture sheet reaches reliably
                // - the doors are a gamble by design. This places the CALLER's
                // run there, as a bank-ready full clear, so exercise.mjs can
                // test a descent on every run instead of on a lucky one.
                if (requestPath == "/api/v1/dev/delve/at-bottom" && context.Request.HttpMethod == "POST")
                {
                    var delve = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                    var view = await delve.DevPlaceRunAtBottomAsync(playerId);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
                    return;
                }

                // The lantern purchase needs a Deep run whose light is out,
                // which failing doors reaches only by chance.
                if (requestPath == "/api/v1/dev/delve/lantern-out" && context.Request.HttpMethod == "POST")
                {
                    var delve = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                    bool done = await delve.DevPutOutTheLanternAsync(playerId);
                    context.Response.StatusCode = done ? 200 : 409;
                    return;
                }

                // The playtest bots' what-if projection - see
                // NetworkBroadcastSystem.BotWhatIf.cs.
                if (requestPath == "/api/v1/dev/projection/whatif" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevProjectionWhatIf(context, playerId);
                    return;
                }

                // Task 83: finish the running Workshop commission now, and with
                // {"Refund": true} give its price back - so exercise.mjs can
                // place, collect and discard one without spending the fixture.
                if (requestPath == "/api/v1/dev/workshop/finish" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevWorkshopFinish(context, playerId);
                    return;
                }

                // Refine (roadmap 3.4): read an item's raw affix payload, or put
                // one back and refund gold - so exercise.mjs can refine on the
                // fixture and leave the item and the wallet as it found them.
                if (requestPath == "/api/v1/dev/item/payload" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevItemPayload(context, playerId);
                    return;
                }

                // Herbs and potions: put a stack of one material in the caller's
                // chest (or take it back with a negative quantity), so
                // exercise.mjs can brew a potion without an hour of picking and
                // round-trip what it granted. Never below zero.
                if (requestPath == "/api/v1/dev/materials/grant" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevMaterialGrant(context, playerId);
                    return;
                }

                // The equipment dungeon: set the caller's tickets (exercise.mjs
                // runs a stage and puts them back).
                if (requestPath == "/api/v1/dev/dungeon/tickets" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevDungeonTickets(context, playerId);
                    return;
                }
                if (requestPath == "/api/v1/dev/dungeon/unlock" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevDungeonUnlock(context, playerId);
                    return;
                }

                // ...and end the caller's running potions on the next tick.
                if (requestPath == "/api/v1/dev/potions/expire" && context.Request.HttpMethod == "POST")
                {
                    Domain.Shared.PotionShelfTickCoordinator.DevExpireRequests.Enqueue(playerId);
                    await WriteJsonAsync(context, new { Queued = true });
                    return;
                }

                // Loot chests: grant one, so exercise.mjs can open it on the fixture.
                if (requestPath == "/api/v1/dev/chests/grant" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevLootChestGrant(context, playerId);
                    return;
                }

                // The quest line: take back a claim's reward so exercise.mjs
                // can claim on the fixture and round-trip it.
                if (requestPath == "/api/v1/dev/quests/unclaim" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevQuestUnclaim(context, playerId);
                    return;
                }

                // Task 54: a cosmetic chest for the caller, so exercise.mjs can
                // open one without thousands of kills first.
                if (requestPath == "/api/v1/dev/cosmetics/chest" && context.Request.HttpMethod == "POST")
                {
                    await HandleDevCosmeticChest(context, playerId);
                    return;
                }

                // Task 69: nine Normal pieces of one region-5 item the fixture
                // does not otherwise hold, so exercise.mjs can fuse a whole
                // stack (9 -> 3 -> 1) and bin the result - a round trip that
                // leaves the fixture as it found it, instead of eating the
                // fixture's own piles one run at a time.
                if (requestPath == "/api/v1/dev/forge/stack" && context.Request.HttpMethod == "POST")
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    long[] ids = await DevFixtureSeeder.GrantForgeStackAsync(db, playerId);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                    {
                        BaseItemId = DevFixtureSeeder.DevStackBaseId,
                        Ids = ids,
                    });
                    return;
                }

                // A title for the caller, through the real idempotent grant, so
                // the title picker and the profile can be exercised without a
                // player first clearing floor 10 by luck.
                if (requestPath == "/api/v1/dev/titles/grant" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    string? slug = null;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("Slug", out var el) && el.ValueKind == JsonValueKind.String) slug = el.GetString();
                    }
                    catch (JsonException) { }

                    if (slug == null || FolkIdle.Server.Domain.Progression.TitleRegistry.Find(slug) == null)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    await FolkIdle.Server.Domain.Progression.TitleEngine.GrantAsync(db, playerId, slug, GameClock.UtcNow);
                    context.Response.StatusCode = 200;
                    return;
                }

                // Task 87: put one boss's Ascension ladder back to a step, so
                // exercise.mjs can climb a rung and leave the fixture as it found
                // it. Body {Region, Step, BossDefeated?}; reloads the live session so the tick's
                // cache of the ladder agrees with the table again.
                if (requestPath == "/api/v1/dev/boss-ascension/restore" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    int region = 0, step = 0;
                    bool? bossDefeated = null;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("Region", out var r) && r.TryGetInt32(out int rv)) region = rv;
                        if (parsed.RootElement.TryGetProperty("Step", out var st) && st.TryGetInt32(out int sv)) step = sv;
                        if (parsed.RootElement.TryGetProperty("BossDefeated", out var bd)
                            && (bd.ValueKind == JsonValueKind.True || bd.ValueKind == JsonValueKind.False)) bossDefeated = bd.GetBoolean();
                    }
                    catch (JsonException) { }

                    if (!FolkIdle.Server.Domain.Combat.BossAscensionRegistry.IsValidRegion(region))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                        await FolkIdle.Server.Domain.Combat.BossAscensionEngine.DevRestoreAsync(db, playerId, region, step, bossDefeated);
                    }
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                    await WriteJsonAsync(context, new { Ok = true });
                    return;
                }

                // Task 84: put one Great Work back to (Stage, Progress) and move the
                // stock of one material by a signed StockDelta, so exercise.mjs can
                // deposit and leave the fixture as it found it. Body {Region, Stage,
                // Progress, Material (0 log, 1 ore), StockDelta}; reloads the session
                // so the tick's cache of the stages agrees with the table again.
                if (requestPath == "/api/v1/dev/great-works/restore" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    int region = 0, stage = 0, material = 0;
                    long progress = 0, stockDelta = 0;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("Region", out var r) && r.TryGetInt32(out int rv)) region = rv;
                        if (parsed.RootElement.TryGetProperty("Stage", out var st) && st.TryGetInt32(out int sv)) stage = sv;
                        if (parsed.RootElement.TryGetProperty("Material", out var mt) && mt.TryGetInt32(out int mv)) material = mv;
                        if (parsed.RootElement.TryGetProperty("Progress", out var pr) && pr.TryGetInt64(out long pv)) progress = pv;
                        if (parsed.RootElement.TryGetProperty("StockDelta", out var sd) && sd.TryGetInt64(out long sdv)) stockDelta = sdv;
                    }
                    catch (JsonException) { }

                    if (!FolkIdle.Server.Domain.Progression.GreatWorksRegistry.IsValidRegion(region)
                        || !FolkIdle.Server.Domain.Progression.GreatWorksRegistry.IsValidMaterialKind(material))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                        await FolkIdle.Server.Domain.Progression.GreatWorksEngine.DevRestoreAsync(db, playerId, region, stage, progress, material, stockDelta);
                    }
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                    await WriteJsonAsync(context, new { Ok = true });
                    return;
                }

                // The seasonal event out of season: {"EventId": 1} forces it
                // live, 0 returns to the calendar. {"Grant": n} adds n of the
                // current event's currency to the caller's live balance,
                // outside the daily cap - exercise.mjs buys with it.
                // A monster pet "found" on demand, through the same queue a kill
                // uses - so exercise.mjs can prove a find lands and a duplicate
                // mails diamonds without waiting 100,000 kills.
                if (requestPath == "/api/v1/dev/pet-drop" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    string? petId = null;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("PetId", out var pid)) petId = pid.GetString();
                    }
                    catch (JsonException) { }
                    if (FolkIdle.Server.Domain.Progression.PetRegistry.Find(petId) == null)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                    FolkIdle.Server.Domain.Progression.PetEngine.Drops.Enqueue(
                        new FolkIdle.Server.Domain.Progression.PetDropNote(playerId, petId!));
                    await WriteJsonAsync(context, new { Queued = petId });
                    return;
                }

                if (requestPath == "/api/v1/dev/event" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    int? eventId = null;
                    int grant = 0;
                    try
                    {
                        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        if (parsed.RootElement.TryGetProperty("EventId", out var ev) && ev.TryGetInt32(out int evv)) eventId = evv;
                        if (parsed.RootElement.TryGetProperty("Grant", out var gr) && gr.TryGetInt32(out int grv)) grant = grv;
                    }
                    catch (JsonException) { }

                    if (eventId.HasValue)
                    {
                        if (eventId.Value != 0 && FolkIdle.Server.Domain.Progression.SeasonalEventRegistry.Find(eventId.Value) == null)
                        {
                            context.Response.StatusCode = 400;
                            return;
                        }
                        FolkIdle.Server.Domain.Progression.SeasonalEventRegistry.ForceLive = eventId.Value;
                    }
                    var (devEvent, devPhase) = FolkIdle.Server.Domain.Progression.SeasonalEventRegistry.Current(GameClock.UtcNowOffset);
                    if (grant > 0 && devEvent != null)
                    {
                        FolkIdle.Server.Domain.Progression.EventShopEngine.Refunds.Enqueue(
                            new FolkIdle.Server.Domain.Progression.EventShopRefund(playerId, devEvent.CurrencyStamp, grant));
                    }
                    await WriteJsonAsync(context, new { EventId = devEvent?.Id ?? 0, Phase = (int)devPhase });
                    return;
                }

                if (_worldBossEngine == null)
                {
                    context.Response.StatusCode = 503;
                    return;
                }

                if (requestPath == "/api/v1/dev/worldboss/window" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<DevWorldBossWindowRequest>(
                        string.IsNullOrWhiteSpace(body) ? "{}" : body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DevWorldBossWindowRequest();

                    if (req.Open)
                    {
                        await _worldBossEngine.OpenManualWindowAsync(req.DurationSeconds);
                    }
                    else
                    {
                        await _worldBossEngine.CloseManualWindowAsync();
                    }

                    await WriteDevWorldBossStateAsync(context, playerId);
                    return;
                }

                // Ends registration now (spec 2026-10-13): the revealed half of
                // the week is otherwise reachable only on a Wednesday.
                if (requestPath == "/api/v1/dev/worldboss/reveal" && context.Request.HttpMethod == "POST")
                {
                    await _worldBossEngine.RevealNowAsync();
                    await WriteDevWorldBossStateAsync(context, playerId);
                    return;
                }

                if (requestPath == "/api/v1/dev/worldboss/attempt" && context.Request.HttpMethod == "GET")
                {
                    await WriteDevWorldBossStateAsync(context, playerId);
                    return;
                }

                // Modul: the caller's gold ledger for today, so exercise.mjs can
                // check a sink against the debit it caused rather than the wallet.
                // The wallet also moves with combat income and offline payouts,
                // and "the gold actually left" failed working builds on exactly
                // that (a 707,733 gold CombatAway payout landed mid-step).
                if (requestPath == "/api/v1/dev/gold-ledger" && context.Request.HttpMethod == "GET")
                {
                    await WriteDevGoldLedgerAsync(context, playerId);
                    return;
                }

                context.Response.StatusCode = 404;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dev endpoint error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>The boss and the CALLER's attempt row, straight from the database.</summary>
        private async Task WriteDevWorldBossStateAsync(HttpListenerContext context, long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var snapshot = await db.WorldBossSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
            var attempt = await db.PlayerWorldBossAttempts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.PlayerId == playerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                EventState = snapshot?.EventState ?? 0,
                CurrentHp = snapshot?.CurrentHp ?? 0,
                MaxHp = snapshot?.MaxHp ?? 0,
                EventEndEpoch = snapshot?.EventEndEpoch ?? 0,
                AttemptCount = attempt?.AttemptCount ?? 0,
                TotalInflictedDamage = attempt?.TotalInflictedDamage ?? 0,
                HpRevealed = snapshot?.HpRevealed ?? 0,
                RevealEpoch = snapshot?.RevealEpoch ?? 0,
                DefeatedAtEpoch = snapshot?.DefeatedAtEpoch ?? 0,
                IsRegistered = attempt?.IsRegistered ?? false,
                MilestonesPaid = attempt?.MilestonesPaid ?? 0,
            });
        }

        /// <summary>Today's (UTC) spend and income rows for the CALLER, keyed by the enum names.</summary>
        private async Task WriteDevGoldLedgerAsync(HttpListenerContext context, long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var day = DateOnly.FromDateTime(GameClock.UtcNow);

            var spend = await db.GoldSpendDaily.AsNoTracking()
                .Where(r => r.PlayerId == playerId && r.Day == day)
                .ToListAsync();
            var income = await db.GoldIncomeDaily.AsNoTracking()
                .Where(r => r.PlayerId == playerId && r.Day == day)
                .ToListAsync();

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                Day = day.ToString("yyyy-MM-dd"),
                Spend = spend.ToDictionary(r => ((GoldSpendCategory)r.Category).ToString(), r => r.Amount),
                Income = income.ToDictionary(r => ((GoldIncomeSource)r.Source).ToString(), r => r.Amount),
            });
        }

        private bool IsAdmin(FolkIdle.Server.Models.PlayerRecord? player)
        {
            if (player == null) return false;
            if (!string.IsNullOrEmpty(player.Username) && player.Username.Equals("Mivoru", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(player.Email) && player.Email.Equals("prochalcz@gmail.com", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private async Task HandleAdminEndpoints(HttpListenerContext context, string requestPath)
        {
            try
            {
                long requesterId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (requesterId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var player = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == requesterId).FirstOrDefaultAsync();
                if (!IsAdmin(player))
                {
                    context.Response.StatusCode = 403;
                    return;
                }

                if (requestPath == "/api/v1/admin/status" && context.Request.HttpMethod == "GET")
                {
                    string json = JsonSerializer.Serialize(new { isAdmin = true, profanityEnabled = FolkIdle.Server.Engine.ChatProfanityFilter.IsEnabled });
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    return;
                }

                // Modul: SEASON CONTROL WAS HERE (2026-09-28 to 2026-10-14):
                // pause, move the end, and a typed "END SEASON" that rolled
                // every account over at once. Seasons are gone - a run ends
                // with its own player's rebirth - so the endpoints went with
                // them and the server has no way left to wipe the roster.

                if (requestPath == "/api/v1/admin/profanity" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, bool>>(body);
                    if (req != null && req.TryGetValue("enabled", out bool isEnabled))
                    {
                        FolkIdle.Server.Engine.ChatProfanityFilter.IsEnabled = isEnabled;
                        context.Response.StatusCode = 200;
                        return;
                    }
                    context.Response.StatusCode = 400;
                    return;
                }

                if (requestPath == "/api/v1/admin/announce" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(body);
                    if (req != null && req.TryGetValue("text", out string? message) && !string.IsNullOrWhiteSpace(message))
                    {
                        if (message.Length > 128) message = message.Substring(0, 128);
                        
                        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                        var packet = new ResponseChatMessagePacket
                        {
                            MessageLength = (ushort)bytes.Length,
                            ChannelType = 3, // ANNOUNCEMENT
                            SenderPlayerId = -1,
                            TimestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        unsafe
                        {
                            fixed (byte* src = bytes)
                            {
                                System.Buffer.MemoryCopy(src, packet.MessageText, 128, bytes.Length);
                            }
                        }

                        // Broadcast to everyone, through each outbox - the
                        // same fan-out chat uses (see ChatFrames), so an
                        // announcement is queued rather than dropped behind
                        // a state frame and one stalled socket cannot hold
                        // this request open.
                        var frames = new ChatFrames(packet);
                        foreach (var target in _connectedClients.Values)
                        {
                            if (target.Socket.State == System.Net.WebSockets.WebSocketState.Open)
                            {
                                frames.EnqueueTo(target);
                            }
                        }

                        // Task 110e: an admin announcement is News too, so a
                        // player who signs in afterwards still reads it.
                        await ChatHistory.RecordAsync(_serviceProvider, ChatEngine.AnnouncementChannelType, 0, packet.SenderPlayerId, message, packet.TimestampEpochMs);
                        
                        context.Response.StatusCode = 200;
                        return;
                    }
                    context.Response.StatusCode = 400;
                    return;
                }
                
                // Modul: the whole point of the penalty table - a moderator can
                // answer "why is this account restricted" from the admin
                // screen, rather than by reading two booleans in a database
                // console and guessing which of five writers set them.
                if (requestPath == "/api/v1/admin/penalties" && context.Request.HttpMethod == "GET")
                {
                    var pq = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    string who = pq["username"] ?? string.Empty;
                    if (string.IsNullOrEmpty(who))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var subject = await db.PlayerRecords.AsNoTracking()
                        .FirstOrDefaultAsync(p => p.Username != null && p.Username.ToLower() == who.ToLower());
                    if (subject == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    var history = await db.AccountPenalties.AsNoTracking()
                        .Where(a => a.PlayerId == subject.Id)
                        .OrderByDescending(a => a.AppliedAtEpochMs)
                        .Take(50)
                        .ToListAsync();

                    var payload = new
                    {
                        subject.Id,
                        subject.Username,
                        Restricted = subject.IsQuarantined || subject.Quarantine_Active,
                        // Modul: a restricted account with NO row predates this
                        // table. Said plainly rather than rendered as an empty
                        // history, because "we do not know" and "nothing
                        // happened" are different answers and only one of them
                        // is honest.
                        ReasonKnown = history.Count > 0,
                        Penalties = history.Select(a => new
                        {
                            a.Id,
                            Source = PenaltySource.Describe(a.Source),
                            a.ReasonCode,
                            a.DetailCode,
                            a.AppliedAtEpochMs,
                            a.AppliedBy,
                            a.Note,
                            a.LiftedAtEpochMs,
                            a.LiftedBy
                        })
                    };

                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                    return;
                }

                if (requestPath == "/api/v1/admin/ban" && context.Request.HttpMethod == "POST")
                {
                    var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    string targetUsername = query["username"] ?? string.Empty;
                    if (!string.IsNullOrEmpty(targetUsername))
                    {
                        var target = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Username != null && p.Username.ToLower() == targetUsername.ToLower());
                        if (target != null)
                        {
                            target.IsQuarantined = true;
                            target.Quarantine_Active = true;

                            // Modul: A MODERATOR BAN IS NOT AN ANTI-CHEAT FLAG,
                            // 2026-09-01. Both write the same two booleans on
                            // PlayerRecords, so after the fact they were
                            // indistinguishable - "the detector caught you" and
                            // "a human decided" read identically, which is
                            // unfair to the player and useless to the
                            // moderator. The row carries WHO, WHEN and WHICH.
                            db.AccountPenalties.Add(new AccountPenalty
                            {
                                PlayerId = target.Id,
                                Source = PenaltySource.Admin,
                                ReasonCode = 0,
                                DetailCode = 0,
                                AppliedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                AppliedBy = player?.Username,
                                Note = query["reason"]
                            });

                            await db.SaveChangesAsync();
                            context.Response.StatusCode = 200;
                            return;
                        }
                    }
                    context.Response.StatusCode = 404;
                    return;
                }

                if (requestPath == "/api/v1/admin/unban" && context.Request.HttpMethod == "POST")
                {
                    var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                    string targetUsername = query["username"] ?? string.Empty;
                    if (!string.IsNullOrEmpty(targetUsername))
                    {
                        var target = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Username != null && p.Username.ToLower() == targetUsername.ToLower());
                        if (target != null)
                        {
                            target.IsQuarantined = false;
                            target.Quarantine_Active = false;

                            // Modul: AND UNFREEZE THEIR MARKET ORDERS. The
                            // anti-cheat's shadow ban flags every open listing
                            // (see AntiCheatTelemetryEngine), and lifting the
                            // account flag did not clear them - so an unbanned
                            // player was returned to a game where their
                            // listings stayed dead, with nothing saying why.
                            await db.Database.ExecuteSqlRawAsync(
                                "UPDATE \"MarketOrderRecords\" SET \"IsQuarantined\" = FALSE WHERE \"SellerId\" = {0}",
                                target.Id);

                            // Stamped, not deleted - "flagged in August, cleared
                            // in September" is the history that makes a second
                            // flag readable.
                            long liftedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            await db.Database.ExecuteSqlRawAsync(
                                "UPDATE account_penalties SET \"LiftedAtEpochMs\" = {0}, \"LiftedBy\" = {1} " +
                                "WHERE \"PlayerId\" = {2} AND \"LiftedAtEpochMs\" IS NULL",
                                liftedAt, (object?)player?.Username ?? System.DBNull.Value, target.Id);

                            await db.SaveChangesAsync();

                            // Modul: AND THE REDIS FRAME, 2026-10-10. The
                            // session frame keeps its own is_quarantined, and
                            // RedisWriteBehindEngine copies it back onto
                            // PlayerRecords - so an unban that only touched the
                            // row was undone by the next write-behind. Found
                            // lifting player 8: the row read false, the frame
                            // still read 1.
                            var unbanRedis = _serviceProvider.GetService<IConnectionMultiplexer>();
                            if (unbanRedis != null && unbanRedis.IsConnected)
                            {
                                await unbanRedis.GetDatabase().HashSetAsync(
                                    RedisSessionCache.SessionStateKey(target.Id), "is_quarantined", 0);
                            }
                            context.Response.StatusCode = 200;
                            return;
                        }
                    }
                    context.Response.StatusCode = 404;
                    return;
                }

                if (requestPath == "/api/v1/admin/mail" && context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);
                    var req = JsonSerializer.Deserialize<AdminMailRequest>(body);
                    if (req == null
                        || req.Diamonds < 0 || req.Gold < 0
                        || (!string.IsNullOrEmpty(req.TitleSlug) && FolkIdle.Server.Domain.Progression.TitleRegistry.Find(req.TitleSlug) == null))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var now = GameClock.UtcNowOffset.ToUnixTimeSeconds();

                    // Modul: SEVERAL NAMES, comma-separated (2026-10-09), so a
                    // gift to three testers is one send - and an empty name is
                    // still "everyone", which is why a mail carrying diamonds or
                    // a title must NAME its recipients: a slip of the field must
                    // not hand every guest on the server a title.
                    var names = (req.TargetUsername ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(n => n.ToLowerInvariant())
                        .Distinct()
                        .ToList();
                    if (names.Count == 0 && (req.Diamonds > 0 || !string.IsNullOrEmpty(req.TitleSlug)))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var targets = names.Count == 0
                        ? await db.PlayerRecords.Select(p => new { p.Id, p.Username, p.Email }).ToListAsync()
                        : await db.PlayerRecords
                            .Where(p => p.Username != null && names.Contains(p.Username.ToLower()))
                            .Select(p => new { p.Id, p.Username, p.Email })
                            .ToListAsync();
                    if (names.Count > 0 && targets.Count != names.Count)
                    {
                        // A misspelt name sends nothing, rather than a gift to some.
                        context.Response.StatusCode = 404;
                        return;
                    }

                    db.MailboxInstances.AddRange(targets.Select(t => new FolkIdle.Server.Models.MailboxInstance
                    {
                        PlayerId = t.Id,
                        BaseItemId = req.BaseItemId ?? string.Empty,
                        QualityTier = req.QualityTier,
                        Quantity = req.Quantity,
                        GoldAttachment = req.Gold,
                        DiamondAttachment = req.Diamonds,
                        TitleAttachment = string.IsNullOrEmpty(req.TitleSlug) ? null : req.TitleSlug,
                        SenderName = req.SenderName,
                        MessageText = req.MessageText,
                        ReceivedTimestamp = now
                    }));
                    await db.SaveChangesAsync();

                    // Modul: the wrapper is CZECH on purpose (owner, 2026-10-09):
                    // the testers are Czech and the owner writes the message in
                    // Czech, so an English greeting around it read as mixed.
                    // The same message by email, to the named recipients that
                    // have an address. Never to "everyone": that is a newsletter,
                    // and it needs consent this endpoint does not check.
                    int emailed = 0;
                    if (req.SendEmail && names.Count > 0 && !string.IsNullOrWhiteSpace(req.MessageText))
                    {
                        var sender = _serviceProvider.GetRequiredService<Engine.IEmailSender>();
                        string subject = string.IsNullOrWhiteSpace(req.EmailSubject) ? "Zpráva z FolkIdle" : req.EmailSubject!;
                        foreach (var t in targets.Where(t => !string.IsNullOrWhiteSpace(t.Email)))
                        {
                            string text = $"Ahoj {t.Username},\n\n{req.MessageText}\n\nOdměnu najdeš v ingame mailboxu.\n\nhttps://folkidle.duckdns.org";
                            if (await sender.SendAsync(t.Email!, subject, text)) emailed++;
                        }
                    }

                    await WriteJsonAsync(context, new { Mailed = targets.Count, Emailed = emailed });
                    return;
                }

                context.Response.StatusCode = 404;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Admin endpoint error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Dev only: POST /api/v1/dev/item/payload { "Id": 1 } answers the item's
        /// raw AffixPayload; with "Payload" it writes it back, and with "Gold" it
        /// refunds that much - the round trip exercise.mjs's Refine step needs.
        /// </summary>
        private async Task HandleDevItemPayload(HttpListenerContext context, long playerId)
        {
            long id = 0; string? payload = null; long gold = 0;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Id", out var i) && i.TryGetInt64(out long iv)) id = iv;
                if (parsed.RootElement.TryGetProperty("Payload", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String) payload = p.GetString();
                if (parsed.RootElement.TryGetProperty("Gold", out var g) && g.TryGetInt64(out long gv)) gold = Math.Max(0L, gv);
            }
            catch (System.Text.Json.JsonException) { context.Response.StatusCode = 400; return; }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var item = await db.EquipmentInstances.FirstOrDefaultAsync(e => e.Id == id && e.PlayerId == playerId);
            if (item == null) { context.Response.StatusCode = 404; return; }
            if (payload != null)
            {
                item.AffixPayload = payload;
                // GoldLedger: not income - a dev refund of a Refine the exercise spent a moment ago.
                if (gold > 0) await CommodityLedger.AddAsync(db, playerId, "gold", gold);
                await db.SaveChangesAsync();
            }
            await WriteJsonAsync(context, new { item.Id, item.AffixPayload });
        }
    }
}

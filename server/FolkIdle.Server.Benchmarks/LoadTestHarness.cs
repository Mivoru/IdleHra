using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Benchmarks
{
    // Modul: Phase 5, Part 2. Multi-client WebSocket load test harness -
    // spins up up to 1000 independent virtual clients (bots) against a
    // live FolkIdle server, each running its own connect/handshake/action
    // loop, to profile connection-layer and database/cache behavior under
    // concurrent load (PgBouncer pool pressure, Redis lock contention)
    // that no single-connection integration test can exercise. Not a unit
    // test - a standalone tool run manually against a running server
    // instance, mirroring FolkIdle.ChaosTester's overall shape but
    // measuring gameplay-command confirmation latency (via the Part 5
    // command-result ring buffer) rather than only chat echo round trips,
    // and mixing in ForgeSplicing/MarketListing traffic specifically
    // because those two paths open real Postgres Serializable transactions
    // (ForgeSplicingEngine, MarketEscrowEngine) - the paths most likely to
    // surface PgBouncer pool exhaustion or Redis session-lock contention
    // under concurrent load.
    //
    // Task-premise correction: the task text names CommandType IDs 12/13
    // for "ForgeSplicing"/"MarketListing" - those IDs are actually
    // DepositToBank/WithdrawFromBank (see ClientCommandPacket.cs's
    // CommandType enum). The real IDs are ExecuteForgeFusion = 2 and
    // MarketListItem = 9; this harness sends those, not the literal IDs
    // the task named, since sending 12/13 would exercise bank deposit/
    // withdraw instead of forge/market traffic and would not test what
    // this task is actually asking for. Likewise "736-byte Auth/State
    // layout" does not match the current wire sizes (AuthHandshakePacket
    // is 530 bytes, StateUpdatePacket is 680 bytes as of the Phase 3
    // wire-bloat pass) - this harness reads both sizes dynamically via
    // Marshal.SizeOf<T>() rather than hardcoding either number, so it
    // stays correct across future packet-layout changes instead of
    // silently drifting stale.
    public static class LoadTestHarness
    {
        public static async Task<int> RunAsync(LoadTestOptions options)
        {
            Console.WriteLine("FolkIdle Load Test Harness");
            Console.WriteLine($"  Server: {options.ServerBaseUrl}");
            Console.WriteLine($"  Bots: {options.BotCount}");
            Console.WriteLine($"  Duration: {options.DurationSeconds} seconds");
            Console.WriteLine($"  Ramp concurrency: {options.RampConcurrency}");
            Console.WriteLine($"  Action interval: {options.ActionIntervalMinMs}-{options.ActionIntervalMaxMs} ms per bot");
            Console.WriteLine($"  Observed AuthHandshakePacket size: {Marshal.SizeOf<AuthHandshakePacket>()} bytes");
            Console.WriteLine($"  Observed StateUpdatePacket size: {Marshal.SizeOf<StateUpdatePacket>()} bytes");
            Console.WriteLine();

            var stats = new LoadTestStats();
            var bots = new BotConnectionState[options.BotCount];
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i] = new BotConnectionState();
            }

            using var httpClient = new HttpClient();
            using var rampSemaphore = new SemaphoreSlim(options.RampConcurrency, options.RampConcurrency);
            using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));

            var reporterTask = ReporterLoopAsync(bots, stats, options, runCts.Token);

            var workerTasks = new Task[options.BotCount];
            for (int i = 0; i < options.BotCount; i++)
            {
                int botIndex = i;
                workerTasks[i] = RunBotAsync(botIndex, bots[botIndex], options, httpClient, rampSemaphore, stats, runCts.Token);
            }

            await Task.WhenAll(workerTasks).ConfigureAwait(false);

            // Stop the reporter's own loop deterministically rather than
            // relying on it observing the same CancellationToken mid-sleep -
            // one final report below covers the tail end of the run either
            // way.
            await reporterTask.ConfigureAwait(false);

            PrintFinalSummary(bots, stats);
            return 0;
        }

        // Modul: one simulated player end to end - login, connect,
        // handshake, then a combined receive loop and action-send loop
        // running concurrently until the overall run duration elapses.
        // Mirrors FolkIdle.ChaosTester.RunConnectionAsync's ramp-semaphore
        // and lifecycle structure exactly (see that method's own comment
        // for why the semaphore is released after handshake rather than
        // held for the connection's full lifetime).
        private static async Task RunBotAsync(int botIndex, BotConnectionState state, LoadTestOptions options, HttpClient httpClient, SemaphoreSlim rampSemaphore, LoadTestStats stats, CancellationToken runToken)
        {
            Interlocked.Increment(ref stats.ConnectionAttempts);

            await rampSemaphore.WaitAsync(runToken).ConfigureAwait(false);
            bool releasedRampSlot = false;

            ClientWebSocket? socket = null;
            try
            {
                string deviceId = $"loadtest-{botIndex}-{Guid.NewGuid():N}";

                string? jwt = await LoginAsync(httpClient, options.ServerBaseUrl, deviceId, stats).ConfigureAwait(false);
                if (jwt == null)
                {
                    return;
                }

                socket = new ClientWebSocket();
                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(new Uri(options.WebSocketBaseUrl), connectCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    stats.RecordFailure("connect-failed");
                    Console.WriteLine($"[{botIndex}] connect failed: {ex.Message}");
                    return;
                }

                long handshakeSentAtTicks = Stopwatch.GetTimestamp();
                await SendAuthHandshakeAsync(socket, jwt, runToken).ConfigureAwait(false);

                bool handshakeConfirmed = await WaitForHandshakeConfirmationAsync(socket, state, runToken).ConfigureAwait(false);
                if (!handshakeConfirmed)
                {
                    stats.RecordFailure("handshake-timeout");
                    Console.WriteLine($"[{botIndex}] handshake not confirmed (no StateUpdatePacket received).");
                    return;
                }

                state.HandshakeLatencyMs = (float)((Stopwatch.GetTimestamp() - handshakeSentAtTicks) * 1000.0 / Stopwatch.Frequency);
                Interlocked.Increment(ref stats.SuccessfulHandshakes);

                rampSemaphore.Release();
                releasedRampSlot = true;

                var receiveTask = ReceiveLoopAsync(socket, state, stats, runToken);
                var actionTask = ActionSendLoopAsync(botIndex, socket, state, options, stats, runToken);

                await Task.WhenAll(receiveTask, actionTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected at run-duration expiry.
            }
            catch (Exception ex)
            {
                stats.RecordFailure("unexpected-error");
                Console.WriteLine($"[{botIndex}] unexpected error: {ex.Message}");
            }
            finally
            {
                if (!releasedRampSlot)
                {
                    rampSemaphore.Release();
                }

                if (socket != null)
                {
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "load-test-complete", closeCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // Best-effort close - the run is ending regardless.
                    }
                    finally
                    {
                        socket.Dispose();
                    }
                }
            }
        }

        private static async Task<string?> LoginAsync(HttpClient httpClient, string serverBaseUrl, string deviceId, LoadTestStats stats)
        {
            try
            {
                var requestBody = new LoginRequestBody { deviceId = deviceId };
                using HttpResponseMessage response = await httpClient.PostAsJsonAsync($"{serverBaseUrl}/api/v1/auth/login", requestBody).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    stats.RecordFailure("login-failed");
                    return null;
                }

                var body = await response.Content.ReadFromJsonAsync<LoginResponseBody>().ConfigureAwait(false);
                if (body == null || string.IsNullOrEmpty(body.Token))
                {
                    stats.RecordFailure("login-malformed-response");
                    return null;
                }

                return body.Token;
            }
            catch (Exception)
            {
                stats.RecordFailure("login-exception");
                return null;
            }
        }

        // Mirrors WebSocketClient.SendAuthHandshakeAsync's exact fixed-buffer
        // write pattern - see FolkIdle.ChaosTester's identical helper.
        private static async Task SendAuthHandshakeAsync(ClientWebSocket socket, string jwt, CancellationToken token)
        {
            byte[] jwtBytes = Encoding.UTF8.GetBytes(jwt);
            int length = jwtBytes.Length > AuthHandshakePacket.JwtTokenCapacity ? AuthHandshakePacket.JwtTokenCapacity : jwtBytes.Length;

            AuthHandshakePacket packet = new AuthHandshakePacket
            {
                JwtTokenLength = (ushort)length,
                AssetHash = 0,
                PlatformSignature = 0
            };

            unsafe
            {
                byte* target = packet.JwtToken;
                for (int i = 0; i < AuthHandshakePacket.JwtTokenCapacity; i++)
                {
                    target[i] = i < length ? jwtBytes[i] : (byte)0;
                }
            }

            byte[] buffer = new byte[Marshal.SizeOf<AuthHandshakePacket>()];
            Unsafe.WriteUnaligned(ref buffer[0], packet);
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
        }

        // Waits only for the first StateUpdatePacket - the proof the
        // handshake succeeded and a playerId was resolved server-side.
        // Seeds MaxObservedResultTick from the first packet's ring buffer
        // so the action loop only ever measures NEW confirmations, never
        // one that happened to already be sitting in a stale slot from a
        // different prior session on the same player id.
        private static async Task<bool> WaitForHandshakeConfirmationAsync(ClientWebSocket socket, BotConnectionState state, CancellationToken runToken)
        {
            byte[] buffer = new byte[Marshal.SizeOf<StateUpdatePacket>()];
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                while (!timeoutCts.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return false;
                    }

                    if (result.Count == Marshal.SizeOf<StateUpdatePacket>())
                    {
                        var statePacket = MemoryMarshal.Read<StateUpdatePacket>(new ReadOnlySpan<byte>(buffer, 0, result.Count));
                        state.PlayerId = statePacket.PlayerId;
                        state.MaxObservedResultTick = MaxCommandResultTick(in statePacket);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        // Modul: zero-allocation per-message hot path - one reused receive
        // buffer for the connection's whole lifetime, no LINQ, no per-
        // message string formatting. Drains both StateUpdatePacket (the
        // 10Hz per-player broadcast, whose CommandResult0-3 ring buffer is
        // this harness's confirmation signal - see MaxCommandResultTick's
        // own comment) and ResponseChatMessagePacket (the chat echo),
        // distinguished purely by exact byte count, mirroring
        // WebSocketClient.ConnectAndReceiveLoopAsync's real dispatch.
        private static async Task ReceiveLoopAsync(ClientWebSocket socket, BotConnectionState state, LoadTestStats stats, CancellationToken runToken)
        {
            byte[] buffer = new byte[Marshal.SizeOf<StateUpdatePacket>()];

            try
            {
                while (socket.State == WebSocketState.Open && !runToken.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), runToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.Count == Marshal.SizeOf<ResponseChatMessagePacket>())
                    {
                        var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(buffer, 0, result.Count));
                        if (chatPacket.SenderPlayerId == state.PlayerId)
                        {
                            long awaitingSince = Interlocked.Exchange(ref state.ChatAwaitingSinceTicks, 0L);
                            if (awaitingSince != 0L)
                            {
                                float elapsedMs = (float)((Stopwatch.GetTimestamp() - awaitingSince) * 1000.0 / Stopwatch.Frequency);
                                state.RecordChatLatency(elapsedMs);
                                Interlocked.Increment(ref stats.ChatRoundTripped);
                            }
                        }
                        continue;
                    }

                    if (result.Count == Marshal.SizeOf<StateUpdatePacket>())
                    {
                        var statePacket = MemoryMarshal.Read<StateUpdatePacket>(new ReadOnlySpan<byte>(buffer, 0, result.Count));
                        uint newMax = MaxCommandResultTick(in statePacket);
                        if (newMax != state.MaxObservedResultTick)
                        {
                            state.MaxObservedResultTick = newMax;

                            long awaitingSince = Interlocked.Exchange(ref state.ActionAwaitingSinceTicks, 0L);
                            if (awaitingSince != 0L)
                            {
                                float elapsedMs = (float)((Stopwatch.GetTimestamp() - awaitingSince) * 1000.0 / Stopwatch.Frequency);
                                state.RecordActionLatency(elapsedMs);
                                Interlocked.Increment(ref stats.ActionsConfirmed);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected at run-duration expiry.
            }
            catch (Exception)
            {
                // Connection dropped mid-run - reflected in the final
                // socket-state tally the caller performs.
            }
        }

        // Modul: the command-result ring buffer's ResultTick is a
        // per-player monotonically increasing counter stamped by the tick
        // thread on every processed rejectable command (see
        // SimulationEngine's CommandResultQueue drain and
        // TickStatePayload.CommandResultSlot0-3) - it advances on both
        // success-adjacent and outright-rejected outcomes for the command
        // types this harness sends, so "the max tick across all 4 slots
        // increased since last observed" is a reliable, allocation-free
        // signal that the server actually processed a new command, distinct
        // from the routine 10Hz broadcast cadence which fires regardless of
        // whether any command was processed that tick.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint MaxCommandResultTick(in StateUpdatePacket packet)
        {
            uint max = packet.CommandResult0_Tick;
            if (packet.CommandResult1_Tick > max) max = packet.CommandResult1_Tick;
            if (packet.CommandResult2_Tick > max) max = packet.CommandResult2_Tick;
            if (packet.CommandResult3_Tick > max) max = packet.CommandResult3_Tick;
            return max;
        }

        // Modul: randomized 100-500ms per-bot interval (per the task's own
        // spec) deliberately avoids lockstep synchronization across up to
        // 1000 bots - a fixed shared interval would make every bot hammer
        // the server in the same instant every cycle, testing a bursty
        // traffic pattern rather than the sustained concurrent load this
        // harness is meant to profile. Only one outstanding action
        // confirmation is tracked per bot at a time (mirrors
        // FolkIdle.ChaosTester's identical chat-echo simplification) - if
        // the previous action has not confirmed within
        // ConfirmationTimeoutMs, it is counted as timed out rather than
        // left to await forever; a rising timed-out rate under increasing
        // bot count is this harness's observable proxy for PgBouncer pool
        // exhaustion or Redis session-lock contention, since
        // SafeDispatchAsync's own catch-and-log design means a DB/Redis
        // failure inside ForgeSplicingEngine/MarketEscrowEngine surfaces
        // server-side only as a log line, never a wire response - from a
        // black-box WebSocket client, that failure mode is indistinguishable
        // from a command that simply never got confirmed.
        private static async Task ActionSendLoopAsync(int botIndex, ClientWebSocket socket, BotConnectionState state, LoadTestOptions options, LoadTestStats stats, CancellationToken runToken)
        {
            var random = new Random(botIndex);
            byte[] commandBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
            byte[] chatBuffer = new byte[Marshal.SizeOf<RequestChatMessagePacket>()];

            try
            {
                while (!runToken.IsCancellationRequested)
                {
                    int jitterMs = random.Next(options.ActionIntervalMinMs, options.ActionIntervalMaxMs);
                    await Task.Delay(jitterMs, runToken).ConfigureAwait(false);

                    if (socket.State != WebSocketState.Open)
                    {
                        break;
                    }

                    long stillAwaitingSince = Interlocked.CompareExchange(ref state.ActionAwaitingSinceTicks, 0L, 0L);
                    if (stillAwaitingSince != 0L)
                    {
                        double outstandingMs = (Stopwatch.GetTimestamp() - stillAwaitingSince) * 1000.0 / Stopwatch.Frequency;
                        if (outstandingMs >= options.ConfirmationTimeoutMs)
                        {
                            Interlocked.CompareExchange(ref state.ActionAwaitingSinceTicks, 0L, stillAwaitingSince);
                            Interlocked.Increment(ref stats.ActionsTimedOut);
                        }
                        else
                        {
                            // Still waiting on the previous action's
                            // confirmation - skip sending a new one this
                            // cycle rather than stacking a second
                            // in-flight action this harness cannot
                            // distinguish from the first.
                            continue;
                        }
                    }

                    int actionRoll = random.Next(3);
                    if (actionRoll == 0)
                    {
                        await SendForgeSplicingAsync(socket, commandBuffer, state, random, stats, runToken).ConfigureAwait(false);
                    }
                    else if (actionRoll == 1)
                    {
                        await SendMarketListingAsync(socket, commandBuffer, state, random, stats, runToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendChatAsync(botIndex, socket, chatBuffer, state, stats, runToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected at run-duration expiry.
            }
            catch (Exception)
            {
                // Connection dropped mid-run.
            }
        }

        // Modul: ExecuteForgeFusion (CommandType 2, not the task-text's
        // literal "ID 12" - see this file's own top-of-file correction
        // comment) targets three equipment-instance ids (TargetId,
        // SecondaryId, TertiaryId) via ForgeSplicingEngine. Bots have no
        // real inventory, so these almost always resolve as a rejected
        // fusion (InvalidRequest/InsufficientGold) - that is fine and
        // expected for a load-generation tool: what is being measured is
        // round-trip confirmation time and server-side transactional
        // behavior under concurrency, not business-rule success.
        private static async Task SendForgeSplicingAsync(ClientWebSocket socket, byte[] buffer, BotConnectionState state, Random random, LoadTestStats stats, CancellationToken token)
        {
            var packet = new ClientCommandPacket
            {
                Command = CommandType.ExecuteForgeFusion,
                TargetId = random.Next(1, 1_000_000),
                SecondaryId = random.Next(1, 1_000_000),
                TertiaryId = random.Next(1, 1_000_000)
            };

            Unsafe.WriteUnaligned(ref buffer[0], packet);

            Interlocked.Exchange(ref state.ActionAwaitingSinceTicks, Stopwatch.GetTimestamp());
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            Interlocked.Increment(ref stats.ActionsSent);
            stats.RecordActionKind("forge");
        }

        // Modul: MarketListItem (CommandType 9, not the task-text's literal
        // "ID 13" - see this file's own top-of-file correction comment)
        // targets one equipment-instance id (TargetId) at a random price
        // (LimitPrice, kept positive so it passes ValidateMarketCommands's
        // earliest structural check even though the deeper
        // MarketEscrowEngine ownership check will almost always reject it
        // for a bot with no real inventory).
        private static async Task SendMarketListingAsync(ClientWebSocket socket, byte[] buffer, BotConnectionState state, Random random, LoadTestStats stats, CancellationToken token)
        {
            var packet = new ClientCommandPacket
            {
                Command = CommandType.MarketListItem,
                TargetId = random.Next(1, 1_000_000),
                LimitPrice = random.Next(1, 10_000)
            };

            Unsafe.WriteUnaligned(ref buffer[0], packet);

            Interlocked.Exchange(ref state.ActionAwaitingSinceTicks, Stopwatch.GetTimestamp());
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            Interlocked.Increment(ref stats.ActionsSent);
            stats.RecordActionKind("market");
        }

        private static async Task SendChatAsync(int botIndex, ClientWebSocket socket, byte[] buffer, BotConnectionState state, LoadTestStats stats, CancellationToken token)
        {
            string messageText = $"load-test message from bot {botIndex}";
            byte[] textBytes = Encoding.UTF8.GetBytes(messageText);
            int length = textBytes.Length > RequestChatMessagePacket.MessageCapacity ? RequestChatMessagePacket.MessageCapacity : textBytes.Length;

            var packet = new RequestChatMessagePacket { MessageLength = (ushort)length };
            unsafe
            {
                byte* target = packet.MessageText;
                for (int i = 0; i < RequestChatMessagePacket.MessageCapacity; i++)
                {
                    target[i] = i < length ? textBytes[i] : (byte)0;
                }
            }

            Unsafe.WriteUnaligned(ref buffer[0], packet);

            Interlocked.Exchange(ref state.ChatAwaitingSinceTicks, Stopwatch.GetTimestamp());
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
            Interlocked.Increment(ref stats.ChatSent);
            stats.RecordActionKind("chat");
        }

        // Modul: periodic telemetry reporter - wakes once every
        // ReportIntervalSeconds and prints exactly one consolidated block
        // built from a single reused StringBuilder, rather than any
        // per-bot or per-message logging, so the reporter itself cannot
        // become an allocation/CPU spike source that would distort the
        // very load measurements it exists to report. Percentile samples
        // are drawn from each bot's small fixed-capacity latency ring
        // buffer (see BotConnectionState) into one reused scratch list,
        // keeping this bounded by bot count regardless of run duration or
        // message rate instead of accumulating an ever-growing sample set.
        private static async Task ReporterLoopAsync(BotConnectionState[] bots, LoadTestStats stats, LoadTestOptions options, CancellationToken runToken)
        {
            var reportBuilder = new StringBuilder(512);
            var latencyScratch = new List<float>(bots.Length * 8);

            try
            {
                while (!runToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.ReportIntervalSeconds), runToken).ConfigureAwait(false);
                    PrintPeriodicReport(bots, stats, reportBuilder, latencyScratch);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected at run-duration expiry - the final summary after
                // RunAsync's Task.WhenAll covers the tail of the run.
            }
        }

        private static void PrintPeriodicReport(BotConnectionState[] bots, LoadTestStats stats, StringBuilder reportBuilder, List<float> latencyScratch)
        {
            latencyScratch.Clear();
            foreach (BotConnectionState bot in bots)
            {
                bot.CopyActionLatenciesInto(latencyScratch);
            }
            latencyScratch.Sort();

            reportBuilder.Clear();
            reportBuilder.Append("[report] handshakes=").Append(stats.SuccessfulHandshakes).Append('/').Append(stats.ConnectionAttempts);
            reportBuilder.Append(" actions sent=").Append(stats.ActionsSent);
            reportBuilder.Append(" confirmed=").Append(stats.ActionsConfirmed);
            reportBuilder.Append(" timed-out=").Append(stats.ActionsTimedOut);
            reportBuilder.Append(" chat sent=").Append(stats.ChatSent);
            reportBuilder.Append(" round-tripped=").Append(stats.ChatRoundTripped);

            if (latencyScratch.Count > 0)
            {
                reportBuilder.Append(" | action latency ms p90=").Append(Percentile(latencyScratch, 0.90).ToString("F1"));
                reportBuilder.Append(" p95=").Append(Percentile(latencyScratch, 0.95).ToString("F1"));
                reportBuilder.Append(" p99=").Append(Percentile(latencyScratch, 0.99).ToString("F1"));
            }

            Console.WriteLine(reportBuilder.ToString());
        }

        private static void PrintFinalSummary(BotConnectionState[] bots, LoadTestStats stats)
        {
            int attempts = stats.ConnectionAttempts;
            int successes = stats.SuccessfulHandshakes;
            int failures = attempts - successes;
            double successRate = attempts == 0 ? 0.0 : 100.0 * successes / attempts;

            int sent = stats.ActionsSent;
            int confirmed = stats.ActionsConfirmed;
            int timedOut = stats.ActionsTimedOut;
            double confirmRate = sent == 0 ? 0.0 : 100.0 * confirmed / sent;
            double timeoutRate = sent == 0 ? 0.0 : 100.0 * timedOut / sent;

            Console.WriteLine();
            Console.WriteLine("Load Test Summary");
            Console.WriteLine("------------------");
            Console.WriteLine($"Connection attempts:      {attempts}");
            Console.WriteLine($"Successful handshakes:    {successes}");
            Console.WriteLine($"Failed handshakes:        {failures}");
            Console.WriteLine($"Connection success rate:  {successRate:F2} percent");

            if (stats.FailureReasonCounts.Count > 0)
            {
                Console.WriteLine("Failure reason breakdown:");
                foreach (var kvp in stats.FailureReasonCounts)
                {
                    Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Gameplay actions sent:     {sent}");
            Console.WriteLine($"Gameplay actions confirmed: {confirmed} ({confirmRate:F2} percent)");
            Console.WriteLine($"Gameplay actions timed out: {timedOut} ({timeoutRate:F2} percent)");
            Console.WriteLine("(A rising timed-out rate as bot count increases is this harness's observable proxy for");
            Console.WriteLine(" PgBouncer database pool exhaustion or Redis session-lock contention - a server-side DB/");
            Console.WriteLine(" Redis failure inside ForgeSplicingEngine/MarketEscrowEngine is caught and logged server-side");
            Console.WriteLine(" by SafeDispatchAsync's design, never surfaced back over the wire, so from a black-box");
            Console.WriteLine(" WebSocket client it is indistinguishable from a command that simply never confirmed.)");

            if (stats.ActionKindCounts.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Action kind breakdown:");
                foreach (var kvp in stats.ActionKindCounts)
                {
                    Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Chat messages sent:          {stats.ChatSent}");
            Console.WriteLine($"Chat messages round-tripped: {stats.ChatRoundTripped}");

            var latencies = new List<float>(bots.Length * 8);
            foreach (BotConnectionState bot in bots)
            {
                bot.CopyActionLatenciesInto(latencies);
            }
            latencies.Sort();

            if (latencies.Count > 0)
            {
                double sum = 0.0;
                foreach (float v in latencies) sum += v;
                double avg = sum / latencies.Count;
                double max = latencies[^1];
                double p50 = Percentile(latencies, 0.50);
                double p90 = Percentile(latencies, 0.90);
                double p95 = Percentile(latencies, 0.95);
                double p99 = Percentile(latencies, 0.99);

                Console.WriteLine();
                Console.WriteLine("Gameplay action confirmation latency (milliseconds, most recent samples per bot):");
                Console.WriteLine($"  average: {avg:F2}");
                Console.WriteLine($"  p50:     {p50:F2}");
                Console.WriteLine($"  p90:     {p90:F2}");
                Console.WriteLine($"  p95:     {p95:F2}");
                Console.WriteLine($"  p99:     {p99:F2}");
                Console.WriteLine($"  max:     {max:F2}");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("No action confirmation latency samples were recorded.");
            }

            float handshakeSum = 0f;
            int handshakeCount = 0;
            foreach (BotConnectionState bot in bots)
            {
                if (bot.HandshakeLatencyMs > 0f)
                {
                    handshakeSum += bot.HandshakeLatencyMs;
                    handshakeCount++;
                }
            }

            if (handshakeCount > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Average handshake latency: {(handshakeSum / handshakeCount):F2} ms across {handshakeCount} bots");
            }
        }

        private static double Percentile(List<float> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0) return 0.0;
            int index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
            if (index < 0) index = 0;
            if (index >= sortedValues.Count) index = sortedValues.Count - 1;
            return sortedValues[index];
        }

        private sealed class LoginRequestBody
        {
            public string deviceId { get; set; } = string.Empty;
        }

        private sealed class LoginResponseBody
        {
            public string Token { get; set; } = string.Empty;
            public long ExpiresAtEpoch { get; set; }
        }
    }

    // Modul: per-bot mutable state shared between its receive loop and
    // action-send loop, which run concurrently on the same connection.
    // *AwaitingSinceTicks fields are Stopwatch timestamps (0 means "not
    // currently awaiting a confirmation/echo"), read/written via
    // Interlocked since the two loops run on different tasks. Latency
    // samples are kept in small fixed-capacity ring buffers (not an
    // unbounded list) so per-bot memory stays bounded regardless of run
    // duration or message rate - the actual zero/low-allocation hot path
    // this task's Constraint 2 is about.
    internal sealed class BotConnectionState
    {
        private const int LatencyCapacity = 32;

        public long PlayerId;
        public uint MaxObservedResultTick;
        public long ActionAwaitingSinceTicks;
        public long ChatAwaitingSinceTicks;
        public float HandshakeLatencyMs;

        private readonly float[] _actionLatenciesMs = new float[LatencyCapacity];
        private int _actionLatencyCursor;
        private int _actionLatencyCount;
        private readonly object _actionLatencyLock = new();

        private readonly float[] _chatLatenciesMs = new float[LatencyCapacity];
        private int _chatLatencyCursor;
        private int _chatLatencyCount;
        private readonly object _chatLatencyLock = new();

        public void RecordActionLatency(float elapsedMs)
        {
            lock (_actionLatencyLock)
            {
                _actionLatenciesMs[_actionLatencyCursor] = elapsedMs;
                _actionLatencyCursor = (_actionLatencyCursor + 1) % LatencyCapacity;
                if (_actionLatencyCount < LatencyCapacity) _actionLatencyCount++;
            }
        }

        public void RecordChatLatency(float elapsedMs)
        {
            lock (_chatLatencyLock)
            {
                _chatLatenciesMs[_chatLatencyCursor] = elapsedMs;
                _chatLatencyCursor = (_chatLatencyCursor + 1) % LatencyCapacity;
                if (_chatLatencyCount < LatencyCapacity) _chatLatencyCount++;
            }
        }

        public void CopyActionLatenciesInto(List<float> target)
        {
            lock (_actionLatencyLock)
            {
                for (int i = 0; i < _actionLatencyCount; i++)
                {
                    target.Add(_actionLatenciesMs[i]);
                }
            }
        }
    }

    // Modul: thread-safe aggregation across up to 1000 concurrent bot
    // tasks - plain counters via Interlocked, keyed breakdowns via
    // ConcurrentDictionary, matching FolkIdle.ChaosTester's identical
    // aggregation approach.
    internal sealed class LoadTestStats
    {
        public int ConnectionAttempts;
        public int SuccessfulHandshakes;
        public int ActionsSent;
        public int ActionsConfirmed;
        public int ActionsTimedOut;
        public int ChatSent;
        public int ChatRoundTripped;

        private readonly ConcurrentDictionary<string, int> _failureReasonCounts = new();
        public IReadOnlyDictionary<string, int> FailureReasonCounts => _failureReasonCounts;

        private readonly ConcurrentDictionary<string, int> _actionKindCounts = new();
        public IReadOnlyDictionary<string, int> ActionKindCounts => _actionKindCounts;

        public void RecordFailure(string reason)
        {
            _failureReasonCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
        }

        public void RecordActionKind(string kind)
        {
            _actionKindCounts.AddOrUpdate(kind, 1, (_, count) => count + 1);
        }
    }

    public sealed class LoadTestOptions
    {
        public int BotCount = 1000;
        public string ServerBaseUrl = "http://localhost:8080";
        public int DurationSeconds = 60;
        public int RampConcurrency = 200;
        public int ActionIntervalMinMs = 100;
        public int ActionIntervalMaxMs = 500;
        public int ReportIntervalSeconds = 5;
        public int ConfirmationTimeoutMs = 5000;

        public string WebSocketBaseUrl => "ws" + ServerBaseUrl.Substring(ServerBaseUrl.IndexOf("://", StringComparison.Ordinal));

        public static LoadTestOptions Parse(string[] args)
        {
            var options = new LoadTestOptions();

            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--bots":
                        if (int.TryParse(args[i + 1], out int bots)) options.BotCount = bots;
                        break;
                    case "--server":
                        options.ServerBaseUrl = args[i + 1].TrimEnd('/');
                        break;
                    case "--duration":
                        if (int.TryParse(args[i + 1], out int duration)) options.DurationSeconds = duration;
                        break;
                    case "--ramp-concurrency":
                        if (int.TryParse(args[i + 1], out int ramp)) options.RampConcurrency = ramp;
                        break;
                    case "--action-interval-min-ms":
                        if (int.TryParse(args[i + 1], out int intervalMin)) options.ActionIntervalMinMs = intervalMin;
                        break;
                    case "--action-interval-max-ms":
                        if (int.TryParse(args[i + 1], out int intervalMax)) options.ActionIntervalMaxMs = intervalMax;
                        break;
                    case "--report-interval-seconds":
                        if (int.TryParse(args[i + 1], out int reportInterval)) options.ReportIntervalSeconds = reportInterval;
                        break;
                    case "--confirmation-timeout-ms":
                        if (int.TryParse(args[i + 1], out int confirmTimeout)) options.ConfirmationTimeoutMs = confirmTimeout;
                        break;
                }
            }

            return options;
        }
    }
}

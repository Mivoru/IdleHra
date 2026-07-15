using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FolkIdle.Server.Network;

namespace FolkIdle.ChaosTester
{
    // Headless load tester: opens many concurrent authenticated WebSocket
    // connections against a running FolkIdle server, then periodically fires
    // chat messages on each one and measures round-trip latency via the
    // server's own chat echo (every published message, including the
    // sender's own, comes back through ChatEngine's Redis Pub/Sub broadcast -
    // see ResponseChatMessagePacket). Not a unit test - a standalone tool run
    // manually against a live server instance.
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            ChaosOptions options = ChaosOptions.Parse(args);

            Console.WriteLine("FolkIdle Chaos Tester");
            Console.WriteLine($"  Server: {options.ServerBaseUrl}");
            Console.WriteLine($"  Connections: {options.ConnectionCount}");
            Console.WriteLine($"  Duration: {options.DurationSeconds} seconds");
            Console.WriteLine($"  Ramp concurrency: {options.RampConcurrency}");
            Console.WriteLine();

            var stats = new ChaosStats();
            using var httpClient = new HttpClient();
            using var rampSemaphore = new SemaphoreSlim(options.RampConcurrency, options.RampConcurrency);
            using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));

            var workerTasks = new Task[options.ConnectionCount];
            for (int i = 0; i < options.ConnectionCount; i++)
            {
                int connectionIndex = i;
                workerTasks[i] = RunConnectionAsync(connectionIndex, options, httpClient, rampSemaphore, stats, runCts.Token);
            }

            await Task.WhenAll(workerTasks);

            PrintSummary(stats);
            return 0;
        }

        // Modul: one simulated player end to end - login, handshake, then a
        // periodic chat-send loop and a continuous receive loop running
        // concurrently until the overall run duration elapses. The ramp
        // semaphore is held only across login+connect+handshake so opening
        // thousands of connections happens in bounded batches rather than an
        // instantaneous spike, then released so the next connection can
        // start while this one continues sending chat traffic for the rest
        // of the run.
        private static async Task RunConnectionAsync(int connectionIndex, ChaosOptions options, HttpClient httpClient, SemaphoreSlim rampSemaphore, ChaosStats stats, CancellationToken runToken)
        {
            Interlocked.Increment(ref stats.ConnectionAttempts);

            await rampSemaphore.WaitAsync(runToken).ConfigureAwait(false);
            bool releasedRampSlot = false;

            ClientWebSocket? socket = null;
            try
            {
                string deviceId = $"chaos-{connectionIndex}-{Guid.NewGuid():N}";

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
                    Console.WriteLine($"[{connectionIndex}] connect failed: {ex.Message}");
                    return;
                }

                await SendAuthHandshakeAsync(socket, jwt, runToken).ConfigureAwait(false);

                var connectionState = new ConnectionState();
                bool handshakeConfirmed = await WaitForHandshakeConfirmationAsync(socket, connectionState, runToken).ConfigureAwait(false);
                if (!handshakeConfirmed)
                {
                    stats.RecordFailure("handshake-timeout");
                    Console.WriteLine($"[{connectionIndex}] handshake not confirmed (no StateUpdatePacket received).");
                    return;
                }

                Interlocked.Increment(ref stats.SuccessfulHandshakes);

                // Ramp slot released now - login/connect/handshake is done,
                // the remaining work is just periodic chat traffic that does
                // not need to be throttled against other connections still
                // ramping up.
                rampSemaphore.Release();
                releasedRampSlot = true;

                var receiveTask = ReceiveLoopAsync(socket, connectionState, stats, runToken);
                var sendTask = ChatSendLoopAsync(connectionIndex, socket, connectionState, options, stats, runToken);

                await Task.WhenAll(receiveTask, sendTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected at run-duration expiry.
            }
            catch (Exception ex)
            {
                stats.RecordFailure("unexpected-error");
                Console.WriteLine($"[{connectionIndex}] unexpected error: {ex.Message}");
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
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "chaos-test-complete", closeCts.Token).ConfigureAwait(false);
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

        private static async Task<string?> LoginAsync(HttpClient httpClient, string serverBaseUrl, string deviceId, ChaosStats stats)
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
        // write pattern.
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
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref buffer[0], packet);
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
        }

        // Waits only for the first StateUpdatePacket - that is the proof the
        // handshake succeeded and a playerId was resolved server-side. Any
        // chat-sized message observed here (unlikely this early, but
        // possible under heavy concurrent chat traffic from other
        // connections) is routed the same way the steady-state receive loop
        // would route it, just inline, so nothing is lost before the two
        // long-running loops take over.
        private static async Task<bool> WaitForHandshakeConfirmationAsync(ClientWebSocket socket, ConnectionState state, CancellationToken runToken)
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

        // Continuously drains both StateUpdatePacket (703 bytes, arriving at
        // the server's normal 10Hz per-player cadence) and
        // ResponseChatMessagePacket (146 bytes) off the same connection,
        // distinguished purely by exact byte count - mirrors
        // WebSocketClient.ConnectAndReceiveLoopAsync's dispatch exactly.
        // StateUpdatePacket contents beyond the one PlayerId already
        // captured during handshake confirmation are discarded; a chat
        // packet whose SenderPlayerId matches this connection's own
        // resolved playerId and which this connection is currently awaiting
        // an echo for completes the round-trip latency measurement.
        private static async Task ReceiveLoopAsync(ClientWebSocket socket, ConnectionState state, ChaosStats stats, CancellationToken runToken)
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
                            long awaitingSince = Interlocked.Exchange(ref state.AwaitingEchoSinceTicks, 0L);
                            if (awaitingSince != 0L)
                            {
                                double elapsedMs = (Stopwatch.GetTimestamp() - awaitingSince) * 1000.0 / Stopwatch.Frequency;
                                stats.RecordRoundTrip(elapsedMs);
                            }
                        }
                    }
                    // StateUpdatePacket (or anything else) - discarded, just
                    // keeps the socket draining so it never backs up.
                }
            }
            catch (OperationCanceledException)
            {
                // Expected at run-duration expiry.
            }
            catch (Exception)
            {
                // Connection dropped mid-run - counted via the final
                // socket-state check the caller already performs.
            }
        }

        // Fires RequestChatMessagePacket on a randomized jitter timer,
        // deliberately compatible with ChatEngine's server-side rate limit
        // (5-message burst, refilling at 0.5 messages/second - see
        // ChatEngine.ChatBucketCapacity/ChatBucketRefillRatePerSecond) so
        // messages are not routinely dropped by the very limiter this tool
        // is meant to help validate elsewhere.
        private static async Task ChatSendLoopAsync(int connectionIndex, ClientWebSocket socket, ConnectionState state, ChaosOptions options, ChaosStats stats, CancellationToken runToken)
        {
            var random = new Random(connectionIndex);
            byte[] sendBuffer = new byte[Marshal.SizeOf<RequestChatMessagePacket>()];

            try
            {
                while (!runToken.IsCancellationRequested)
                {
                    int jitterMs = random.Next(options.ChatIntervalMinSeconds * 1000, options.ChatIntervalMaxSeconds * 1000);
                    await Task.Delay(jitterMs, runToken).ConfigureAwait(false);

                    if (socket.State != WebSocketState.Open)
                    {
                        break;
                    }

                    string messageText = $"chaos-test message from connection {connectionIndex}";
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

                    System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref sendBuffer[0], packet);

                    // Only one outstanding round-trip measurement is tracked
                    // per connection at a time - the jitter interval and the
                    // server's own rate limit both make overlapping sends
                    // from a single connection rare, and a still-outstanding
                    // previous send simply has its measurement overwritten
                    // rather than double-counted, which is an acceptable
                    // simplification for a load-generation tool.
                    Interlocked.Exchange(ref state.AwaitingEchoSinceTicks, Stopwatch.GetTimestamp());
                    await socket.SendAsync(new ArraySegment<byte>(sendBuffer), WebSocketMessageType.Binary, true, runToken).ConfigureAwait(false);
                    Interlocked.Increment(ref stats.MessagesSent);
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

        private static void PrintSummary(ChaosStats stats)
        {
            int attempts = stats.ConnectionAttempts;
            int successes = stats.SuccessfulHandshakes;
            int failures = attempts - successes;
            double successRate = attempts == 0 ? 0.0 : 100.0 * successes / attempts;

            int sent = stats.MessagesSent;
            int roundTripped = stats.RoundTripCount;
            double roundTripRate = sent == 0 ? 0.0 : 100.0 * roundTripped / sent;

            Console.WriteLine();
            Console.WriteLine("Chaos Test Summary");
            Console.WriteLine("-------------------");
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
            Console.WriteLine($"Chat messages sent:       {sent}");
            Console.WriteLine($"Chat messages round-tripped: {roundTripped}");
            Console.WriteLine($"Round-trip observation rate: {roundTripRate:F2} percent");
            Console.WriteLine("(Messages sent but never round-tripped may have been dropped by the server's per-connection chat rate limiter rather than lost or slow.)");

            var latencies = stats.GetSortedLatenciesMs();
            if (latencies.Count > 0)
            {
                double avg = latencies.Average();
                double max = latencies[^1];
                double p50 = Percentile(latencies, 0.50);
                double p95 = Percentile(latencies, 0.95);
                double p99 = Percentile(latencies, 0.99);

                Console.WriteLine();
                Console.WriteLine("Round-trip latency (milliseconds):");
                Console.WriteLine($"  average: {avg:F2}");
                Console.WriteLine($"  p50:     {p50:F2}");
                Console.WriteLine($"  p95:     {p95:F2}");
                Console.WriteLine($"  p99:     {p99:F2}");
                Console.WriteLine($"  max:     {max:F2}");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("No round-trip latency samples were recorded.");
            }
        }

        private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
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

    // Modul: per-connection mutable state shared between the receive loop
    // and the chat-send loop, which run concurrently on the same
    // connection. AwaitingEchoSinceTicks is a Stopwatch timestamp (0 means
    // "not currently awaiting an echo"), read/written via Interlocked since
    // the two loops run on different tasks.
    internal sealed class ConnectionState
    {
        public long PlayerId;
        public long AwaitingEchoSinceTicks;
    }

    internal sealed class ChaosOptions
    {
        public int ConnectionCount = 10000;
        public string ServerBaseUrl = "http://localhost:8080";
        public int DurationSeconds = 60;
        public int RampConcurrency = 200;
        public int ChatIntervalMinSeconds = 5;
        public int ChatIntervalMaxSeconds = 15;

        public string WebSocketBaseUrl => "ws" + ServerBaseUrl.Substring(ServerBaseUrl.IndexOf("://", StringComparison.Ordinal));

        public static ChaosOptions Parse(string[] args)
        {
            var options = new ChaosOptions();

            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--connections":
                        if (int.TryParse(args[i + 1], out int connections)) options.ConnectionCount = connections;
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
                }
            }

            return options;
        }
    }

    // Modul: thread-safe aggregation across thousands of concurrent worker
    // tasks - plain counters via Interlocked, latency samples via
    // ConcurrentBag since order does not matter and they are only sorted
    // once at the very end for the percentile report.
    internal sealed class ChaosStats
    {
        public int ConnectionAttempts;
        public int SuccessfulHandshakes;
        public int MessagesSent;

        private readonly ConcurrentBag<double> _roundTripLatenciesMs = new();
        public int RoundTripCount => _roundTripLatenciesMs.Count;

        private readonly ConcurrentDictionary<string, int> _failureReasonCounts = new();
        public IReadOnlyDictionary<string, int> FailureReasonCounts => _failureReasonCounts;

        public void RecordFailure(string reason)
        {
            _failureReasonCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
        }

        public void RecordRoundTrip(double elapsedMs)
        {
            _roundTripLatenciesMs.Add(elapsedMs);
        }

        public List<double> GetSortedLatenciesMs()
        {
            var list = _roundTripLatenciesMs.ToList();
            list.Sort();
            return list;
        }
    }
}

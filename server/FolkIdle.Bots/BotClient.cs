using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// One signed-in guest: the REST bearer and the live WebSocket, spoken
    /// exactly the way the web client speaks them (connection.ts): a JSON
    /// handshake with mode "json", ClientCommand packets stamped with the last
    /// epoch seen, and the anti-cheat challenge answered when a snapshot
    /// carries one.
    ///
    /// Modul: THE SERVER'S OWN TYPES, NOT A COPY. Packets go through
    /// PacketJsonCodec and the challenge through
    /// AntiCheatTelemetryEngine.ComputeChallengeHash - the same code the server
    /// validates with - so a struct change reaches the bot at compile time
    /// instead of drifting (two sources of one truth is this codebase's
    /// dominant bug class). REST answers are read as JsonElement by property
    /// name rather than mirrored into DTOs for the same reason.
    /// </summary>
    public sealed class BotClient : IAsyncDisposable
    {
        private readonly string _baseUrl;
        private readonly HttpClient _http;
        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _stateLock = new();
        private StateUpdatePacket _state;
        private bool _hasState;
        private uint _answeredChallenge;
        private long _stateFrames;

        public string DeviceId { get; }
        public string Token { get; private set; } = string.Empty;

        public BotClient(string baseUrl, string deviceId)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            DeviceId = deviceId;
            _http = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>State frames received so far (the server sends one per broadcast).</summary>
        public long StateFrames => Interlocked.Read(ref _stateFrames);

        private long _commandsSent;

        /// <summary>Gameplay commands sent so far - a lockstep bot that sent one wakes again after a reaction, not after its chore timers.</summary>
        public long CommandsSent => Interlocked.Read(ref _commandsSent);

        public bool TryGetState(out StateUpdatePacket state)
        {
            lock (_stateLock)
            {
                state = _state;
                return _hasState;
            }
        }

        /// <summary>
        /// Modul: A THROTTLED LOGIN IS RETRIED, THE THROTTLE IS NOT BYPASSED
        /// (night 2026-11-04, val-x20). Twenty bots signing in in the same
        /// second met AuthThrottle (15 auth requests a minute per address) and
        /// five of them ended on "429 Too Many Requests" before their first
        /// fight, so the reference run had 15 bots. The throttle is a real
        /// production defence and stays on in dev; the bot honours Retry-After
        /// instead, with an exponential backoff and jitter so the retries spread
        /// out rather than arriving together again. Waits are WALL time: the
        /// budget is wall time. Under lockstep the bot holds its turn while it
        /// waits, so no game time passes and the run's results do not move.
        /// </summary>
        public const int LoginAttempts = 8;

        /// <summary>Logins this client retried after a 429.</summary>
        public int LoginRetries { get; private set; }

        /// <summary>The larger of Retry-After and 2^(attempt-1) s, at most 120 s, plus up to 25% jitter.</summary>
        public static TimeSpan LoginBackoff(int attempt, TimeSpan? retryAfter, double jitter01)
        {
            double exponential = Math.Min(120, Math.Pow(2, Math.Max(0, attempt - 1)));
            double seconds = Math.Max(retryAfter?.TotalSeconds ?? 1, exponential);
            return TimeSpan.FromSeconds(seconds * (1 + 0.25 * Math.Clamp(jitter01, 0, 1)));
        }

        /// <summary>
        /// Signs in and opens the session. Paced runs wait here (wall time) for
        /// the first snapshot. A lockstep run must NOT: the login completes on
        /// the server's ticks, which do not run while this bot holds its turn,
        /// so <paramref name="waitForState"/> false returns at once and the bot
        /// yields game time until the snapshot arrives.
        /// </summary>
        public async Task ConnectAsync(CancellationToken token, bool waitForState = true)
        {
            for (int attempt = 1; ; attempt++)
            {
                using var response = await _http.PostAsJsonAsync("/api/v1/auth/login", new { deviceId = DeviceId }, token);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < LoginAttempts)
                {
                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                    TimeSpan wait = LoginBackoff(attempt, retryAfter, Random.Shared.NextDouble());
                    LoginRetries++;
                    Console.WriteLine($"[{DeviceId}] login throttled (429, Retry-After {retryAfter?.TotalSeconds.ToString("0") ?? "none"} s): attempt {attempt} of {LoginAttempts}, next in {wait.TotalSeconds:0.#} wall s");
                    await Task.Delay(wait, token);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                Token = body.RootElement.GetProperty("Token").GetString() ?? throw new InvalidOperationException("login returned no token");
                break;
            }
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            string wsUrl = "ws" + _baseUrl.Substring(_baseUrl.IndexOf("://", StringComparison.Ordinal)) + "/";
            await _socket.ConnectAsync(new Uri(wsUrl), token);

            byte[] jwt = Encoding.UTF8.GetBytes(Token);
            string handshake = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [PacketJsonCodec.TypePropertyName] = PacketJsonCodec.TypeAuthHandshake,
                [PacketJsonCodec.ModePropertyName] = PacketJsonCodec.ModeJson,
                ["JwtToken"] = Convert.ToBase64String(jwt),
                ["JwtTokenLength"] = jwt.Length,
                ["AssetHash"] = 0,
                ["PlatformSignature"] = 0,
            });
            await SendTextAsync(handshake, token);
            _ = Task.Run(() => ReceiveLoopAsync(token), token);
            if (!waitForState) return;

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!TryGetState(out _))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"{DeviceId}: no StateUpdate within 15 s of the handshake");
                await Task.Delay(100, token);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var bytes = new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length);
                    HandleMessage(bytes);
                    message.SetLength(0);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"[{DeviceId}] socket closed: {ex.Message} (state {_socket.State}, close {_socket.CloseStatus} '{_socket.CloseStatusDescription}', frames {StateFrames})");
            }
        }

        private void HandleMessage(ReadOnlyMemory<byte> bytes)
        {
            if (!PacketJsonCodec.TryParseEnvelope(bytes, out JsonDocument? doc, out string type, out _) || doc == null) return;
            using (doc)
            {
                if (type != PacketJsonCodec.TypeStateUpdate) return;
                if (!PacketJsonCodec.TryRead(doc.RootElement, out StateUpdatePacket packet, out _)) return;
                lock (_stateLock)
                {
                    _state = packet;
                    _hasState = true;
                }
                Interlocked.Increment(ref _stateFrames);

                // Judged against the epoch the challenge was ISSUED under - the
                // one on this very packet (connection.ts answerChallengeIfIssued).
                uint seed = packet.ActiveChallengeSeed;
                if (seed != 0 && seed != _answeredChallenge)
                {
                    _answeredChallenge = seed;
                    var answer = new ClientCommandPacket
                    {
                        Command = CommandType.AntiCheatChallengeResponse,
                        ChallengeId = seed,
                        ChallengeVerificationHash = AntiCheatTelemetryEngine.ComputeChallengeHash(seed, packet.PlayerId, packet.LogicEpochCounter),
                        LogicEpochCounter = packet.LogicEpochCounter,
                    };
                    _ = SendPacketAsync(answer, CancellationToken.None);
                }
            }
        }

        /// <summary>Sends a command stamped with the newest epoch, as connection.ts send() does.</summary>
        public Task SendCommandAsync(ClientCommandPacket packet, CancellationToken token)
        {
            Interlocked.Increment(ref _commandsSent);
            if (TryGetState(out var state)) packet.LogicEpochCounter = state.LogicEpochCounter;
            return SendPacketAsync(packet, token);
        }

        private Task SendPacketAsync(ClientCommandPacket packet, CancellationToken token)
            => SendTextAsync(Encoding.UTF8.GetString(PacketJsonCodec.SerializeToUtf8(ref packet)), token);

        private async Task SendTextAsync(string text, CancellationToken token)
        {
            await _sendLock.WaitAsync(token);
            try
            {
                if (_socket.State != WebSocketState.Open) return;
                await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public bool IsOpen => _socket.State == WebSocketState.Open;

        public async Task<JsonElement?> GetAsync(string path, CancellationToken token)
        {
            using var response = await _http.GetAsync(path, token);
            if (!response.IsSuccessStatusCode) return null;
            string text = await response.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        /// <summary>
        /// Modul: PROPERTY NAMES AS WRITTEN, NOT camelCase (2026-11-01).
        /// PostAsJsonAsync uses the web defaults, which turn "Scenarios" into
        /// "scenarios" - and the dungeon, Delve and what-if handlers read their
        /// bodies with exact-case TryGetProperty, so every such POST arrived
        /// empty and was answered as if nothing had been asked. The web client
        /// sends PascalCase (rest.ts); so does the bot now.
        /// </summary>
        private static readonly JsonSerializerOptions BodyJson = new();

        public async Task<(int Status, JsonElement? Body)> PostAsync(string path, object body, CancellationToken token)
        {
            using var response = await _http.PostAsJsonAsync(path, body, BodyJson, token);
            string text = await response.Content.ReadAsStringAsync(token);
            if (string.IsNullOrWhiteSpace(text)) return ((int)response.StatusCode, null);
            try
            {
                using var doc = JsonDocument.Parse(text);
                return ((int)response.StatusCode, doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                return ((int)response.StatusCode, null);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bot done", cts.Token);
                }
            }
            catch { }
            _socket.Dispose();
            _http.Dispose();
        }
    }
}

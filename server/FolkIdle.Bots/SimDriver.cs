using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolkIdle.Bots
{
    /// <summary>
    /// The bot process's side of the lockstep control socket (/api/v1/dev/sim,
    /// docs/design/LOCKSTEP_SIM.md, phase 2).
    ///
    /// Modul: EVERY WAIT IS A YIELD. Each bot keeps its ordinary async code; a
    /// wait inside it (its turn ending, a swipe's reaction, a login settling)
    /// goes through <see cref="LockstepBotTime"/>, which hands the server a wake
    /// tick and parks the bot until the server wakes it. The driver collects
    /// the yields of every bot woken in one frame and answers with one frame, so
    /// a busy game second is one round trip. Bots woken together run in
    /// parallel; the world waits for the slowest.
    /// </summary>
    public sealed class SimDriver : IAsyncDisposable
    {
        private const int TickMs = 100;

        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _gate = new();
        private readonly Dictionary<string, LockstepBotTime> _bots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long NextWakeTick, BotWake WakeOn)> _yields = new(StringComparer.Ordinal);
        private HashSet<string> _awaitingYield = new(StringComparer.Ordinal);
        private long _wakeTick;
        private Task? _receiveLoop;
        private TaskCompletionSource _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Tick { get; private set; }
        public long StartTick { get; private set; }
        public DateTime AnchorUtc { get; private set; }
        public int Quantum { get; private set; } = 10;
        public long Wakes { get; private set; }

        public double GameHours => (Tick - StartTick) * TickMs / 3_600_000.0;
        public DateTime GameUtc => AnchorUtc.AddMilliseconds(Tick * (double)TickMs);

        public async Task ConnectAsync(string server, CancellationToken token)
        {
            string ws = "ws" + server.Substring(server.IndexOf("://", StringComparison.Ordinal)) + "/api/v1/dev/sim";
            await _socket.ConnectAsync(new Uri(ws), token);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(token), token);
            await _hello.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }

        /// <summary>Registers a bot; its clock's first SleepAsync returns at the server's join wake.</summary>
        public LockstepBotTime Join(string bot, int reactionMinTicks, int reactionMaxTicks)
        {
            var time = new LockstepBotTime(this, bot);
            lock (_gate) _bots[bot] = time;
            _ = SendAsync(new { Type = "join", Bot = bot, ReactionMin = reactionMinTicks, ReactionMax = reactionMaxTicks });
            return time;
        }

        internal void Bind(string bot, long playerId)
            => _ = SendAsync(new { Type = "bind", Bot = bot, PlayerId = playerId });

        internal long TicksFromNow(TimeSpan gameTime) => Tick + Math.Max(1, (long)Math.Ceiling(gameTime.TotalMilliseconds / TickMs));

        internal long TickAt(double gameHours) => StartTick + (long)Math.Ceiling(gameHours * 3_600_000.0 / TickMs);

        /// <summary>A bot finished its turn. When the last bot of this wake has, the yield frame goes out.</summary>
        internal void OnYield(string bot, long nextWakeTick, BotWake wakeOn)
        {
            object? frame = null;
            lock (_gate)
            {
                _yields[bot] = (Math.Max(nextWakeTick, Tick + 1), wakeOn);
                _awaitingYield.Remove(bot);
                if (_awaitingYield.Count == 0 && _yields.Count > 0)
                {
                    frame = BuildYieldFrameLocked();
                }
            }
            if (frame != null) _ = SendAsync(frame);
        }

        internal void OnLeave(string bot)
        {
            object? frame = null;
            lock (_gate)
            {
                _bots.Remove(bot);
                _awaitingYield.Remove(bot);
                _yields.Remove(bot);
                if (_awaitingYield.Count == 0 && _yields.Count > 0) frame = BuildYieldFrameLocked();
            }
            _ = SendAsync(new { Type = "leave", Bot = bot });
            if (frame != null) _ = SendAsync(frame);
        }

        private object BuildYieldFrameLocked()
        {
            var bots = _yields.Select(kv => new { Bot = kv.Key, NextWakeTick = kv.Value.NextWakeTick, WakeOn = (int)kv.Value.WakeOn }).ToList();
            _yields.Clear();
            return new { Type = "yield", Bots = bots };
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            try
            {
                while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    var r = await _socket.ReceiveAsync(buffer, token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    Handle(text.ToString());
                    text.Clear();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
            Console.WriteLine("[SimDriver] control socket closed");
        }

        private void Handle(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("Type").GetString())
            {
                case "hello":
                    Tick = root.GetProperty("Tick").GetInt64();
                    StartTick = Tick;
                    Quantum = root.GetProperty("Quantum").GetInt32();
                    AnchorUtc = root.TryGetProperty("AnchorUtc", out var a) && a.TryGetDateTime(out var at)
                        ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                        : DateTime.UtcNow;
                    _hello.TrySetResult();
                    break;
                case "wake":
                {
                    long tick = root.GetProperty("Tick").GetInt64();
                    var woken = new List<(LockstepBotTime Time, BotWake Reasons)>();
                    lock (_gate)
                    {
                        // The bots see the world as it stood after the last
                        // completed tick: the wake tick has not run yet.
                        Tick = tick - 1;
                        _wakeTick = tick;
                        Wakes++;
                        _awaitingYield = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var b in root.GetProperty("Bots").EnumerateArray())
                        {
                            string name = b.GetProperty("Bot").GetString() ?? "";
                            if (!_bots.TryGetValue(name, out var time))
                            {
                                // Not ours any more: answer for it so the world moves.
                                _yields[name] = (long.MaxValue / 4, BotWake.None);
                                continue;
                            }
                            _awaitingYield.Add(name);
                            woken.Add((time, (BotWake)b.GetProperty("Reasons").GetInt32()));
                        }
                    }
                    if (woken.Count == 0)
                    {
                        object? frame;
                        lock (_gate) frame = _yields.Count > 0 ? BuildYieldFrameLocked() : null;
                        if (frame != null) _ = SendAsync(frame);
                    }
                    foreach (var (time, reasons) in woken) time.Wake(reasons);
                    break;
                }
            }
        }

        private async Task SendAsync(object frame)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
            await _sendLock.WaitAsync();
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"[SimDriver] send failed: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "run over", cts.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
            _socket.Dispose();
        }
    }

    /// <summary>One bot's lockstep clock: every wait is a yield to the conductor.</summary>
    public sealed class LockstepBotTime : IBotTime
    {
        private readonly SimDriver _driver;
        private readonly string _bot;
        private TaskCompletionSource<BotWake> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _left;

        internal LockstepBotTime(SimDriver driver, string bot)
        {
            _driver = driver;
            _bot = bot;
        }

        public bool IsLockstep => true;
        public double GameHours => _driver.GameHours;
        public DateTime GameUtc => _driver.GameUtc;

        /// <summary>Completes at the join wake: the bot's first turn.</summary>
        public Task<BotWake> FirstTurn => _wake.Task;

        internal void Wake(BotWake reasons) => _wake.TrySetResult(reasons);

        public async Task DelayAsync(TimeSpan gameTime, CancellationToken token)
            => await YieldAsync(_driver.TicksFromNow(gameTime), BotWake.None, token);

        public Task<BotWake> SleepAsync(double untilGameHours, BotWake wakeOn, CancellationToken token)
            => YieldAsync(Math.Max(_driver.TickAt(untilGameHours), _driver.Tick + 1), wakeOn, token);

        private async Task<BotWake> YieldAsync(long tick, BotWake wakeOn, CancellationToken token)
        {
            if (_left) return BotWake.None;
            _wake = new TaskCompletionSource<BotWake>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wake = _wake.Task;
            _driver.OnYield(_bot, tick, wakeOn);
            return await wake.WaitAsync(token);
        }

        public void Bind(long playerId) => _driver.Bind(_bot, playerId);

        public void Leave()
        {
            if (_left) return;
            _left = true;
            _driver.OnLeave(_bot);
        }
    }
}

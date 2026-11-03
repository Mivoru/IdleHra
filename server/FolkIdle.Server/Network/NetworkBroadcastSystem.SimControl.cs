using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    public partial class NetworkBroadcastSystem
    {
        /// <summary>
        /// GET /api/v1/dev/sim (WebSocket) - the lockstep control socket, dev only
        /// (docs/design/LOCKSTEP_SIM.md). One per bot process, carrying all its
        /// bots, because a bot is LOGGED OUT between check-ins and its wake cannot
        /// travel on a game session it does not have.
        ///
        /// Bot process -> server, one JSON text frame each:
        ///   { "Type": "join",  "Bot": "optimal1", "ReactionMin": 4, "ReactionMax": 12 }
        ///   { "Type": "bind",  "Bot": "optimal1", "PlayerId": 123 }
        ///   { "Type": "yield", "Bots": [ { "Bot": "optimal1", "NextWakeTick": 5400,
        ///                                  "WakeOn": 12, "ReactionMin": 4, "ReactionMax": 12 } ] }
        ///   { "Type": "leave", "Bot": "optimal1" }
        /// Server -> bot process:
        ///   { "Type": "hello", "Tick": 0, "Quantum": 10, "TickMs": 100, "AnchorUtc": "..." }
        ///   { "Type": "wake",  "Tick": 5400, "Bots": [ { "Bot": "optimal1", "Reasons": 1 } ] }
        ///
        /// A wake frame lists every bot due at that tick; the process answers
        /// with ONE yield frame for all of them, so a busy game second costs one
        /// round trip, not one per bot. The tick does not run until every woken
        /// bot has yielded or left. The socket closing makes every bot leave, so
        /// a crashed bot process can never freeze the world.
        /// </summary>
        private async Task HandleSimControlSocketAsync(HttpListenerContext context)
        {
            var conductor = SimLockstep.Conductor;
            if (!DevToolsEnabled() || conductor == null)
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;
            var outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            var joined = new HashSet<string>(StringComparer.Ordinal);

            // Only one bot process drives the world at a time; a second one
            // replaces the first's sink, and the first's bots time out of it.
            SimLockstep.WakeSink = (tick, wakes) =>
            {
                var bots = new List<object>(wakes.Count);
                foreach (var w in wakes) bots.Add(new { Bot = w.Bot, Reasons = (int)w.Reasons });
                outbox.Writer.TryWrite(JsonSerializer.Serialize(new { Type = "wake", Tick = tick, Bots = bots }));
            };
            Console.WriteLine("[SimControl] bot process connected");

            var writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var text in outbox.Reader.ReadAllAsync())
                    {
                        if (socket.State != WebSocketState.Open) break;
                        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
                {
                }
            });

            outbox.Writer.TryWrite(JsonSerializer.Serialize(new { Type = "hello", Tick = conductor.Tick, Quantum = conductor.QuantumTicks, TickMs = 100, AnchorUtc = GameClock.LockstepAnchorUtc }));

            var buffer = new byte[64 * 1024];
            var message = new StringBuilder();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;
                    string text = message.ToString();
                    message.Clear();
                    try
                    {
                        HandleSimControlMessage(conductor, text, joined);
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine($"[SimControl] bad frame ignored: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
            }
            finally
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    // Finish the close handshake, or the bot process waits on it.
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                    catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
                }
                foreach (var bot in joined) conductor.Leave(bot);
                outbox.Writer.TryComplete();
                await writer;
                Console.WriteLine($"[SimControl] bot process disconnected; {joined.Count} bots left the world at tick {conductor.Tick}");
            }
        }

        internal static void HandleSimControlMessage(SimConductor conductor, string text, HashSet<string> joined)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string type = root.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "join":
                {
                    string bot = root.GetProperty("Bot").GetString() ?? "";
                    if (bot.Length == 0) return;
                    conductor.Join(bot,
                        OptionalInt(root, "ReactionMin") ?? SimConductor.DefaultReactionMinTicks,
                        OptionalInt(root, "ReactionMax") ?? SimConductor.DefaultReactionMaxTicks);
                    joined.Add(bot);
                    break;
                }
                case "bind":
                    conductor.Bind(root.GetProperty("Bot").GetString() ?? "", root.GetProperty("PlayerId").GetInt64());
                    break;
                case "leave":
                {
                    string bot = root.GetProperty("Bot").GetString() ?? "";
                    conductor.Leave(bot);
                    joined.Remove(bot);
                    break;
                }
                case "yield":
                    foreach (var y in root.GetProperty("Bots").EnumerateArray())
                    {
                        conductor.Yield(
                            y.GetProperty("Bot").GetString() ?? "",
                            y.GetProperty("NextWakeTick").GetInt64(),
                            (SimWakeReason)(OptionalInt(y, "WakeOn") ?? 0),
                            OptionalInt(y, "ReactionMin"),
                            OptionalInt(y, "ReactionMax"));
                    }
                    break;
                default:
                    Console.WriteLine($"[SimControl] unknown frame type '{type}' ignored");
                    break;
            }
        }

        private static int? OptionalInt(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    }
}

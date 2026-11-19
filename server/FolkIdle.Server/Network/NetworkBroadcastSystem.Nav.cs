using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // The Descent into Nav (2026-11-19): /api/v1/nav* - see NavRules, NavSimulator
    // and NavEngine. REST like the dungeon: a run is one request the server
    // decides and writes. Every route answers 404 unless FOLKIDLE_NAV=on, which
    // is how production stays closed until the owner decides on Souls and the
    // gold research - and how the client knows to hide the screen.
    public partial class NetworkBroadcastSystem
    {
        private async Task<bool> TryHandleNavAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/nav", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            try
            {
                if (!NavRules.Enabled()) { context.Response.StatusCode = 404; return true; }
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return true; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                long now = GameClock.UtcNowOffset.ToUnixTimeSeconds();

                if (requestPath == "/api/v1/nav" && method == "GET")
                {
                    var row = await NavEngine.LoadAsync(db, playerId, now);
                    await db.SaveChangesAsync();
                    var snapshot = await NavSnapshotAsync(playerId);
                    var roster = snapshot == null ? Array.Empty<object>() : Enumerable.Range(0, 3)
                        .Where(slot => SlotHoldsCharacter(snapshot.Value.Payload, slot))
                        .Select(slot =>
                        {
                            var power = DungeonRunner.ChampionPower(snapshot.Value.Payload, slot);
                            return (object)new { Slot = slot, power.Dps, power.MaxHp, power.Worn };
                        }).ToArray();
                    bool open = snapshot == null || NavOpenFor(playerId, snapshot.Value.Payload.DefeatedRegionBossMask);
                    await WriteJsonAsync(context, NavEngine.View(row, now, roster, open, open ? string.Empty : NavRules.LockedReason));
                    return true;
                }

                if (requestPath == "/api/v1/nav/descend" && method == "POST")
                {
                    int slot, circle;
                    try
                    {
                        using var parsed = JsonDocument.Parse(await ReadBodyOrEmptyAsync(context));
                        slot = Math.Clamp(IntOf(parsed.RootElement, "Slot"), 0, 2);
                        circle = Math.Max(1, IntOf(parsed.RootElement, "Circle"));
                    }
                    catch (JsonException) { context.Response.StatusCode = 400; return true; }

                    var snapshot = await NavSnapshotAsync(playerId);
                    if (snapshot == null) { await WriteJsonAsync(context, new { Result = NavDescendResult.Offline.ToString() }); return true; }
                    if (!NavOpenFor(playerId, snapshot.Value.Payload.DefeatedRegionBossMask)) { await WriteJsonAsync(context, new { Result = NavDescendResult.Locked.ToString() }); return true; }
                    if (!SlotHoldsCharacter(snapshot.Value.Payload, slot)) { await WriteJsonAsync(context, new { Result = NavDescendResult.NoCharacter.ToString() }); return true; }

                    var (result, run, rewards) = await NavEngine.DescendAsync(db, playerId, snapshot.Value.Payload, slot, circle, now, GameClock.UtcNow, Random.Shared.Next());
                    await WriteJsonAsync(context, new { Result = result.ToString(), Run = run == null ? null : NavEngine.ViewOf(run, rewards) });
                    return true;
                }

                if (requestPath == "/api/v1/nav/workshop" && method == "POST")
                {
                    string key;
                    int levels;
                    try
                    {
                        using var parsed = JsonDocument.Parse(await ReadBodyOrEmptyAsync(context));
                        key = parsed.RootElement.TryGetProperty("Key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? string.Empty : string.Empty;
                        levels = Math.Max(1, IntOf(parsed.RootElement, "Levels"));
                    }
                    catch (JsonException) { context.Response.StatusCode = 400; return true; }

                    var (result, level) = await NavEngine.BuyAsync(db, playerId, key, levels, now);
                    await WriteJsonAsync(context, new { Result = result.ToString(), Level = level });
                    return true;
                }

                if (requestPath == "/api/v1/nav/research" && method == "POST")
                {
                    string key;
                    try
                    {
                        using var parsed = JsonDocument.Parse(await ReadBodyOrEmptyAsync(context));
                        key = parsed.RootElement.TryGetProperty("Key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? string.Empty : string.Empty;
                    }
                    catch (JsonException) { context.Response.StatusCode = 400; return true; }

                    var (result, gold) = await NavEngine.ResearchAsync(db, playerId, key, now);
                    if (result == NavResearchResult.Started)
                    {
                        // The gold left the database; the live wallet follows on reload.
                        CommandQueue.Enqueue(new PlayerCommand { PlayerId = playerId, Packet = new ClientCommandPacket { Command = CommandType.ReloadState } });
                    }
                    await WriteJsonAsync(context, new { Result = result.ToString(), Gold = gold });
                    return true;
                }

                if (requestPath == "/api/v1/nav/dev/open" && method == "POST")
                {
                    if (!DevToolsEnabled()) { context.Response.StatusCode = 404; return true; }
                    _devNavOpen[playerId] = 1;
                    await WriteJsonAsync(context, new { Ok = true });
                    return true;
                }

                if (requestPath == "/api/v1/nav/dev/hurry" && method == "POST")
                {
                    if (!DevToolsEnabled()) { context.Response.StatusCode = 404; return true; }
                    await NavEngine.DevHurryAsync(db, playerId, now);
                    await WriteJsonAsync(context, new { Ok = true });
                    return true;
                }

                context.Response.StatusCode = 404;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Nav error: {ex}");
                context.Response.StatusCode = 500;
                return true;
            }
            finally
            {
                context.Response.Close();
            }
        }

        // Modul: DEV ONLY - Nav counted as open for this player, in memory and
        // never written. The dev fixture has not beaten Malakor; exercise.mjs
        // opens it here, as it opens the dungeon (_devDungeonBossMask).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _devNavOpen = new();

        private static bool NavOpenFor(long playerId, byte defeatedRegionBossMask)
            => NavRules.IsOpen(defeatedRegionBossMask) || (DevToolsEnabled() && _devNavOpen.ContainsKey(playerId));

        private static async Task<string> ReadBodyOrEmptyAsync(HttpListenerContext context)
        {
            string body = await ReadBodyAsync(context);
            return string.IsNullOrWhiteSpace(body) ? "{}" : body;
        }

        /// <summary>A copy of the live payload, slots untouched, or null offline.</summary>
        private async Task<PayloadSnapshot?> NavSnapshotAsync(long playerId)
        {
            if (_playerSessionRegistry == null) return null;
            var order = new PayloadSnapshotOrder { PlayerId = playerId };
            _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
            var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
            return finished == order.Completion.Task ? order.Completion.Task.Result : null;
        }
    }
}

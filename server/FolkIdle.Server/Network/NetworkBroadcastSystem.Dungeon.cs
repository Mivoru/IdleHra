using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    public partial class NetworkBroadcastSystem
    {
        // The equipment dungeon (CHESTS_DUNGEONS_DIAMONDS.md 2.6a). REST, like
        // the Delve and the loot chests: a run is one request that the server
        // decides and writes; nothing is added to StateUpdatePacket.
        // Modul: HIDDEN FROM PLAYERS (owner, 2026-10-31: "far from how I want it
        // to look"). Only the admin accounts may open or run it; everyone else
        // gets a closed lobby and a refused run, so a remembered screen or an
        // old client cannot spend a ticket. The code, tables and tickets stay;
        // delete this gate when the rework lands.
        private const string DungeonClosedReason = "The dungeon is closed while it is being rebuilt.";

        private async Task<bool> TryHandleDungeonAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/dungeon", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            if (requestPath == "/api/v1/dungeon" && method == "GET") { await HandleDungeonView(context); return true; }
            if (requestPath == "/api/v1/dungeon/run" && method == "POST") { await HandleDungeonRun(context); return true; }
            return false;
        }

        /// <summary>A copy of the live payload with the main character in the active register, or null offline.</summary>
        private async Task<TickStatePayload?> MainCharacterSnapshotAsync(long playerId)
        {
            if (_playerSessionRegistry == null) return null;
            var order = new PayloadSnapshotOrder { PlayerId = playerId };
            _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
            var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
            var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
            if (snapshot == null) return null;
            var payload = snapshot.Value.Payload;
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 0);
            return payload;
        }

        private async Task HandleDungeonView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null) { context.Response.StatusCode = 404; return; }
                if (!IsAdmin(player))
                {
                    await WriteJsonAsync(context, new { Open = false, LockedReason = DungeonClosedReason, Stages = Array.Empty<object>() });
                    return;
                }
                if (DungeonEngine.TopUpTickets(player, DateTime.UtcNow)) await db.SaveChangesAsync();

                var snapshot = await MainCharacterSnapshotAsync(playerId);
                byte mask = snapshot?.DefeatedRegionBossMask ?? 0;
                bool open = DungeonEngine.IsOpen(mask);
                await WriteJsonAsync(context, new
                {
                    Open = open,
                    LockedReason = open ? string.Empty : DungeonEngine.StageLockedReason(1, mask, 0) ?? string.Empty,
                    Online = snapshot != null,
                    Tickets = player.DungeonTickets,
                    DungeonEngine.TicketCap,
                    DungeonEngine.TicketsPerDay,
                    BestStage = player.DungeonBestStage,
                    Boss = "Baba Yaga",
                    DungeonSimulator.Waves,
                    DungeonSimulator.MealCap,
                    Stages = Enumerable.Range(1, DungeonSimulator.StageCount).Select(stage => new
                    {
                        Stage = stage,
                        Band = DungeonEngine.BandOf(stage),
                        LockedReason = DungeonEngine.StageLockedReason(stage, mask, player.DungeonBestStage) ?? string.Empty,
                        Cleared = stage <= player.DungeonBestStage,
                        FirstClearPaid = (player.DungeonFirstClearMask & (1 << stage)) != 0,
                        Reward = DungeonEngine.RewardLine(stage),
                        Abilities = DungeonSimulator.AbilitiesAt(stage),
                    }).ToArray(),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dungeon view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleDungeonRun(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int stage = 0;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (parsed.RootElement.TryGetProperty("Stage", out var s) && s.TryGetInt32(out int sv)) stage = sv;
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using (var gateScope = _serviceProvider.CreateScope())
                {
                    var gateDb = gateScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    var gatePlayer = await gateDb.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
                    if (!IsAdmin(gatePlayer))
                    {
                        await WriteJsonAsync(context, new { Outcome = "Locked", Reason = DungeonClosedReason });
                        return;
                    }
                }

                // The fight is the live character's: no session, no run.
                var snapshot = await MainCharacterSnapshotAsync(playerId);
                if (snapshot == null)
                {
                    context.Response.StatusCode = 409;
                    await WriteJsonAsync(context, new { Outcome = "NoSession", Reason = "Your character is not online - reconnect and try again." });
                    return;
                }

                var food = DungeonEngine.BestFood(snapshot.Value);
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var (outcome, reason, result, reward, tickets) = await DungeonEngine.RunAsync(
                    db, playerId, stage, snapshot.Value, food.ItemId, food.Count, DateTime.UtcNow, Random.Shared);

                // Meals eaten come out of the live larder, as a delta the tick applies.
                if (result != null && result.MealsEaten > 0 && food.SlotIndex >= 0)
                {
                    _playerSessionRegistry?.LarderSlotUpdateQueue.Enqueue(new LarderSlotUpdateNotification
                    {
                        PlayerId = playerId,
                        SlotIndex = food.SlotIndex,
                        ItemId = food.ItemId,
                        Added = -result.MealsEaten,
                    });
                }
                if (reward != null && reward.Diamonds > 0)
                {
                    int balance = await db.PlayerRecords.Where(p => p.Id == playerId).Select(p => p.PremiumDiamonds).FirstAsync();
                    _playerSessionRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification { PlayerId = playerId, PremiumDiamondsBalance = balance });
                }

                await WriteJsonAsync(context, new { Outcome = outcome.ToString(), Reason = reason ?? string.Empty, Result = result, Reward = reward, Tickets = tickets });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dungeon run error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        // POST /api/v1/dev/dungeon/tickets {Tickets} - dev only: set the caller's
        // tickets, so exercise.mjs can run a stage and put them back.
        private async Task HandleDevDungeonTickets(HttpListenerContext context, long playerId)
        {
            int tickets = DungeonEngine.TicketCap;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Tickets", out var t) && t.TryGetInt32(out int tv)) tickets = tv;
            }
            catch (JsonException) { }
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
            if (player == null) { context.Response.StatusCode = 404; return; }
            DungeonEngine.TopUpTickets(player, DateTime.UtcNow);
            player.DungeonTickets = Math.Clamp(tickets, 0, DungeonEngine.TicketCap);
            await db.SaveChangesAsync();
            await WriteJsonAsync(context, new { Tickets = player.DungeonTickets });
        }
    }
}

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
        // The three dungeons (CHESTS_DUNGEONS_DIAMONDS.md 2.6a, 2.8). REST, like
        // the Delve and the loot chests: a run is one request that the server
        // decides and writes; nothing is added to StateUpdatePacket.
        // Modul: HIDDEN FROM PLAYERS (owner, 2026-10-31: "far from how I want it
        // to look"). Only the admin accounts may open or run it; everyone else
        // gets a closed lobby and a refused run, so a remembered screen or an
        // old client cannot spend a ticket. The code, tables and tickets stay;
        // delete this gate when the rework lands.
        private const string DungeonClosedReason = "The dungeon is closed while it is being rebuilt.";

        // The admins, and anyone where the dev tools are on (the local stack,
        // where exercise.mjs runs the whole flow on the dev fixture).
        private bool MaySeeDungeon(PlayerRecord? player) => IsAdmin(player) || DevToolsEnabled();

        // Modul: an admin in production runs the dungeon without spending
        // tickets, while the owner tests it (2026-10-31). Not where dev tools
        // are on: exercise.mjs signs in as the admin fixture and asserts that
        // a run takes a ticket from its own gate only.
        private bool FreeDungeonRuns(PlayerRecord? player) => IsAdmin(player) && !DevToolsEnabled();

        // Modul: DEV ONLY - region bosses counted as beaten for the dungeons,
        // in memory and never written. The dev fixture has not beaten the
        // region 2 boss, so the lobby is shut for it; exercise.mjs opens it
        // here, runs its stages and clears it, leaving the codex untouched.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _devDungeonBossMask = new();

        private byte DungeonBossMask(long playerId, byte live)
            => DevToolsEnabled() && _devDungeonBossMask.TryGetValue(playerId, out byte extra) ? (byte)(live | extra) : live;

        private async Task<bool> TryHandleDungeonAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/dungeon", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            if (requestPath == "/api/v1/dungeon" && method == "GET") { await HandleDungeonView(context); return true; }
            if (requestPath == "/api/v1/dungeon/run" && method == "POST") { await HandleDungeonRun(context); return true; }
            return false;
        }

        /// <summary>A copy of the live payload with the main character in the active register, or null offline.</summary>
        private async Task<PayloadSnapshot?> MainCharacterSnapshotAsync(long playerId)
        {
            if (_playerSessionRegistry == null) return null;
            var order = new PayloadSnapshotOrder { PlayerId = playerId };
            _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
            var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
            var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
            if (snapshot == null) return null;
            var payload = snapshot.Value.Payload;
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 0);
            return snapshot.Value with { Payload = payload };
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
                if (!MaySeeDungeon(player))
                {
                    await WriteJsonAsync(context, new { Open = false, LockedReason = DungeonClosedReason, Dungeons = Array.Empty<object>() });
                    return;
                }
                var rows = await DungeonEngine.LoadAsync(db, playerId, DateTime.UtcNow);
                await db.SaveChangesAsync();

                var snapshot = await MainCharacterSnapshotAsync(playerId);
                byte mask = DungeonBossMask(playerId, snapshot?.Payload.DefeatedRegionBossMask ?? 0);
                bool open = DungeonEngine.IsOpen(mask);
                await WriteJsonAsync(context, new
                {
                    Open = open,
                    LockedReason = open ? string.Empty : DungeonEngine.StageLockedReason(1, mask, 0) ?? string.Empty,
                    Online = snapshot != null,
                    FreeRuns = FreeDungeonRuns(player),
                    DungeonEngine.TicketCap,
                    DungeonEngine.TicketsPerDay,
                    DungeonSimulator.Waves,
                    DungeonSimulator.MealCap,
                    Dungeons = DungeonBoss.All.Select(kind =>
                    {
                        var boss = DungeonBoss.For(kind);
                        var row = rows[kind];
                        return new
                        {
                            Kind = (int)kind,
                            Key = kind.ToString(),
                            Name = boss.DungeonName,
                            Boss = boss.BossName,
                            Tickets = row.Tickets,
                            BestStage = row.BestStage,
                            Stages = Enumerable.Range(1, DungeonSimulator.StageCount).Select(stage => new
                            {
                                Stage = stage,
                                Band = DungeonEngine.BandOf(stage),
                                LockedReason = DungeonEngine.StageLockedReason(stage, mask, row.BestStage) ?? string.Empty,
                                Cleared = stage <= row.BestStage,
                                FirstClearPaid = (row.FirstClearMask & (1 << stage)) != 0,
                                Reward = DungeonEngine.RewardLine(kind, stage),
                                Abilities = boss.AbilitiesAt(stage),
                            }).ToArray(),
                        };
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
                DungeonKind kind = DungeonKind.Equipment;
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (parsed.RootElement.TryGetProperty("Stage", out var s) && s.TryGetInt32(out int sv)) stage = sv;
                    if (parsed.RootElement.TryGetProperty("Kind", out var k) && k.TryGetInt32(out int kv))
                    {
                        if (!Enum.IsDefined(typeof(DungeonKind), kv)) { context.Response.StatusCode = 400; return; }
                        kind = (DungeonKind)kv;
                    }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                bool freeRun;
                using (var gateScope = _serviceProvider.CreateScope())
                {
                    var gateDb = gateScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    var gatePlayer = await gateDb.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
                    freeRun = FreeDungeonRuns(gatePlayer);
                    if (!MaySeeDungeon(gatePlayer))
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

                var payload = snapshot.Value.Payload;
                payload.DefeatedRegionBossMask = DungeonBossMask(playerId, payload.DefeatedRegionBossMask);
                var food = DungeonEngine.BestFood(payload);
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var income = new DungeonEngine.Income(
                    kind == DungeonKind.Xp ? DungeonEngine.BestHuntingXpPerHour(in payload, snapshot.Value.GlobalXpMultiplier, snapshot.Value.ActiveGlobalEventId) : 0,
                    kind == DungeonKind.Gold ? await GoldRewardScale.HourlyIncomeAsync(db, playerId, DateTime.UtcNow) : 0);
                var (outcome, reason, result, reward, tickets) = await DungeonEngine.RunAsync(
                    db, playerId, kind, stage, payload, food.ItemId, food.Count, income, DateTime.UtcNow, Random.Shared, freeRun);

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
                if (reward != null && reward.Xp > 0)
                {
                    _playerSessionRegistry?.DungeonXpGrantQueue.Enqueue(new DungeonXpGrantNotification { PlayerId = playerId, Xp = reward.Xp });
                }
                if (reward != null && reward.Gold > 0)
                {
                    // Modul: the gold moved in the database, off the tick - the
                    // Delve's and the quest line's answer (see HandleQuestLineClaim).
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
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

        // POST /api/v1/dev/dungeon/unlock {Regions} - dev only: count the bosses
        // of regions 1..Regions as beaten for the caller's dungeons (0 clears).
        private async Task HandleDevDungeonUnlock(HttpListenerContext context, long playerId)
        {
            int regions = 0;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Regions", out var r) && r.TryGetInt32(out int rv)) regions = rv;
            }
            catch (JsonException) { }
            byte mask = 0;
            for (int region = 1; region <= Math.Clamp(regions, 0, 5); region++)
                mask = BossFirstClearRules.MarkDefeated(mask, RaceUnlockRegistry.GetRegionBossMonsterId(region));
            if (mask == 0) _devDungeonBossMask.TryRemove(playerId, out _);
            else _devDungeonBossMask[playerId] = mask;
            await WriteJsonAsync(context, new { Regions = Math.Clamp(regions, 0, 5) });
        }

        // POST /api/v1/dev/dungeon/tickets {Kind, Tickets} - dev only: set the
        // caller's tickets in one dungeon (every dungeon when Kind is absent),
        // so exercise.mjs can run a stage and put them back.
        private async Task HandleDevDungeonTickets(HttpListenerContext context, long playerId)
        {
            int tickets = DungeonEngine.TicketCap;
            int kind = -1;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Tickets", out var t) && t.TryGetInt32(out int tv)) tickets = tv;
                if (parsed.RootElement.TryGetProperty("Kind", out var k) && k.TryGetInt32(out int kv)) kind = kv;
            }
            catch (JsonException) { }
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var rows = await DungeonEngine.LoadAsync(db, playerId, DateTime.UtcNow);
            foreach (var (k, row) in rows)
            {
                if (kind < 0 || (int)k == kind) row.Tickets = Math.Clamp(tickets, 0, DungeonEngine.TicketCap);
            }
            await db.SaveChangesAsync();
            await WriteJsonAsync(context, new { Tickets = rows.ToDictionary(r => r.Key.ToString(), r => r.Value.Tickets) });
        }
    }
}

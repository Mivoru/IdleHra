using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Network
{
    // Modul: THE LOGIN WEEK'S TWO ROUTES (owner, 2026-11-15). The client opens
    // its window at the first session of the day from GET .../state, and the
    // player claims with POST .../claim. The week, the pause-not-reset rule and
    // the income-scaled gold all live in DailyLoginRewardEngine; this file only
    // reads, writes and tells the live session what moved.
    public partial class NetworkBroadcastSystem
    {
        private async Task<bool> TryHandleLoginWeekAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/login-bonus", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            if (requestPath == "/api/v1/login-bonus/state" && method == "GET") { await HandleLoginWeekState(context); return true; }
            if (requestPath == "/api/v1/login-bonus/claim" && method == "POST") { await HandleLoginWeekClaim(context); return true; }
            return false;
        }

        // GET /api/v1/login-bonus/state
        // The whole week with today's gold figures, which day is next, and
        // whether it can be claimed today.
        private async Task HandleLoginWeekState(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var player = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new { p.LoginStreakDays, p.LoginRewardClaimDay, p.LoginCyclesCompleted })
                    .SingleOrDefaultAsync();
                if (player == null) { context.Response.StatusCode = 404; return; }

                var utcNow = GameClock.UtcNow;
                long hourly = await GoldRewardScale.HourlyIncomeAsync(db, playerId, utcNow);
                long today = DailyLoginRewardEngine.ToDateKey(utcNow);
                bool claimable = DailyLoginRewardEngine.CanClaim(player.LoginRewardClaimDay, today);

                await WriteJsonAsync(context, new
                {
                    ClaimableToday = claimable,
                    // The day today's claim pays, or - once claimed - the day just paid.
                    Day = claimable ? DailyLoginRewardEngine.NextDay(player.LoginStreakDays) : Math.Max(1, player.LoginStreakDays),
                    LastClaimedDay = player.LoginStreakDays,
                    WeeksCompleted = player.LoginCyclesCompleted,
                    // Seconds until the next UTC day, so the window can say when.
                    SecondsUntilNextDay = (int)((today + 1) * 86400L - new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds()),
                    Days = DailyLoginRewardEngine.Week.Select(d => new
                    {
                        d.Day,
                        Gold = DailyLoginRewardEngine.GoldFor(d, hourly),
                        d.GoldMinutes,
                        d.Diamonds,
                        LootChest = d.LootChest?.ToString() ?? string.Empty,
                        WardrobeChest = d.WardrobeChestRarity > 0 ? CosmeticRegistry.RarityNames[d.WardrobeChestRarity] : string.Empty,
                        d.TicketsPerGate,
                    }),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Login week state error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        // POST /api/v1/login-bonus/claim
        // Pays today's day. Mutating, so the router's per-account stripe already
        // serialises a double tap; the row lock in ClaimAsync covers two devices.
        private async Task HandleLoginWeekClaim(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var result = await DailyLoginRewardEngine.ClaimAsync(db, playerId, GameClock.UtcNow);

                if (result.Outcome == DailyLoginRewardEngine.ClaimOutcome.Ok)
                {
                    // Modul: both balances moved in the DATABASE, and the
                    // checkpoint writes both from the live payload - so the
                    // session is told, the same two calls the gold dungeon makes.
                    if (result.Gold > 0)
                    {
                        CommandQueue.Enqueue(new PlayerCommand
                        {
                            PlayerId = playerId,
                            Packet = new ClientCommandPacket { Command = CommandType.ReloadState },
                        });
                    }
                    if (result.Diamonds > 0)
                    {
                        _playerSessionRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification
                        {
                            PlayerId = playerId,
                            PremiumDiamondsBalance = result.DiamondBalance,
                        });
                    }
                }

                // Not WriteJsonAsync: that one always answers 200, and an
                // already-claimed day must say 409.
                context.Response.StatusCode = result.Outcome switch
                {
                    DailyLoginRewardEngine.ClaimOutcome.Ok => 200,
                    DailyLoginRewardEngine.ClaimOutcome.AlreadyClaimed => 409,
                    _ => 404,
                };
                context.Response.ContentType = "application/json";
                await System.Text.Json.JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                {
                    Outcome = result.Outcome.ToString(),
                    result.Day,
                    result.Gold,
                    result.Diamonds,
                    result.LootChest,
                    result.WardrobeChest,
                    result.TicketsGranted,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Login week claim error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}

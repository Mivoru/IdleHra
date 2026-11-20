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
        /// <summary>
        /// The shield wheel's four routes (task 36, spec 5).
        ///
        /// Modul: TIMESTAMPS AND CHOICES ONLY. Nothing a request carries is a
        /// quantity the server adopts: Seq, TapMs, a parry enum and a plate
        /// 0-4, every one of them scored against the server's own schedule.
        /// Every refusal answers 200 with a Result the screen can say out loud;
        /// malformed JSON is 400 and a missing token 401.
        /// </summary>
        private async Task HandleWorldBossStrikeRoute(HttpListenerContext context, string requestPath)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var service = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Combat.WorldBossStrike.WorldBossStrikeService>();
                string method = context.Request.HttpMethod;
                object? answer = null;

                var duel = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Combat.WorldBossStrike.BossDuelService>();

                if (requestPath == "/api/v1/worldboss/challenge" && method == "GET")
                {
                    answer = await service.GetChallengeAsync(playerId);
                }
                else if (requestPath == "/api/v1/worldboss/duel" && method == "GET")
                {
                    answer = duel.Get(playerId);
                }
                else if (method == "POST")
                {
                    string body;
                    body = await ReadBodyAsync(context);

                    try
                    {
                        switch (requestPath)
                        {
                            case "/api/v1/worldboss/challenge":
                                bool practice = false;
                                if (!string.IsNullOrWhiteSpace(body))
                                {
                                    using var parsed = JsonDocument.Parse(body);
                                    if (parsed.RootElement.TryGetProperty("Practice", out var p) && p.ValueKind == JsonValueKind.True) practice = true;
                                }
                                answer = await service.IssueChallengeAsync(playerId, practice);
                                break;
                            case "/api/v1/worldboss/duel":
                                bool duelPractice = false;
                                if (!string.IsNullOrWhiteSpace(body))
                                {
                                    using var parsedDuel = JsonDocument.Parse(body);
                                    if (parsedDuel.RootElement.TryGetProperty("Practice", out var dp) && dp.ValueKind == JsonValueKind.True) duelPractice = true;
                                }
                                answer = await duel.IssueAsync(playerId, duelPractice);
                                break;
                            case "/api/v1/worldboss/duel/call":
                                var callRequest = JsonSerializer.Deserialize<FolkIdle.Server.Domain.Combat.WorldBossStrike.DuelCallRequest>(body, WorldBossStrikeJson);
                                if (callRequest == null) { context.Response.StatusCode = 400; return; }
                                answer = await duel.CallAsync(playerId, callRequest);
                                break;
                            case "/api/v1/worldboss/throw":
                                var throwRequest = JsonSerializer.Deserialize<FolkIdle.Server.Domain.Combat.WorldBossStrike.ThrowRequest>(body, WorldBossStrikeJson);
                                if (throwRequest == null) { context.Response.StatusCode = 400; return; }
                                answer = service.Throw(playerId, throwRequest);
                                break;
                            case "/api/v1/worldboss/practice/score":
                                var scoreRequest = JsonSerializer.Deserialize<FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest>(body, WorldBossStrikeJson);
                                if (scoreRequest == null) { context.Response.StatusCode = 400; return; }
                                answer = service.ScorePractice(playerId, scoreRequest);
                                break;
                            case "/api/v1/worldboss/strike":
                                var strikeRequest = string.IsNullOrWhiteSpace(body)
                                    ? new FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest()
                                    : JsonSerializer.Deserialize<FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest>(body, WorldBossStrikeJson);
                                answer = await service.StrikeAsync(playerId, strikeRequest ?? new FolkIdle.Server.Domain.Combat.WorldBossStrike.StrikeRequest());
                                if (answer == null) { context.Response.StatusCode = 400; return; }
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }
                }

                if (answer == null)
                {
                    context.Response.StatusCode = 405;
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, answer, answer.GetType(), WorldBossStrikeJson);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"World boss strike route error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private async Task HandleDelveView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var engine = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                var view = await engine.GetViewAsync(playerId);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Delve view error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// start / door / bank.
        ///
        /// Modul: THE ONLY NUMBER THIS ACCEPTS FROM THE CLIENT IS A DOOR INDEX,
        /// and it is bounds-checked in the engine. There is no field here that a
        /// tampered client could make profitable - no floor, no depth, no score,
        /// no reward. That is deliberate and it is the whole security design of
        /// the feature: opcode 39 once granted diamonds straight out of an
        /// unsigned client field, and a minigame is exactly the shape that
        /// invites the same mistake a second time.
        ///
        /// Every refusal answers 200 with a Result the screen can read, not a
        /// bare status code. A silent rollback here would present as a dead
        /// button, which is this server's favourite way to lie.
        /// </summary>
        private async Task HandleDelveAction(HttpListenerContext context, string requestPath)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var engine = _serviceProvider.GetRequiredService<FolkIdle.Server.Domain.Economy.DelveEngine>();
                // Modul: each Delve action is one Serializable transaction on its
                // own context, touching the gold and player rows the checkpoint
                // flush writes too. A lost race (40001) reached the catch below
                // as a 500 the client reads as "could not reach the server";
                // every action is safe to rerun (nothing leaves the database
                // before its commit), so it is retried (audit 2026-11-02).
                FolkIdle.Server.Domain.Economy.DelveActionOutcome outcome;
                FolkIdle.Server.Domain.Economy.DelveAutoOutcome? auto = null;

                if (requestPath == "/api/v1/delve/auto")
                {
                    // Not retried as a whole: every step inside is its own
                    // transaction, and a rerun would play a second run.
                    auto = await engine.AutoRunAsync(playerId);
                    outcome = auto.Final;
                    if (auto.StartedRun) FolkIdle.Server.Domain.Progression.MissionEvents.Record(playerId, FolkIdle.Server.Domain.Progression.MissionAction.DelveRun);
                }
                else if (requestPath == "/api/v1/delve/start")
                {
                    outcome = await Domain.Shared.TransientDbConflict.RetryAsync(() => engine.StartRunAsync(playerId));
                    if (outcome.Result == FolkIdle.Server.Domain.Economy.DelveResult.Ok) FolkIdle.Server.Domain.Progression.MissionEvents.Record(playerId, FolkIdle.Server.Domain.Progression.MissionAction.DelveRun);
                }
                else if (requestPath == "/api/v1/delve/bank")
                {
                    outcome = await Domain.Shared.TransientDbConflict.RetryAsync(() => engine.BankAsync(playerId));
                }
                else if (requestPath == "/api/v1/delve/door")
                {
                    string body = await ReadBodyAsync(context);

                    int door;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("Door", out var doorElement)
                            || doorElement.ValueKind != JsonValueKind.Number)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }
                        door = doorElement.GetInt32();
                    }
                    catch (JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    outcome = await Domain.Shared.TransientDbConflict.RetryAsync(() => engine.ChooseDoorAsync(playerId, door));
                }
                else if (requestPath == "/api/v1/delve/deep/descend")
                {
                    // Modul: QuotedStake is the stake the screen SHOWED, and it
                    // is only ever a ceiling - the engine charges its own
                    // number and answers PriceChanged if that is higher. A
                    // missing or malformed quote is a 400, not a zero: a zero
                    // quote would just read as "price changed" for ever.
                    string body = await ReadBodyAsync(context);

                    long quotedStake;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("QuotedStake", out var quoteElement)
                            || quoteElement.ValueKind != JsonValueKind.Number
                            || !quoteElement.TryGetInt64(out quotedStake))
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }
                    }
                    catch (JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    outcome = await Domain.Shared.TransientDbConflict.RetryAsync(() => engine.DescendAsync(playerId, quotedStake));
                    if (outcome.Result == FolkIdle.Server.Domain.Economy.DelveResult.Ok) FolkIdle.Server.Domain.Progression.MissionEvents.Record(playerId, FolkIdle.Server.Domain.Progression.MissionAction.DelveDeep);
                }
                else if (requestPath == "/api/v1/delve/deep/lantern")
                {
                    // No body is read: the action is the whole request, and the
                    // price is the engine's (stake x 2^bought, frozen stake).
                    outcome = await Domain.Shared.TransientDbConflict.RetryAsync(() => engine.BuyLanternAsync(playerId));
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                // Modul: gold and diamonds moved in the database, out of band
                // from the live payload - so the session has to be told, or the
                // header keeps showing the old balance until the player signs in
                // again. Reported once already as "I have to press F5 for the
                // gold to update"; ReloadState is the answer every other
                // off-tick engine here uses.
                //
                // A descent moves both: it banks floors 1-8 (diamonds or
                // consolation gold) and debits a toll, all in the database.
                //
                // A Deep floor that is a new weekly record pays one diamond on the
                // door endpoint (2026-10-07), so a cleared floor that paid reloads too.
                bool changedBalances =
                    (outcome.Result == FolkIdle.Server.Domain.Economy.DelveResult.Ok
                        && (requestPath == "/api/v1/delve/start"
                            || requestPath == "/api/v1/delve/bank"
                            || requestPath == "/api/v1/delve/deep/descend"
                            || requestPath == "/api/v1/delve/deep/lantern"))
                    || (outcome.Result == FolkIdle.Server.Domain.Economy.DelveResult.FloorCleared
                        && outcome.DiamondsGranted > 0)
                    || (auto != null && (auto.StartedRun || auto.Banked));

                if (changedBalances)
                {
                    CommandQueue.Enqueue(new PlayerCommand
                    {
                        PlayerId = playerId,
                        Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                {
                    Result = outcome.Result.ToString(),
                    outcome.DiamondsGranted,
                    outcome.GoldReturned,
                    outcome.GoldCharged,
                    BottomChestGranted = auto?.BottomChestGranted ?? outcome.BottomChestGranted,
                    BottomTicketsGranted = auto?.BottomTicketsGranted ?? outcome.BottomTicketsGranted,
                    outcome.SoulsGranted,
                    AutoSteps = auto?.Steps,
                    AutoBanked = auto?.Banked ?? false,
                    outcome.View
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Delve action error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
    }
}

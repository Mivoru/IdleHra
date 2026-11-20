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
        /// Reads (GET) or sets (POST) whether this player wants notification
        /// email. Opt-in: the column starts false for every account and this is
        /// the only route that writes it.
        /// </summary>
        /// <remarks>
        /// Modul: separate from the settings the client keeps for itself,
        /// because this one is a PERMISSION rather than a preference. It has to
        /// be stored where the sender can read it - a background job on the
        /// server decides to mail somebody hours after they closed the tab, so
        /// a consent flag living in the browser would be a consent flag nobody
        /// asks.
        /// </remarks>
        private async Task HandleEmailConsent(HttpListenerContext context)
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

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);

                    bool consent;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("Consented", out var flag)
                            || (flag.ValueKind != JsonValueKind.True && flag.ValueKind != JsonValueKind.False))
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }
                        consent = flag.GetBoolean();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    // Consenting with no address on the account would arm a
                    // notifier that can never send. Refused, so the client can
                    // say why rather than showing a toggle that does nothing.
                    if (consent && string.IsNullOrWhiteSpace(player.Email))
                    {
                        context.Response.StatusCode = 409;
                        context.Response.Close();
                        return;
                    }

                    player.EmailNotificationsConsented = consent;

                    // Withdrawing consent clears the once-per-absence marker as
                    // well, so opting back in later is not silently blocked by
                    // a send that happened before the player opted out.
                    if (!consent) player.OfflineCapEmailSentEpoch = 0L;

                    await db.SaveChangesAsync();
                }

                var response = new EmailConsentResponse
                {
                    Consented = player.EmailNotificationsConsented,
                    HasEmailAddress = !string.IsNullOrWhiteSpace(player.Email)
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Email consent error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>Minimal JSON string escaping for the two values above.</summary>
        private static string JsonEscape(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private async Task HandlePushTokenRegistration(HttpListenerContext context)
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

                if (_pushNotificationTriggerEngine == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                string body = await ReadBodyAsync(context);

                string token;
                string platform;
                try
                {
                    using var parsed = JsonDocument.Parse(body);
                    token = parsed.RootElement.TryGetProperty("Token", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
                    platform = parsed.RootElement.TryGetProperty("Platform", out var pf) ? (pf.GetString() ?? string.Empty) : string.Empty;
                }
                catch (System.Text.Json.JsonException)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                byte platformFamily = platform.ToLowerInvariant() switch
                {
                    "android" => (byte)1,
                    "ios" => (byte)2,
                    _ => (byte)0
                };

                if (platformFamily == 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                // Bounds checked HERE as well as in the engine, so a token that
                // is obviously not one is a 400 the client can explain rather
                // than a 500 it cannot.
                int tokenBytes = System.Text.Encoding.UTF8.GetByteCount(token.Trim());
                if (tokenBytes < PushNotificationTriggerEngine.MinDeviceTokenBytes
                    || tokenBytes > PushNotificationTriggerEngine.MaxDeviceTokenBytes)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                bool stored = await _pushNotificationTriggerEngine.RegisterDeviceTokenAsync(playerId, token, platformFamily);
                context.Response.StatusCode = stored ? 200 : 500;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Push token registration error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Reads (GET) or replaces (PUT) the set of onboarding explanations
        /// this player has been shown.
        /// </summary>
        /// <remarks>
        /// Modul: REPLACE, NOT APPEND, and the client is the one that merges.
        ///
        /// An append endpoint looks safer and is not: the client already holds
        /// a local copy for synchronous reads, it unions that with whatever the
        /// server returns at sign-in, and it is the only place that knows about
        /// "forget this one" and "forget all". Two merge strategies for one set
        /// - one here and one there - is the two-sources-of-truth shape this
        /// codebase keeps getting bitten by. So: the server stores, the client
        /// decides.
        ///
        /// NULL SURVIVES. A PUT is what creates the record, and until one
        /// arrives the column stays null and `HasRecord` stays false. That is
        /// load-bearing - see PlayerRecord.OnboardingSeenIds.
        /// </remarks>
        private async Task HandleOnboardingSeen(HttpListenerContext context)
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

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var player = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "PUT" || context.Request.HttpMethod == "POST")
                {
                    string body = await ReadBodyAsync(context);

                    string[] ids;
                    try
                    {
                        using var parsed = JsonDocument.Parse(body);
                        if (!parsed.RootElement.TryGetProperty("Seen", out var seenElement)
                            || seenElement.ValueKind != JsonValueKind.Array)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }

                        var collected = new List<string>();
                        foreach (var entry in seenElement.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.String) continue;
                            string id = entry.GetString() ?? string.Empty;

                            // Modul: silently DROPPED rather than refused. An id
                            // this client no longer recognises is what a rolled
                            // back deploy looks like, and rejecting the whole
                            // body over one would lose the other twenty-five.
                            if (id.Length == 0 || id.Length > MaxOnboardingIdLength) continue;
                            if (!collected.Contains(id)) collected.Add(id);
                        }

                        if (collected.Count > MaxOnboardingSeenIds)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.Close();
                            return;
                        }

                        ids = collected.ToArray();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    player.OnboardingSeenIds = JsonSerializer.Serialize(ids);
                    await db.SaveChangesAsync();
                }

                var response = new OnboardingSeenResponse
                {
                    HasRecord = player.OnboardingSeenIds != null,
                    Seen = ParseOnboardingSeen(player.OnboardingSeenIds)
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Onboarding seen error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Reads the stored column back, treating anything unreadable as empty.
        /// </summary>
        /// <remarks>
        /// A column that will not parse must not 500 a sign-in. The worst
        /// outcome of treating it as empty is that the player is taught
        /// something again; the worst outcome of throwing is that they cannot
        /// play.
        /// </remarks>
        private static string[] ParseOnboardingSeen(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return System.Array.Empty<string>();
            try
            {
                return JsonSerializer.Deserialize<string[]>(stored) ?? System.Array.Empty<string>();
            }
            catch (System.Text.Json.JsonException)
            {
                return System.Array.Empty<string>();
            }
        }
    }
}

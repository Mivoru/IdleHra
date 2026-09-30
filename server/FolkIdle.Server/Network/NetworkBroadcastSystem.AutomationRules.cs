using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    // Task 85, "Orders": the automation rules. REST, not the wire - a rule is a
    // setting, like the auto-salvage floor, so it needs no packet field and no
    // opcode. The POST writes characters."AutomationRules" and hands the change
    // to the tick through AutomationRulesQueue. A refusal answers 200 with its
    // Result so the panel can say why (the "silent rollback" rule); only a
    // malformed body is a 400.
    public partial class NetworkBroadcastSystem
    {
        /// <summary>One rule as the API speaks it: a fishing rule's Param is the spot's activity id.</summary>
        public sealed class AutomationRuleDto
        {
            public int Type { get; set; }
            public int Param { get; set; }
        }

        private sealed class AutomationRulesRequest
        {
            public Guid CharacterId { get; set; }
            public List<AutomationRuleDto>? Rules { get; set; }
        }

        /// <summary>Routes /api/v1/automation-rules; true when it answered.</summary>
        private async Task<bool> TryHandleAutomationRulesAsync(HttpListenerContext context, string requestPath)
        {
            if (requestPath != "/api/v1/automation-rules") return false;
            string method = context.Request.HttpMethod;
            if (method != "GET" && method != "POST") return false;
            await HandleAutomationRules(context, method == "POST");
            return true;
        }

        private static AutomationRuleDto ToDto(AutomationRules.Rule rule) => new()
        {
            Type = rule.Type,
            Param = rule.Type == AutomationRules.FishWhenLarderDry ? (int)AutomationRules.FishingNodeFor(rule.Param) : rule.Param,
        };

        private static AutomationRules.Rule FromDto(AutomationRuleDto dto) => new()
        {
            Type = (byte)Math.Clamp(dto.Type, 0, 255),
            Param = dto.Type == AutomationRules.FishWhenLarderDry
                ? (int)(dto.Param - ActivityIdBands.FishingBand)
                : dto.Param,
        };

        private async Task HandleAutomationRules(HttpListenerContext context, bool isPost)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // The level the slots are gated on. The account's, because the
                // game has no per-character level (CharacterRecord says why).
                // The row lags the live session by at most one checkpoint.
                int level = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId).Select(p => (int?)p.CurrentLevel).SingleOrDefaultAsync() ?? 0;

                // The fielded characters, in LoadPlayerState's own order - the
                // three the tick simulates and the rules act for.
                var characters = await db.CharacterRecords
                    .Where(c => c.PlayerId == playerId && !c.IsLockedInEscrow)
                    .OrderBy(c => c.SlotIndex)
                    .ThenBy(c => c.Id)
                    .Take(AutomationRules.SlotCount)
                    .ToListAsync();

                string result = "Ok";
                if (isPost)
                {
                    AutomationRulesRequest? request;
                    try
                    {
                        string body = await ReadBodyAsync(context);
                        request = JsonSerializer.Deserialize<AutomationRulesRequest>(body);
                    }
                    catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    if (request == null || request.Rules == null)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var target = characters.Find(c => c.Id == request.CharacterId);
                    var rules = request.Rules.Select(FromDto).ToArray();
                    string? refusal = target == null ? "NotFielded" : AutomationRules.Validate(rules, level);
                    if (refusal != null)
                    {
                        result = refusal;
                    }
                    else
                    {
                        long packed = AutomationRules.Pack(rules);
                        target!.AutomationRules = packed;
                        await db.SaveChangesAsync();
                        _playerSessionRegistry?.AutomationRulesQueue.Enqueue(new AutomationRulesNotification
                        {
                            PlayerId = playerId,
                            CharacterId = target.Id,
                            PackedRules = packed,
                        });
                    }
                }

                await WriteJsonAsync(context, new
                {
                    Result = result,
                    Level = level,
                    UnlockLevels = AutomationRules.UnlockLevels,
                    MaxFuseTier = Domain.Economy.ForgeSplicingEngine.MaxQualityTier,
                    FishingSpots = ContentRegistry.GatheringNodes.ToArray()
                        .Where(n => n.ProfessionType == 2)
                        .Select(n => new { n.ActivityId, Location = ContentRegistry.GetNodeLocation(n.ActivityId) })
                        .OrderBy(n => n.ActivityId)
                        .ToArray(),
                    Characters = characters.Select((c, i) => new
                    {
                        CharacterId = c.Id,
                        c.Name,
                        Slot = i,
                        Rules = Enumerable.Range(0, AutomationRules.SlotCount)
                            .Select(s => ToDto(AutomationRules.Unpack(c.AutomationRules, s)))
                            .ToArray(),
                    }).ToArray(),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Automation rules error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}

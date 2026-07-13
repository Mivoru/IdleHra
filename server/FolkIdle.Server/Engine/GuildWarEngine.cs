using System;
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Engine
{
    public struct GuildWarPointEvent
    {
        public long MatchId;
        public long GuildId;
        public int Front; // 0 = Combat, 1 = Logistics, 2 = Supply Chain
        public int Points;
    }

    public struct GuildWarSupplyContribution
    {
        public long PlayerId;
        public long CommodityId;
        public long QuantityToBurn;
    }

    public class GuildWarEngine
    {
        private readonly IServiceProvider _serviceProvider;
        public readonly ConcurrentQueue<GuildWarPointEvent> GuildWarPointQueue = new();
        public readonly ConcurrentQueue<GuildWarSupplyContribution> SupplyChainQueue = new();
        private CancellationTokenSource _cts = new();

        public GuildWarEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _ = ExecuteAsync(_cts.Token);
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var aggregationTask = RunAggregationLoopAsync(stoppingToken);
            var supplyChainTask = RunSupplyChainLoopAsync(stoppingToken);
            var matchmakingTask = RunMatchmakingLoopAsync(stoppingToken);

            await Task.WhenAll(aggregationTask, supplyChainTask, matchmakingTask);
        }

        private async Task RunAggregationLoopAsync(CancellationToken stoppingToken)
        {
            var matchDeltas = new System.Collections.Generic.Dictionary<(long MatchId, long GuildId, int Front), int>();

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);

                matchDeltas.Clear();

                while (GuildWarPointQueue.TryDequeue(out var ev))
                {
                    var key = (ev.MatchId, ev.GuildId, ev.Front);
                    if (!matchDeltas.ContainsKey(key))
                        matchDeltas[key] = 0;
                    matchDeltas[key] += ev.Points;
                }

                if (matchDeltas.Count > 0)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

                    try
                    {
                        foreach (var kvp in matchDeltas)
                        {
                            var match = await dbContext.GuildWarMatches
                                .FromSqlRaw("SELECT * FROM \"GuildWarMatches\" WHERE \"Id\" = {0} FOR UPDATE", kvp.Key.MatchId)
                                .FirstOrDefaultAsync(stoppingToken);
                            if (match != null && match.IsActive)
                            {
                                bool isGuildA = match.GuildA_Id == kvp.Key.GuildId;
                                if (kvp.Key.Front == 0)
                                {
                                    if (isGuildA) match.CombatVanguardWP_A += kvp.Value;
                                    else match.CombatVanguardWP_B += kvp.Value;
                                }
                                else if (kvp.Key.Front == 1)
                                {
                                    if (isGuildA) match.ProductionLogisticsWP_A += kvp.Value;
                                    else match.ProductionLogisticsWP_B += kvp.Value;
                                }
                                else if (kvp.Key.Front == 2)
                                {
                                    if (isGuildA) match.GatheringSupplyChainWP_A += kvp.Value;
                                    else match.GatheringSupplyChainWP_B += kvp.Value;
                                }
                            }
                        }

                        await dbContext.SaveChangesAsync(stoppingToken);
                        await transaction.CommitAsync(stoppingToken);
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(stoppingToken);
                    }
                }
            }
        }

        private async Task RunSupplyChainLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (SupplyChainQueue.TryDequeue(out var contribution))
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);
                    try
                    {
                        var player = await dbContext.PlayerRecords
                            .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", contribution.PlayerId)
                            .FirstOrDefaultAsync(stoppingToken);
                        if (player != null && player.GuildId > 0)
                        {
                            long guildId = player.GuildId;
                            string commodityId = contribution.CommodityId.ToString(CultureInfo.InvariantCulture);
                            long quantityToBurn = contribution.QuantityToBurn;

                            var commodity = await dbContext.CommodityRecords
                                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", player.Id, commodityId)
                                .FirstOrDefaultAsync(stoppingToken);

                            if (commodity != null && commodity.Quantity >= quantityToBurn && quantityToBurn > 0)
                            {
                                // Vaporize exactly
                                commodity.Quantity -= quantityToBurn;
                                if (commodity.Quantity == 0)
                                {
                                    dbContext.CommodityRecords.Remove(commodity);
                                }

                                long rawSupplyChainPoints = (quantityToBurn / 1000L) * 100L;
                                int supplyChainPoints = rawSupplyChainPoints > int.MaxValue ? int.MaxValue : (int)rawSupplyChainPoints;
                                
                                if (supplyChainPoints > 0)
                                {
                                    var activeMatch = await dbContext.GuildWarMatches
                                        .FromSqlRaw("SELECT * FROM \"GuildWarMatches\" WHERE \"IsActive\" = TRUE AND (\"GuildA_Id\" = {0} OR \"GuildB_Id\" = {0}) FOR UPDATE", guildId)
                                        .FirstOrDefaultAsync(stoppingToken);
                                    if (activeMatch != null)
                                    {
                                        if (activeMatch.GuildA_Id == guildId) activeMatch.GatheringSupplyChainWP_A += supplyChainPoints;
                                        else activeMatch.GatheringSupplyChainWP_B += supplyChainPoints;
                                    }
                                }
                                
                                await dbContext.SaveChangesAsync(stoppingToken);
                                await transaction.CommitAsync(stoppingToken);
                            }
                            else
                            {
                                await transaction.RollbackAsync(stoppingToken);
                            }
                        }
                        else
                        {
                            await transaction.RollbackAsync(stoppingToken);
                        }
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(stoppingToken);
                    }
                }
                else
                {
                    await Task.Delay(100, stoppingToken);
                }
            }
        }

        private async Task RunMatchmakingLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 23 && now.Minute == 30)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

                    try
                    {
                        var activeMatches = await dbContext.GuildWarMatches
                            .FromSqlRaw("SELECT * FROM \"GuildWarMatches\" WHERE \"IsActive\" = TRUE FOR UPDATE")
                            .ToListAsync(stoppingToken);
                        foreach (var match in activeMatches)
                        {
                            match.IsActive = false;
                            await ResolveCombatPhaseAsync(dbContext, match, stoppingToken);
                        }

                        await dbContext.SaveChangesAsync(stoppingToken);

                        var guilds = await dbContext.GuildRecords
                            .FromSqlRaw("SELECT * FROM \"GuildRecords\" FOR UPDATE")
                            .ToListAsync(stoppingToken);
                        var matched = new System.Collections.Generic.HashSet<long>();

                        foreach (var gA in guilds)
                        {
                            if (matched.Contains(gA.Id)) continue;
                        
                            GuildRecord? bestMatch = null;
                            double bestDistance = double.MaxValue;

                            foreach (var gB in guilds)
                            {
                                if (gA.Id == gB.Id || matched.Contains(gB.Id)) continue;

                                double distance = Math.Sqrt(1.0 * Math.Pow(gA.GuildMMR - gB.GuildMMR, 2) + 0.35 * Math.Pow(gA.ActiveMembers - gB.ActiveMembers, 2));
                                if (distance < bestDistance)
                                {
                                    bestDistance = distance;
                                    bestMatch = gB;
                                }
                            }

                            if (bestMatch != null)
                            {
                                matched.Add(gA.Id);
                                matched.Add(bestMatch.Id);
                            
                                var newMatch = new GuildWarMatch
                                {
                                    GuildA_Id = gA.Id,
                                    GuildB_Id = bestMatch.Id,
                                    MatchEpoch = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                    IsActive = true
                                };
                                dbContext.GuildWarMatches.Add(newMatch);
                            }
                        }

                        await dbContext.SaveChangesAsync(stoppingToken);
                        await transaction.CommitAsync(stoppingToken);
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(stoppingToken);
                    }
                    
                    await Task.Delay(60000, stoppingToken);
                }
                else
                {
                    await Task.Delay(60000, stoppingToken);
                }
            }
        }

        private async Task ResolveCombatPhaseAsync(FolkIdleDbContext dbContext, GuildWarMatch match, CancellationToken stoppingToken)
        {
            var snapA = await dbContext.GuildWarDefensiveSnapshots.FirstOrDefaultAsync(s => s.GuildId == match.GuildA_Id, stoppingToken);
            var snapB = await dbContext.GuildWarDefensiveSnapshots.FirstOrDefaultAsync(s => s.GuildId == match.GuildB_Id, stoppingToken);
            
            if (snapA == null || snapB == null) return;
            
            try
            {
                var statsA = JsonSerializer.Deserialize<CombatStats>(snapA.RosterPayloadJson);
                var statsB = JsonSerializer.Deserialize<CombatStats>(snapB.RosterPayloadJson);

                long hpA = (long)(statsA.MaxHp * 1.25);
                long hpB = (long)(statsB.MaxHp * 1.25);

                for (int turn = 0; turn < 100; turn++)
                {
                    if (hpA <= 0 || hpB <= 0) break;

                    // A attacks B
                    float hitChanceA = Math.Clamp(100f / 100f, 0.05f, 0.95f); // Simplified attackerAccuracy / defenderDodge for aggregate
                    if (Random.Shared.NextDouble() <= hitChanceA)
                    {
                        float critMult = Random.Shared.NextDouble() <= (statsA.CritChancePct / 100.0f) ? 1.5f : 1.0f;
                        long baseMilliAttack = 15000L;
                        long effectiveMilliAttack = baseMilliAttack + (statsA.FlatMeleeDamage * 1000L);
                        int rawDamage = (int)(effectiveMilliAttack * critMult);
                        int netDamage = Math.Max(1000, rawDamage - (statsB.FlatPhysicalArmor * 1000));
                        hpB -= netDamage;
                    }

                    if (hpB <= 0) break;

                    // B attacks A
                    float hitChanceB = Math.Clamp(100f / 100f, 0.05f, 0.95f);
                    if (Random.Shared.NextDouble() <= hitChanceB)
                    {
                        float critMult = Random.Shared.NextDouble() <= (statsB.CritChancePct / 100.0f) ? 1.5f : 1.0f;
                        long baseMilliAttack = 15000L;
                        long effectiveMilliAttack = baseMilliAttack + (statsB.FlatMeleeDamage * 1000L);
                        int rawDamage = (int)(effectiveMilliAttack * critMult);
                        int netDamage = Math.Max(1000, rawDamage - (statsA.FlatPhysicalArmor * 1000));
                        hpA -= netDamage;
                    }
                }

                if (hpA > hpB) match.CombatVanguardWP_A += 1000;
                else if (hpB > hpA) match.CombatVanguardWP_B += 1000;
            }
            catch (Exception)
            {
            }
        }
    }
}

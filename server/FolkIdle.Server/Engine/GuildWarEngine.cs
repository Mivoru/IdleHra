using System;
using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
        // Modul 13.4.3: not tied to ContentRegistry.ItemDefinitions - guild war
        // victory tokens are a war-specific reward type, stored the same way
        // GuildLogisticsEngine already stores deposited materials
        // (GuildDepotBalances keyed by GuildId + ItemDefinitionId).
        private const int GuildWarVictoryTokenItemId = 9001;
        private const int VictoryTokenReward = 50;

        private readonly IServiceProvider _serviceProvider;
        public readonly ConcurrentQueue<GuildWarPointEvent> GuildWarPointQueue = new();
        public readonly ConcurrentQueue<GuildWarSupplyContribution> SupplyChainQueue = new();
        private CancellationTokenSource _cts = new();

        // Modul: Guild War scoreboard sync. The guilds that had an active war on
        // the previous sync pass, so a war that has since ended can be detected
        // and cleared. Touched only by the single sync loop task.
        private System.Collections.Generic.HashSet<long> _guildsAtWarLastCycle = new();

        // Modul: Guild War scoreboard sync. Assigned after construction
        // (Program.cs builds PlayerSessionRegistry after this engine, the
        // same ordering problem RegisterSimulationEngine/
        // RegisterPlayerSessionRegistry already solve elsewhere). Null until
        // then, and the sync loop simply does nothing while it is.
        private PlayerSessionRegistry? _playerRegistry;

        public void RegisterPlayerSessionRegistry(PlayerSessionRegistry registry)
        {
            _playerRegistry = registry;
        }

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
            var scoreboardTask = RunScoreboardSyncLoopAsync(stoppingToken);

            await Task.WhenAll(aggregationTask, supplyChainTask, matchmakingTask, scoreboardTask);
        }

        // Modul: Guild War scoreboard sync. The missing half of the war
        // feature. The three loops above correctly accumulate points onto the
        // GuildWarMatches row, but nothing ever read that row back out to the
        // players fighting the war - the six scoreboard fields on
        // TickStatePayload were declared, wired all the way through the
        // packet and into the client UI, and never written by anything. Every
        // client therefore showed a column of zeros during a real war.
        //
        // A read-only poll rather than an event: war points arrive from three
        // unrelated sources (combat kills, tier-5 crafts, supply burns) across
        // every member of both guilds, so a periodic snapshot of the
        // authoritative row is both simpler and cheaper than trying to fan
        // every individual increment out to every member.
        //
        // Five seconds matches the guild raid tick - fast enough that a
        // scoreboard feels live, slow enough that a handful of concurrent wars
        // costs one small indexed query each per interval.
        private const int ScoreboardSyncIntervalMs = 5000;

        // One guild war cycle. Matchmaking runs weekly and resolution is
        // scheduled for Sunday 23:30 UTC, so a match still active a full week
        // after it was created has missed its window and is overdue - see the
        // catch-up branch in RunMatchmakingLoopAsync for why that has to be
        // detectable rather than assumed impossible.
        private const int MatchCycleSeconds = 7 * 24 * 60 * 60;

        private async Task RunScoreboardSyncLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ScoreboardSyncIntervalMs, stoppingToken);

                if (_playerRegistry == null)
                {
                    continue;
                }

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                    var activeMatches = await dbContext.GuildWarMatches
                        .AsNoTracking()
                        .Where(m => m.IsActive)
                        .ToListAsync(stoppingToken);

                    // Modul: Guild War scoreboard sync. Which guilds are at war
                    // THIS cycle. Any guild that was in the previous cycle's set
                    // and is not in this one has just finished its war, and has
                    // to be told - see the clearing pass below.
                    var guildsAtWarThisCycle = new System.Collections.Generic.HashSet<long>();
                    for (int i = 0; i < activeMatches.Count; i++)
                    {
                        guildsAtWarThisCycle.Add(activeMatches[i].GuildA_Id);
                        guildsAtWarThisCycle.Add(activeMatches[i].GuildB_Id);
                    }

                    for (int i = 0; i < activeMatches.Count; i++)
                    {
                        var match = activeMatches[i];

                        int totalA = match.CombatVanguardWP_A + match.ProductionLogisticsWP_A + match.GatheringSupplyChainWP_A;
                        int totalB = match.CombatVanguardWP_B + match.ProductionLogisticsWP_B + match.GatheringSupplyChainWP_B;
                        int combined = totalA + totalB;

                        // A war with no points scored yet is an even split,
                        // not a division by zero and not a 0% rout.
                        float shareA = combined > 0 ? totalA / (float)combined : 0.5f;

                        _playerRegistry.GuildWarScoreboardQueue.Enqueue(new GuildWarScoreboardNotification
                        {
                            GuildId = match.GuildA_Id,
                            OurCombatVanguardPoints = match.CombatVanguardWP_A,
                            OurProductionLogisticsPoints = match.ProductionLogisticsWP_A,
                            OurGatheringSupplyChainPoints = match.GatheringSupplyChainWP_A,
                            EnemyCombatVanguardPoints = match.CombatVanguardWP_B,
                            EnemyProductionLogisticsPoints = match.ProductionLogisticsWP_B,
                            EnemyGatheringSupplyChainPoints = match.GatheringSupplyChainWP_B,
                            ScoreShare = shareA
                        });

                        _playerRegistry.GuildWarScoreboardQueue.Enqueue(new GuildWarScoreboardNotification
                        {
                            GuildId = match.GuildB_Id,
                            OurCombatVanguardPoints = match.CombatVanguardWP_B,
                            OurProductionLogisticsPoints = match.ProductionLogisticsWP_B,
                            OurGatheringSupplyChainPoints = match.GatheringSupplyChainWP_B,
                            EnemyCombatVanguardPoints = match.CombatVanguardWP_A,
                            EnemyProductionLogisticsPoints = match.ProductionLogisticsWP_A,
                            EnemyGatheringSupplyChainPoints = match.GatheringSupplyChainWP_A,
                            ScoreShare = 1f - shareA
                        });
                    }

                    // Modul: Guild War scoreboard sync. The clearing pass.
                    // Without it a finished war stayed on every member's screen
                    // with its final score frozen in place, indistinguishable
                    // from a live one, until they logged out and back in.
                    foreach (long endedGuildId in _guildsAtWarLastCycle)
                    {
                        if (guildsAtWarThisCycle.Contains(endedGuildId))
                        {
                            continue;
                        }

                        _playerRegistry.GuildWarScoreboardQueue.Enqueue(new GuildWarScoreboardNotification
                        {
                            GuildId = endedGuildId,
                            WarEnded = true
                        });
                    }

                    _guildsAtWarLastCycle = guildsAtWarThisCycle;

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Guild war scoreboard sync failed: {ex.Message}");
                }
            }
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
                            // Modul: Play Mode audit fix. This used to
                            // stringify the raw numeric CommodityId directly
                            // (e.g. "2"), which could never match a real
                            // CommodityRecords.ItemId - gathering materials
                            // are stored under slug ids (ContentRegistry.
                            // GetMaterialString/GetMaterialId's own small
                            // 1-6 mapping, separate from GetItemBaseId's
                            // 183-entry equipment/consumable catalog), so
                            // every War Supply contribution was permanently
                            // unmatchable. Resolving through GetMaterialString
                            // is the same fix shape as SimulationEngine's
                            // PlaceLimitOrder ItemType_N bug.
                            string commodityId = ContentRegistry.GetMaterialString((int)contribution.CommodityId);
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

                // Modul: guild war resolution catch-up, 2026-08-01.
                //
                // This used to fire ONLY on exact-minute equality with Sunday
                // 23:30. The loop sleeps 60 seconds, so any downtime spanning
                // that single minute - a deploy, a restart, a long transaction
                // delaying the tick - meant the window was simply never
                // observed. Active matches then stayed IsActive = TRUE forever:
                // never resolved, no victory tokens distributed, and because a
                // guild with an active match cannot be rematched (see the
                // matchmaking query below), both guilds were locked out of guild
                // wars permanently with no error anywhere.
                //
                // Resolution is now driven by whether a match is OVERDUE rather
                // than by what minute it happens to be. MatchEpoch is the
                // creation timestamp, so a match older than one full cycle is
                // resolvable whenever the loop next runs - which makes a missed
                // window self-healing instead of terminal.
                int overdueCutoffEpoch = (int)DateTimeOffset.UtcNow.AddSeconds(-MatchCycleSeconds).ToUnixTimeSeconds();
                bool hasOverdueMatch;

                using (var probeScope = _serviceProvider.CreateScope())
                {
                    var probeDb = probeScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    hasOverdueMatch = await probeDb.GuildWarMatches
                        .AsNoTracking()
                        .AnyAsync(m => m.IsActive && m.MatchEpoch <= overdueCutoffEpoch, stoppingToken);
                }

                bool isScheduledWindow = now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 23 && now.Minute == 30;

                // Modul: THE FIRST MATCH USED TO BE UNREACHABLE.
                //
                // The overdue probe above made a MISSED window self-healing,
                // which was the whole point of it - but it only ever sees
                // matches that already exist. With none, neither branch was
                // ever true, so two guilds that had never fought waited for
                // Sunday 23:30, and a server that happened to be down for that
                // one minute made them wait another week. A brand new
                // deployment had no guild wars at all until the calendar
                // agreed, which reads as the feature simply not working.
                //
                // Verified by creating a second guild and waiting: no match was
                // created, because zero existed to be overdue.
                //
                // So pairing is also allowed whenever there are at least two
                // guilds with no active match between them. That does not
                // change the weekly cadence - an existing match still runs its
                // full MatchCycleSeconds before it resolves - it only stops the
                // FIRST one from depending on a single minute per week.
                bool hasUnmatchedGuilds;
                using (var probeScope = _serviceProvider.CreateScope())
                {
                    var probeDb = probeScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                    // Two flat projections and a client-side set rather than
                    // one SelectMany over an array initialiser plus a negated
                    // Contains. Both of those lean on LINQ translation, and
                    // this probe sits OUTSIDE the pass try/catch in a loop
                    // started as fire-and-forget (`_ = ExecuteAsync(...)`) - a
                    // translation failure here would fault the loop task with
                    // nobody observing it, silently stopping guild war
                    // matchmaking AND resolution for the process lifetime.
                    // Two guild ids per match is not enough data to be worth
                    // that exposure.
                    var busyA = await probeDb.GuildWarMatches
                        .AsNoTracking()
                        .Where(m => m.IsActive)
                        .Select(m => m.GuildA_Id)
                        .ToListAsync(stoppingToken);
                    var busyB = await probeDb.GuildWarMatches
                        .AsNoTracking()
                        .Where(m => m.IsActive)
                        .Select(m => m.GuildB_Id)
                        .ToListAsync(stoppingToken);

                    var busy = new System.Collections.Generic.HashSet<long>(busyA);
                    busy.UnionWith(busyB);

                    var allGuildIds = await probeDb.GuildRecords
                        .AsNoTracking()
                        .Select(g => g.Id)
                        .ToListAsync(stoppingToken);

                    int unmatched = 0;
                    for (int i = 0; i < allGuildIds.Count; i++)
                    {
                        if (!busy.Contains(allGuildIds[i])) unmatched++;
                    }

                    // Two, because a war needs an opponent - one lonely guild
                    // must not spin the pairing pass every sixty seconds
                    // forever.
                    hasUnmatchedGuilds = unmatched >= 2;
                }

                if (isScheduledWindow || hasOverdueMatch || hasUnmatchedGuilds)
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

                        foreach (var match in activeMatches)
                        {
                            await DistributeVictoryTokensAsync(dbContext, match, stoppingToken);
                        }

                        var guilds = await dbContext.GuildRecords
                            .FromSqlRaw("SELECT * FROM \"GuildRecords\" FOR UPDATE")
                            .ToListAsync(stoppingToken);
                        var matched = new System.Collections.Generic.HashSet<long>();
                        int created = 0;

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
                                created++;
                            }
                        }

                        if (created > 0)
                        {
                            Console.WriteLine($"GuildWar: paired {created} match(es).");
                        }

                        await dbContext.SaveChangesAsync(stoppingToken);
                        await transaction.CommitAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // Modul: this used to be `catch (Exception)` with a bare
                        // rollback and no message. Guild war matchmaking could
                        // therefore fail on every single pass - forever - and
                        // the only symptom anywhere was that no wars happened,
                        // which is indistinguishable from "no window yet".
                        // Diagnosing the first-match gate meant reading the
                        // source because the log had nothing to say.
                        Console.WriteLine($"GuildWar matchmaking pass failed: {ex.Message}");
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

                    // A attacks B - Vodnik's CritMitigationPct on the defending
                    // side (B) reduces the crit multiplier, matching the same
                    // mitigation formula used against monster crits and in
                    // GuildCombatSimulationEngine.
                    float hitChanceA = Math.Clamp(100f / 100f, 0.05f, 0.95f); // Simplified attackerAccuracy / defenderDodge for aggregate
                    if (Random.Shared.NextDouble() <= hitChanceA)
                    {
                        float critMult = Random.Shared.NextDouble() <= (statsA.CritChancePct / 100.0f) ? Math.Max(1.0f, 1.5f - (statsB.CritMitigationPct / 100f)) : 1.0f;
                        long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in statsA, 0, 0);
                        int rawDamage = (int)(effectiveMilliAttack * critMult);
                        int netDamage = Math.Max(1000, rawDamage - (statsB.FlatPhysicalArmor * 1000));
                        hpB -= netDamage;
                    }

                    if (hpB <= 0) break;

                    // B attacks A - mitigated by A's CritMitigationPct.
                    float hitChanceB = Math.Clamp(100f / 100f, 0.05f, 0.95f);
                    if (Random.Shared.NextDouble() <= hitChanceB)
                    {
                        float critMult = Random.Shared.NextDouble() <= (statsB.CritChancePct / 100.0f) ? Math.Max(1.0f, 1.5f - (statsA.CritMitigationPct / 100f)) : 1.0f;
                        long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in statsB, 0, 0);
                        int rawDamage = (int)(effectiveMilliAttack * critMult);
                        int netDamage = Math.Max(1000, rawDamage - (statsA.FlatPhysicalArmor * 1000));
                        hpA -= netDamage;
                    }
                }

                if (hpA > hpB) match.CombatVanguardWP_A += 1000;
                else if (hpB > hpA) match.CombatVanguardWP_B += 1000;
            }
            catch (Exception ex)
            {
                // Modul: previously a silent catch (Exception) { } that
                // swallowed malformed RosterPayloadJson or any mid-simulation
                // fault with zero trace - a guild war match could resolve with
                // neither guild's Combat Vanguard WP incremented and no way to
                // diagnose why. Now logs a distinct diagnostic alert with the
                // full exception detail; the match's WP award for this phase is
                // still safely skipped (transaction-level rollback/commit is
                // handled by the caller, RunMatchmakingLoopAsync, which already
                // wraps this call in its own transaction).
                Console.WriteLine($"GUILD WAR COMBAT RESOLUTION FAILURE - MatchId {match.MatchId}, GuildA {match.GuildA_Id}, GuildB {match.GuildB_Id}: {ex}");
            }
        }

        // Modul 13.4.3: declares the overall match winner by summing all three
        // war fronts (Combat/Logistics/Supply Chain) and credits a flat
        // victory-token reward to the winning guild's GuildDepotBalances -
        // matching GuildLogisticsEngine's existing upsert-on-conflict pattern
        // for depositing materials into that same table. A tie awards nothing.
        private async Task DistributeVictoryTokensAsync(FolkIdleDbContext dbContext, GuildWarMatch match, CancellationToken stoppingToken)
        {
            int totalWpA = match.CombatVanguardWP_A + match.ProductionLogisticsWP_A + match.GatheringSupplyChainWP_A;
            int totalWpB = match.CombatVanguardWP_B + match.ProductionLogisticsWP_B + match.GatheringSupplyChainWP_B;

            long winningGuildId;
            if (totalWpA > totalWpB) winningGuildId = match.GuildA_Id;
            else if (totalWpB > totalWpA) winningGuildId = match.GuildB_Id;
            else return;

            var upsertDepotQuery = @"
                INSERT INTO ""GuildDepotBalances"" (""GuildId"", ""ItemDefinitionId"", ""Quantity"")
                VALUES ({0}, {1}, {2})
                ON CONFLICT (""GuildId"", ""ItemDefinitionId"")
                DO UPDATE SET ""Quantity"" = ""GuildDepotBalances"".""Quantity"" + {2};
            ";
            await dbContext.Database.ExecuteSqlRawAsync(upsertDepotQuery, winningGuildId, GuildWarVictoryTokenItemId, VictoryTokenReward);
        }
    }
}

using System;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public sealed class GuildMatchmakingEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();

        public GuildMatchmakingEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ExecuteAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTime now = DateTime.UtcNow;
                if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 23 && now.Minute == 30)
                {
                    await ExecutePairingCycleAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        public async Task ExecutePairingCycleAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

            try
            {
                var guilds = await db.GuildRecords
                    .FromSqlRaw("SELECT * FROM \"GuildRecords\" ORDER BY \"GuildMMR\" FOR UPDATE")
                    .ToListAsync(stoppingToken);
                var matched = new System.Collections.Generic.HashSet<long>();

                for (int i = 0; i < guilds.Count; i++)
                {
                    var attacker = guilds[i];
                    if (matched.Contains(attacker.Id))
                    {
                        continue;
                    }

                    GuildRecord? defender = null;
                    double bestDistance = double.MaxValue;
                    for (int j = 0; j < guilds.Count; j++)
                    {
                        var candidate = guilds[j];
                        if (candidate.Id == attacker.Id || matched.Contains(candidate.Id))
                        {
                            continue;
                        }

                        double distance = Math.Sqrt(Math.Pow(attacker.GuildMMR - candidate.GuildMMR, 2) +
                            0.35 * Math.Pow(attacker.ActiveMembers - candidate.ActiveMembers, 2));
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            defender = candidate;
                        }
                    }

                    if (defender == null)
                    {
                        continue;
                    }

                    matched.Add(attacker.Id);
                    matched.Add(defender.Id);

                    await UpsertDefenseRosterAsync(db, attacker, stoppingToken);
                    await UpsertDefenseRosterAsync(db, defender, stoppingToken);

                    long maxHp = CalculateScaledDefenderHp(defender);
                    int groupIndex = Math.Max(0, ((attacker.GuildMMR + defender.GuildMMR) / 2) / 500);
                    db.GuildMatchmakingSnapshots.Add(new GuildMatchmakingSnapshot
                    {
                        MatchUuid = Guid.NewGuid(),
                        AttackerGuildId = attacker.Id,
                        DefenderGuildId = defender.Id,
                        GlobalNodeMaxHp = maxHp,
                        GlobalNodeRemainingHp = maxHp,
                        TournamentGroupIndex = groupIndex,
                        ActiveMatchMmr = (attacker.GuildMMR + defender.GuildMMR) / 2,
                        FencingToken = 0L,
                        IsComplete = false
                    });
                }

                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }
            catch
            {
                await transaction.RollbackAsync(stoppingToken);
                throw;
            }
        }

        private static async Task UpsertDefenseRosterAsync(FolkIdleDbContext db, GuildRecord guild, CancellationToken stoppingToken)
        {
            var roster = await db.GuildDefenseRosters
                .FromSqlRaw("SELECT * FROM \"GuildDefenseRosters\" WHERE \"GuildId\" = {0} FOR UPDATE", guild.Id)
                .FirstOrDefaultAsync(stoppingToken);
            string payload = JsonSerializer.Serialize(new
            {
                guild.GuildMMR,
                guild.ActiveMembers,
                guild.CurrentTier,
                guild.MiningMonolithLevel,
                guild.WoodcuttingMonolithLevel
            });

            if (roster == null)
            {
                db.GuildDefenseRosters.Add(new GuildDefenseRoster
                {
                    GuildId = guild.Id,
                    RegionShardId = ResolveRegionShardId(guild.Id),
                    DefensiveStatsJson = payload
                });
                return;
            }

            roster.RegionShardId = ResolveRegionShardId(guild.Id);
            roster.DefensiveStatsJson = payload;
        }

        private static int ResolveRegionShardId(long guildId)
        {
            return (int)Math.Abs(guildId % 1024L);
        }

        private static long CalculateScaledDefenderHp(GuildRecord defender)
        {
            long baseHp = 100000L + defender.CurrentTier * 25000L + defender.ActiveMembers * 10000L;
            baseHp += defender.MiningMonolithLevel * 5000L + defender.WoodcuttingMonolithLevel * 5000L;
            return (long)Math.Ceiling(baseHp * 1.25);
        }
    }
}

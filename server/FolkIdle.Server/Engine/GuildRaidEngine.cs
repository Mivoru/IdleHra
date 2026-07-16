using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    // Co-op PvE guild raid boss background simulation. Distinct from GuildWarEngine /
    // GuildCombatSimulationEngine, which implement the unrelated PvP guild-vs-guild
    // turn-based war system.
    public class GuildRaidEngine
    {
        // Modul: sourced from GameData/GameBalanceConfig.json via
        // ContentRegistry (see GameBalanceDefinition) instead of hardcoded
        // constants, so tuning a raid balance value is a content deploy,
        // not a code deploy. Read once per property access rather than
        // cached in a field - ContentRegistry.Balance is itself a simple
        // property read, no allocation or I/O, so there is no benefit to
        // caching it locally.
        private static long RaidBossBaseHp => ContentRegistry.Balance.GuildRaidBossBaseHp;
        private static int DpsPerLevel => ContentRegistry.Balance.GuildRaidDpsPerLevel;
        private static int TickIntervalSeconds => ContentRegistry.Balance.GuildRaidTickIntervalSeconds;
        private static long RaidVictoryContributionPoints => ContentRegistry.Balance.GuildRaidVictoryContributionPoints;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;
        private CancellationTokenSource _cts = new();

        public GuildRaidEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TickIntervalSeconds * 1000, stoppingToken);

                try
                {
                    await ExecuteRaidTickAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Guild raid tick sweep failed: {ex.Message}");
                }
            }
        }

        private async Task ExecuteRaidTickAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            List<GuildRaidState> activeRaids = await db.GuildRaidStates
                .AsNoTracking()
                .Where(r => r.RaidBossCurrentHp > 0)
                .ToListAsync();

            if (activeRaids.Count == 0)
            {
                return;
            }

            long[] onlinePlayerIds = _playerRegistry.GetOnlinePlayerIds();

            foreach (GuildRaidState raid in activeRaids)
            {
                await ProcessGuildRaidTickAsync(db, raid, onlinePlayerIds);
            }
        }

        // Modul 18: bootstraps a guild's first GuildRaidState row so the passive
        // DPS cron in ExecuteRaidTickAsync has something to process. Once created,
        // that cron auto-advances every subsequent tier forever on its own (kill
        // the boss -> tier++, full HP, keep going) - there is no repeatable
        // "start next raid" action, so this is a no-op once a row already exists.
        public async Task TryStartRaidAsync(long guildId)
        {
            if (guildId <= 0) return;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var existing = await db.GuildRaidStates
                    .FromSqlRaw("SELECT * FROM \"GuildRaidStates\" WHERE \"GuildId\" = {0} FOR UPDATE", guildId)
                    .SingleOrDefaultAsync();

                if (existing != null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                const int initialTier = 1;
                long initialMaxHp = (long)(RaidBossBaseHp * Math.Pow(1.5, initialTier));

                var raid = new GuildRaidState
                {
                    GuildId = guildId,
                    RaidTier = initialTier,
                    RaidBossCurrentHp = initialMaxHp,
                    RaidBossMaxHp = initialMaxHp
                };
                db.GuildRaidStates.Add(raid);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.GuildRaidBossUpdateQueue.Enqueue(new GuildRaidBossUpdateNotification
                {
                    GuildId = guildId,
                    RaidTier = raid.RaidTier,
                    RaidBossCurrentHp = raid.RaidBossCurrentHp,
                    RaidBossMaxHp = raid.RaidBossMaxHp
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Guild raid launch failed for guild {guildId}: {ex.Message}");
            }
        }

        public async Task ProcessGuildRaidTickAsync(FolkIdleDbContext db, GuildRaidState raid, long[] onlinePlayerIds)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var lockedRaid = await db.GuildRaidStates
                    .FromSqlRaw("SELECT * FROM \"GuildRaidStates\" WHERE \"GuildId\" = {0} FOR UPDATE", raid.GuildId)
                    .SingleOrDefaultAsync();

                if (lockedRaid == null || lockedRaid.RaidBossCurrentHp <= 0)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                long guildDps = await db.PlayerRecords
                    .Where(p => p.GuildId == lockedRaid.GuildId && onlinePlayerIds.Contains(p.Id))
                    .SumAsync(p => (long)p.CurrentLevel * DpsPerLevel);

                if (guildDps <= 0)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                long damage = guildDps * TickIntervalSeconds;
                lockedRaid.RaidBossCurrentHp -= damage;

                if (lockedRaid.RaidBossCurrentHp <= 0)
                {
                    lockedRaid.RaidTier++;
                    lockedRaid.RaidBossMaxHp = (long)(RaidBossBaseHp * Math.Pow(1.5, lockedRaid.RaidTier));
                    lockedRaid.RaidBossCurrentHp = lockedRaid.RaidBossMaxHp;

                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"GuildMembers\" SET \"ContributionPoints\" = \"ContributionPoints\" + {0} WHERE \"GuildId\" = {1}",
                        RaidVictoryContributionPoints, lockedRaid.GuildId);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.GuildRaidBossUpdateQueue.Enqueue(new GuildRaidBossUpdateNotification
                {
                    GuildId = lockedRaid.GuildId,
                    RaidTier = lockedRaid.RaidTier,
                    RaidBossCurrentHp = lockedRaid.RaidBossCurrentHp,
                    RaidBossMaxHp = lockedRaid.RaidBossMaxHp
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Guild raid boss tick failed for guild {raid.GuildId}: {ex.Message}");
            }
        }
    }
}

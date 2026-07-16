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

        // Modul: Phase - Full-Stack Production Polish, Part 3.2. Gold cost
        // for a Guild Leader to manually start (or restart, after the
        // previous tier's boss was defeated) the next raid tier, deducted
        // from the requesting leader's own personal gold. Scales linearly
        // with the tier being entered so later, harder tiers cost more to
        // launch - deliberately not an exponential curve like the boss HP
        // itself, since this is a launch fee, not the raid's core
        // difficulty knob.
        public const long RestartRaidBaseGoldCost = 5000L;

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

        // Modul: Phase - Full-Stack Production Polish, Part 3.2. Previously
        // bootstrapped only the guild's FIRST GuildRaidState row (a no-op
        // once one existed) and then relied entirely on the passive DPS
        // cron in ExecuteRaidTickAsync/ProcessGuildRaidTickAsync to
        // auto-advance every subsequent tier forever on its own (kill the
        // boss -> tier++, full HP, keep going, with no player action ever
        // required again). ProcessGuildRaidTickAsync no longer auto-
        // advances - a defeated boss's HP stays at 0 and the tick sweep's
        // own RaidBossCurrentHp > 0 filter naturally stops processing it -
        // so this method is now the ONLY way a raid tier starts or
        // restarts, gated on the requesting player actually being this
        // guild's Leader (checked against the locked GuildMembers row, not
        // a client-supplied claim) and consuming RestartRaidBaseGoldCost *
        // nextTier gold from that leader's own personal balance.
        public async Task TryStartRaidAsync(long guildId, long requestingPlayerId)
        {
            if (guildId <= 0 || requestingPlayerId <= 0) return;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var requester = await db.GuildMembers
                    .FromSqlRaw("SELECT * FROM \"GuildMembers\" WHERE \"GuildId\" = {0} AND \"PlayerId\" = {1} FOR UPDATE", guildId, requestingPlayerId)
                    .SingleOrDefaultAsync();

                if (requester == null || requester.Role != GuildManagementEngine.RoleLeader)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                var existing = await db.GuildRaidStates
                    .FromSqlRaw("SELECT * FROM \"GuildRaidStates\" WHERE \"GuildId\" = {0} FOR UPDATE", guildId)
                    .SingleOrDefaultAsync();

                if (existing != null && existing.RaidBossCurrentHp > 0)
                {
                    // Already active - nothing to (re)start.
                    await transaction.RollbackAsync();
                    return;
                }

                int nextTier = (existing?.RaidTier ?? 0) + 1;
                long cost = RestartRaidBaseGoldCost * nextTier;

                var goldRecord = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", requestingPlayerId)
                    .SingleOrDefaultAsync();

                if (goldRecord == null || goldRecord.Quantity < cost)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                goldRecord.Quantity -= cost;

                long newMaxHp = (long)(RaidBossBaseHp * Math.Pow(1.5, nextTier));

                if (existing == null)
                {
                    existing = new GuildRaidState { GuildId = guildId };
                    db.GuildRaidStates.Add(existing);
                }

                existing.RaidTier = nextTier;
                existing.RaidBossMaxHp = newMaxHp;
                existing.RaidBossCurrentHp = newMaxHp;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.GuildRaidBossUpdateQueue.Enqueue(new GuildRaidBossUpdateNotification
                {
                    GuildId = guildId,
                    RaidTier = existing.RaidTier,
                    RaidBossCurrentHp = existing.RaidBossCurrentHp,
                    RaidBossMaxHp = existing.RaidBossMaxHp
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

                long previousHp = lockedRaid.RaidBossCurrentHp;
                long damage = guildDps * TickIntervalSeconds;
                lockedRaid.RaidBossCurrentHp -= damage;

                if (lockedRaid.RaidBossCurrentHp <= 0 && previousHp > 0)
                {
                    // Modul: no automatic tier advance or HP reset here
                    // anymore (see TryStartRaidAsync's own comment) - the
                    // boss stays defeated (HP clamped to exactly 0) until a
                    // Guild Leader spends gold to manually start the next
                    // tier. ExecuteRaidTickAsync's own RaidBossCurrentHp > 0
                    // filter means this raid simply stops being ticked from
                    // here on, with no special "idle" state needed.
                    lockedRaid.RaidBossCurrentHp = 0;

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

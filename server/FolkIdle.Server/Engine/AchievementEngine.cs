using System;
using System.Linq;
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
    public class AchievementEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _registry;
        private CancellationTokenSource _cts = new();

        public AchievementEngine(IServiceProvider serviceProvider, PlayerSessionRegistry registry)
        {
            _serviceProvider = serviceProvider;
            _registry = registry;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Task.Run(() => ProcessClaimsQueueAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(15000, stoppingToken);

                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process achievements: {ex.Message}");
                }
            }
        }

        // Modul: who counts as "active", 2026-08-02.
        //
        // This used to select the sweep set with:
        //
        //   .Where(p => p.LastLogoutTimestamp == 0 ||
        //               (Environment.TickCount64 - p.LastLogoutTimestamp) < 60000)
        //
        // which subtracts unix SECONDS from milliseconds-since-boot. On any
        // machine that has not been up for about fifty-six years the
        // difference is hugely negative, so the predicate was true for every
        // row: this loaded EVERY account in the database every 15 seconds and
        // opened a Serializable transaction per row. Invisible at nine
        // players and a wall at any real scale.
        //
        // THE OBVIOUS REPAIR - just fix the units - IS WRONG, and that is
        // worth stating here so nobody "corrects" it back.
        // LastLogoutTimestamp is written at LOGIN as well as at logout (see
        // OfflineSimulationEngine.ExtrapolateOfflineProgressAsync), so it
        // means "last session boundary", not "last time this player was
        // seen". A genuine 60-second window over it would exclude everyone
        // who has been online for more than a minute - precisely the players
        // actively earning these achievements - and the accidental
        // match-everything behaviour is the only reason achievements have
        // ever been granted at all. Fixing the units alone would have turned
        // a scalability bug into a silent correctness bug, which is strictly
        // worse.
        //
        // So the heuristic is replaced with the real answer, which this class
        // already held a reference to: the live session registry. Bounded by
        // concurrent players rather than total accounts, and correct by
        // construction rather than by arithmetic accident.
        //
        // Restricting to online players loses nothing. Gold, village levels
        // and population only move through the player's own actions, and
        // offline catch-up is applied at login - while they are online. The
        // one guild-wide condition (depot donations) can cross its threshold
        // while a member is away, and that member is now credited within 15
        // seconds of coming back rather than while logged off, which is also
        // when they can actually see the diamonds arrive.
        internal async Task<int> SweepOnceAsync(CancellationToken stoppingToken)
        {
            long[] onlinePlayerIds = _registry?.GetOnlinePlayerIds() ?? Array.Empty<long>();
            if (onlinePlayerIds.Length == 0)
            {
                return 0;
            }

            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

            var activePlayers = await dbContext.PlayerRecords
                .Where(p => onlinePlayerIds.Contains(p.Id))
                .ToListAsync(stoppingToken);

            foreach (var player in activePlayers)
            {
                // Modul: each player's transaction is its own retry
                // unit - a Serializable conflict on player N retries
                // only player N's attempt, not the whole batch.
                var strategy = dbContext.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    // player was loaded outside this retry boundary
                    // and is mutated below - re-attach after
                    // clearing so PremiumDiamonds changes are not
                    // silently dropped from SaveChangesAsync.
                    dbContext.ChangeTracker.Clear();
                    dbContext.Attach(player);

                    using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, stoppingToken);

                    var achievementRecord = await dbContext.PlayerAchievements.FindAsync(new object[] { player.Id }, stoppingToken);
                    if (achievementRecord == null)
                    {
                        achievementRecord = new PlayerAchievement { PlayerId = player.Id, ClaimedAchievementFlags = 0 };
                        dbContext.PlayerAchievements.Add(achievementRecord);
                    }

                    int currentFlags = achievementRecord.ClaimedAchievementFlags;
                    int newFlags = currentFlags;
                    int diamondsToAward = 0;

                    // Treasury: CurrentGold >= 100000
                    var goldRecord = await dbContext.CommodityRecords
                        .FirstOrDefaultAsync(c => c.PlayerId == player.Id && c.ItemId == "gold", stoppingToken);
                    long currentGold = goldRecord?.Quantity ?? 0;
                    if ((currentFlags & (1 << 0)) == 0 && currentGold >= 100000)
                    {
                        newFlags |= (1 << 0);
                        diamondsToAward += 100;
                    }

                    // Engineering & Demographic
                    var infrastructureRows = await dbContext.VillageInfrastructures
                        .AsNoTracking()
                        .Where(v => v.PlayerId == player.Id)
                        .ToListAsync(stoppingToken);

                    if (infrastructureRows.Count > 0)
                    {
                        int engineeringScore = infrastructureRows.Sum(v => v.CurrentLevel);
                        if ((currentFlags & (1 << 1)) == 0 && engineeringScore >= 10)
                        {
                            newFlags |= (1 << 1);
                            diamondsToAward += 100;
                        }

                        // Modul: VillageResidents has no writer anywhere, so this
                        // achievement could never fire. Population is the
                        // player's characters - see VillageManagementEngine.
                        int population = await dbContext.CharacterRecords
                            .AsNoTracking()
                            .CountAsync(c => c.PlayerId == player.Id && !c.IsLockedInEscrow, stoppingToken);
                        if ((currentFlags & (1 << 2)) == 0 && population >= 50)
                        {
                            newFlags |= (1 << 2);
                            diamondsToAward += 100;
                        }
                    }

                    // Logistics: Guild Depot
                    if (player.GuildId > 0 && (currentFlags & (1 << 3)) == 0)
                    {
                        long totalDonations = await dbContext.GuildDepotBalances
                            .Where(g => g.GuildId == player.GuildId)
                            .SumAsync(g => (long)g.Quantity, stoppingToken);

                        if (totalDonations >= 10000)
                        {
                            newFlags |= (1 << 3);
                            diamondsToAward += 100;
                        }
                    }

                    if (newFlags != currentFlags)
                    {
                        achievementRecord.ClaimedAchievementFlags = newFlags;
                        player.PremiumDiamonds += diamondsToAward;
                        await dbContext.SaveChangesAsync(stoppingToken);
                    }

                    await transaction.CommitAsync(stoppingToken);

                    // Modul: achievement reward sync, 2026-08-02.
                    //
                    // Writing PlayerRecords."PremiumDiamonds" is not
                    // enough for an ONLINE player. The live payload owns
                    // PremiumCurrency and StateCheckpointManager writes
                    // it back with plain assignment
                    // (player.PremiumDiamonds = state.PremiumCurrency),
                    // so the next flush overwrote the reward with the
                    // payload's stale balance and the diamonds silently
                    // vanished. Offline players were unaffected, which is
                    // exactly what made it hard to notice.
                    //
                    // Identical shape to the reroll diamond bug fixed on
                    // 2026-08-01, and fixed the same way: hand the
                    // authoritative balance to the tick thread, which is
                    // the only thread allowed to touch the payload.
                    if (diamondsToAward > 0)
                    {
                        _registry?.BillingSyncQueue.Enqueue(new BillingSyncNotification
                        {
                            PlayerId = player.Id,
                            PremiumDiamondsBalance = player.PremiumDiamonds
                        });
                    }
                });
            }

            return activePlayers.Count;
        }

        // Modul: CLAIMS PAY NOTHING ANY MORE (task 57). Treasury, Master Smith
        // and Logistics are paid by the checkpoint the moment a tier is crossed
        // (StateCheckpointManager.UpsertLifetimeAchievementsAsync), and this
        // path then paid the SAME tiers again, once, off the claim button the
        // Progress screen offered because IsClaimed was never set for them.
        // Monster Slayer's reward table was empty, so its claim paid nothing at
        // all. All four are automatic now - the Book of Deeds pays Monster
        // Slayer on read (LifetimeAchievementBank) - so a claim from an old
        // client is drained and ignored rather than left to grow the queue.
        private async Task ProcessClaimsQueueAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_registry.AchievementClaimQueue.TryDequeue(out var req))
                    {
                        Console.WriteLine($"[AchievementEngine] ignored a retired claim for achievement {req.AchievementId} from player {req.PlayerId}");
                    }
                    else
                    {
                        await Task.Delay(250, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AchievementEngine] claim drain failed: {ex.Message}");
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Data;
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
    public sealed class SeasonalRotationEngine
    {
        private const long EraDurationSeconds = 90L * 24L * 60L * 60L;
        private const int PlayerBatchSize = 100;
        private const double LegacyShardFloorEpsilon = 0.000000001;

        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();

        public SeasonalRotationEngine(IServiceProvider serviceProvider)
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
                try
                {
                    await ExecuteEraCheckAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Seasonal rotation failed: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        private async Task ExecuteEraCheckAsync(CancellationToken stoppingToken)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int closedEraId = 0;

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

                var activeEra = await db.SeasonalEraRecords
                    .FromSqlRaw("SELECT * FROM \"SeasonalEraRecords\" WHERE \"IsActive\" = TRUE ORDER BY \"EndTimestamp\" LIMIT 1 FOR UPDATE")
                    .FirstOrDefaultAsync(stoppingToken);

                if (activeEra == null)
                {
                    db.SeasonalEraRecords.Add(new SeasonalEraRecord
                    {
                        EndTimestamp = now + EraDurationSeconds,
                        IsActive = true
                    });
                    await db.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                    return;
                }

                if (activeEra.EndTimestamp > now)
                {
                    await transaction.CommitAsync(stoppingToken);
                    return;
                }

                activeEra.IsActive = false;
                closedEraId = activeEra.EraId;
                db.SeasonalEraRecords.Add(new SeasonalEraRecord
                {
                    EndTimestamp = now + EraDurationSeconds,
                    IsActive = true
                });

                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }

            if (closedEraId <= 0)
            {
                return;
            }

            GlobalEngineState.IsEraTransitionActive = true;
            
            var networkSystem = _serviceProvider.GetService<FolkIdle.Server.Network.NetworkBroadcastSystem>();
            if (networkSystem != null)
            {
                await networkSystem.DisconnectAllClientsGracefullyAsync();
            }
            try
            {
                await ExecutePlayerRolloversAsync(closedEraId, stoppingToken);
            }
            finally
            {
                GlobalEngineState.IsEraTransitionActive = false;
            }
        }

        // Test-only observability (via InternalsVisibleTo) so
        // FolkIdle.Server.Tests can directly exercise the era-close
        // rollover without waiting on the real 90-day EraDurationSeconds
        // clock or the 5-minute cron poll.
        internal async Task ExecutePlayerRolloversAsync(int closedEraId, CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

            try
            {
                var playerIds = await db.PlayerRecords
                    .AsNoTracking()
                    .OrderBy(p => p.Id)
                    .Select(p => p.Id)
                    .ToListAsync(stoppingToken);

                var newLedgers = new System.Collections.Concurrent.ConcurrentBag<PlayerLegacyLedger>();

                var chunks = playerIds.Chunk(PlayerBatchSize).ToArray();
                foreach (var chunk in chunks)
                {
                    var chunkIds = chunk.ToList();
                    var goldDict = await db.CommodityRecords
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId) && c.ItemId == "gold")
                        .ToDictionaryAsync(c => c.PlayerId, c => c.Quantity, stoppingToken);
                        
                    var charsByPlayer = await db.CharacterRecords
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId))
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);
                        
                    var eqByPlayer = await db.EquipmentInstances
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId))
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);
                        
                    var bankByPlayer = await db.BankEquipmentInstances
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId))
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);
                        
                    var marketByPlayer = await db.MarketEquipmentInstances
                        .AsNoTracking()
                        .Where(c => chunkIds.Contains(c.PlayerId) && !c.IsLockedInEscrow)
                        .GroupBy(c => c.PlayerId)
                        .ToDictionaryAsync(g => g.Key, g => g.ToList(), stoppingToken);

                    var ledgers = await db.PlayerLegacyLedgers
                        .Where(l => l.EraId == closedEraId && chunkIds.Contains(l.PlayerId))
                        .ToDictionaryAsync(l => l.PlayerId, stoppingToken);

                    foreach (var playerId in chunk)
                    {
                        long levelSquareSum = 0L;
                        if (charsByPlayer.TryGetValue(playerId, out var characters))
                        {
                            foreach (var ch in characters)
                            {
                                long level = Math.Max(1, ch.Level);
                                levelSquareSum += level * level;
                            }
                        }

                        var eq = eqByPlayer.GetValueOrDefault(playerId, new List<EquipmentInstance>());
                        var bEq = bankByPlayer.GetValueOrDefault(playerId, new List<BankEquipmentInstance>());
                        var mEq = marketByPlayer.GetValueOrDefault(playerId, new List<MarketEquipmentInstance>());
                        long inventoryScore = CalculateInventoryScore(eq, bEq, mEq);

                        long totalGold = Math.Max(0L, goldDict.GetValueOrDefault(playerId, 0L));
                        int shardsEarned = CalculateLegacyShards(totalGold, levelSquareSum, inventoryScore);

                        int inheritedSlots = await LoadUnlockedSlotMaskAsync(db, playerId, stoppingToken);
                        
                        if (ledgers.TryGetValue(playerId, out var ledger))
                        {
                            ledger.LegacyShardBalance = SafeAdd(ledger.LegacyShardBalance, shardsEarned);
                        }
                        else
                        {
                            var newLedger = new PlayerLegacyLedger
                            {
                                PlayerId = playerId,
                                EraId = closedEraId,
                                LegacyShardBalance = shardsEarned,
                                CitizenMultiSlotsUnlocked = inheritedSlots
                            };
                            db.PlayerLegacyLedgers.Add(newLedger);
                        }
                    }

                    await db.SaveChangesAsync(stoppingToken); // save ledgers per chunk to avoid memory bloat
                }

                // Modul: AND WHAT THE SEASON'S PLACING WAS WORTH.
                //
                // Everything above pays out for what a player ACCUMULATED -
                // gold, levels, gear. This pays for where they FINISHED, which
                // is a different thing and the only reason a leaderboard is
                // worth looking at twice.
                //
                // Ranked by the same rule the live board uses, and for the
                // same reason: a season that ends on a different ordering than
                // the one players watched all season is a broken promise.
                // Level first, then the hardest monster they ever put down,
                // then how many times.
                //
                // One query, not one per player: this runs inside the era
                // transition with every client disconnected, but a per-player
                // round trip over the whole roster is how a five-minute
                // maintenance window becomes an hour.
                await AwardPlacementRewardsAsync(db, closedEraId, stoppingToken);

                // Bulk Updates & Truncations within the same transaction
                // Modul: Play Mode audit fix. EquippedWeaponId/ArmorId/
                // LeggingsId were never cleared here even though the
                // TRUNCATE ... RESTART IDENTITY below wipes and recycles
                // EquipmentInstances' ids from 1 - a genuinely severe bug,
                // not cosmetic: EquipmentSlotEngine.ComputeEquippedTotalsAsync
                // looks up equipped items by Id alone with no PlayerId
                // ownership check, so once any post-reset player's newly
                // crafted item happened to land on a stale EquippedWeaponId/
                // ArmorId/LeggingsId value, every other player still holding
                // that same stale id would silently start showing that
                // stranger's item stats/set bonus as their own equipped
                // gear. Must null these out in the same statement/
                // transaction as the level/gold reset below, before the
                // TRUNCATE recycles the id space.
                await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerRecords\" SET \"CurrentLevel\" = 1, \"CurrentXp\" = 0, \"AccumulatedTimeBankSeconds\" = 0, \"ActiveOffensivePotionId\" = 0, \"OffensivePotionDurationMs\" = 0, \"ActiveDefensivePotionId\" = 0, \"DefensivePotionDurationMs\" = 0, \"BankedChronoSeconds\" = 0, \"IsChronoAccelerating\" = FALSE, \"FreeRespecUsed\" = FALSE, \"AvailableSkillPoints\" = 0", stoppingToken);

                // Modul: THE SKILL TREE DID NOT RESET, AND WAS ALWAYS MEANT TO.
                //
                // PlayerSkillTreeNode's own doc comment says "Levels RESET WITH
                // THE SEASON" and explains why - points come from account
                // levels and the rollover takes those back, so a tree that
                // survived would be paid for twice. Nothing implemented it.
                // Neither the rows nor AvailableSkillPoints were ever cleared.
                //
                // Left alone, a player finishes season one with ~100 points
                // spent, re-levels to 100 in season two and spends ~100 MORE on
                // top of a tree still standing. By the third season the whole
                // 215-point tree is bought and the choice is gone permanently -
                // and with ring 2 exclusive, the fork they did not take is
                // locked forever rather than for a season.
                //
                // TRUNCATE rather than DELETE for the same reason as the tables
                // above: it deallocates pages directly instead of writing a
                // tombstone per row.
                await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"player_skill_tree\" RESTART IDENTITY", stoppingToken);

                // Modul: the free respec comes back with the season, and
                // PAID GRANTS DELIBERATELY DO NOT RESET. They are bought, so
                // an unspent one has to survive a rollover - wiping it would
                // be taking something a player paid real money for. Only the
                // free one is a per-season allowance. See PlayerRecord.

                // Modul: per-character equipment. The six equip pointers moved
                // off "PlayerRecords" onto "characters", so the seasonal wipe
                // needs a second statement or every character would come out of
                // the rollover still pointing at gear the wipe deleted.
                await db.Database.ExecuteSqlRawAsync("UPDATE \"characters\" SET \"EquippedWeaponId\" = NULL, \"EquippedHelmetId\" = NULL, \"EquippedChestId\" = NULL, \"EquippedGlovesId\" = NULL, \"EquippedLeggingsId\" = NULL, \"EquippedBootsId\" = NULL, \"EquippedAmuletId\" = NULL, \"EquippedRingId\" = NULL", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE \"CommodityRecords\" SET \"Quantity\" = 0 WHERE \"ItemId\" = 'gold'", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"ItemId\" <> 'gold'", stoppingToken);

                // Modul 41: unconditional full-table wipes use TRUNCATE ...
                // RESTART IDENTITY CASCADE rather than DELETE FROM. Unlike
                // DELETE, TRUNCATE deallocates pages directly and produces a
                // small, fixed WAL footprint regardless of row count, avoiding
                // WAL bloat and the long-held-lock/gateway-timeout risk of
                // per-row tombstones on large tables at season-reset scale.
                // CASCADE is a no-op safety net here (this schema does not
                // enforce real FK constraints on these tables) but protects
                // against a future FK addition silently breaking this reset.
                await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"EquipmentInstances\", \"BankEquipmentInstances\" RESTART IDENTITY CASCADE", stoppingToken);

                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketOrderRecords\" o USING \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE AND o.\"Status\" = 0 AND o.\"OrderType\" = 'SELL'", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE \"MarketOrderRecords\" o SET \"EquipmentInstanceId\" = NULL FROM \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE", stoppingToken);
                // Modul 41: this TRUNCATE must run after the two statements
                // above, which still need to query MarketEquipmentInstances -
                // truncating it earlier would leave those queries with nothing
                // to match against.
                await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"MarketEquipmentInstances\" RESTART IDENTITY CASCADE", stoppingToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE characters SET \"Level\" = 1, \"AgeTicks\" = 0, \"AgePhase\" = 1", stoppingToken);
                // Modul: WHAT A SEASON LEAVES BEHIND.
                //
                // The village and race mastery used to be wiped with everything
                // else, which made a rollover pure loss - three months of work
                // and nothing to show a returning player that they had ever
                // played. The design has always been that these carry: the
                // season resets the RACE, not the account.
                //
                // Three things now survive a rollover, and the list is
                // deliberately short so that what carries stays legible:
                //
                //   VillageInfrastructures   what you built
                //   player_race_masteries    what you learned
                //   player_inheritance_stats what you bought (diamonds)
                //
                // Everything else in this method still goes. Levels, gear,
                // gold, materials, the market and the chronicle pass all reset,
                // because the season is the ladder and the ladder is the game.
                //
                // player_race_unlocks was never in this method and stays out:
                // a race you have earned is yours.
                await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerChroniclePasses\" SET \"PassLevel\" = 0, \"AccumulatedXp\" = 0, \"ClaimedMilestonesBitmask\" = 0", stoppingToken);

                await transaction.CommitAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(stoppingToken);
                Console.WriteLine($"SEASONAL RESET FAILURE - EraId {closedEraId}: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Ranks the whole roster the way the live leaderboard does and pays
        /// the placement table into PremiumDiamonds.
        ///
        /// Diamonds rather than gold or shards - see SeasonPlacementRewards
        /// for why. In short: gold is wiped at the rollover and shards have no
        /// shop, so neither can be a prize.
        /// </summary>
        internal static async Task AwardPlacementRewardsAsync(
            FolkIdleDbContext db,
            int closedEraId,
            CancellationToken stoppingToken)
        {
            // The same ordering as LeaderboardCronEngine, expressed against
            // the same tables. Quarantined accounts are excluded there and are
            // excluded here: a season they were not simulating is not a season
            // they placed in.
            var standings = await db.Database
                .SqlQueryRaw<PlacementRow>(@"
                    SELECT p.""Id"" AS ""PlayerId""
                    FROM ""PlayerRecords"" p
                    LEFT JOIN LATERAL (
                        SELECT c.""MonsterId"", c.""KillCount""
                        FROM ""monster_codex_entries"" c
                        WHERE c.""PlayerId"" = p.""Id"" AND c.""KillCount"" >= 1
                        ORDER BY c.""MonsterId"" DESC
                        LIMIT 1
                    ) m ON TRUE
                    WHERE NOT p.""IsQuarantined"" AND NOT p.""Quarantine_Active""
                    ORDER BY p.""CurrentLevel"" DESC,
                             COALESCE(m.""MonsterId"", 0) DESC,
                             COALESCE(m.""KillCount"", 0) DESC")
                .ToListAsync(stoppingToken);

            if (standings.Count == 0)
            {
                return;
            }

            // Grouped by reward so the whole roster costs a handful of
            // statements rather than one per player.
            var byReward = new Dictionary<int, List<long>>();
            for (int i = 0; i < standings.Count; i++)
            {
                int diamonds = SeasonPlacementRewards.DiamondsForRank(i + 1);
                if (diamonds <= 0) continue;

                if (!byReward.TryGetValue(diamonds, out var bucket))
                {
                    bucket = new List<long>();
                    byReward[diamonds] = bucket;
                }
                bucket.Add(standings[i].PlayerId);
            }

            foreach (var (diamonds, playerIds) in byReward)
            {
                await db.Database.ExecuteSqlRawAsync(
                    @"UPDATE ""PlayerRecords"" SET ""PremiumDiamonds"" = ""PremiumDiamonds"" + {0}
                      WHERE ""Id"" = ANY({1})",
                    new object[] { diamonds, playerIds.ToArray() },
                    stoppingToken);
            }

            // The top of the board is worth saying out loud - it is the one
            // moment in a season where a name means something to everyone else.
            for (int i = 0; i < standings.Count && i < 3; i++)
            {
                string who = await PlayerNameResolver.GetAsync(standings[i].PlayerId);
                Domain.Social.ChatEngine.EnqueueSystemAnnouncement(
                    $"Season {closedEraId} is over. {who} finished #{i + 1} " +
                    $"({SeasonPlacementRewards.BandNameForRank(i + 1)}). Congratulations!");
            }
        }

        /// <summary>One player's final standing, straight off the query.</summary>
        public sealed class PlacementRow
        {
            public long PlayerId { get; set; }
        }

        public static int CalculateLegacyShards(long totalGold, long characterLevelSquareSum, long inventoryScore)
        {
            double goldTerm = 12.5 * Math.Log10(Math.Max(0.0, (double)totalGold) + 1.0);
            double levelTerm = 0.05 * Math.Max(0.0, (double)characterLevelSquareSum);
            double inventoryTerm = 1.50 * Math.Max(0.0, (double)inventoryScore);
            double raw = Math.Floor(goldTerm + levelTerm + inventoryTerm + LegacyShardFloorEpsilon);
            if (raw <= 0.0) return 0;
            if (raw >= int.MaxValue) return int.MaxValue;
            return (int)raw;
        }

        private static long CalculateInventoryScore(List<EquipmentInstance> equipment, List<BankEquipmentInstance> bankEquipment, List<MarketEquipmentInstance> marketEquipment)
        {
            long score = 0L;
            for (int i = 0; i < equipment.Count; i++) score += Math.Max(1, equipment[i].QualityTier);
            for (int i = 0; i < bankEquipment.Count; i++) score += Math.Max(1, bankEquipment[i].QualityTier);
            for (int i = 0; i < marketEquipment.Count; i++) score += Math.Max(1, marketEquipment[i].QualityTier);
            return score;
        }

        private static int SafeAdd(int left, int right)
        {
            long value = (long)left + right;
            if (value <= 0L) return 0;
            if (value >= int.MaxValue) return int.MaxValue;
            return (int)value;
        }

        private static async Task<int> LoadUnlockedSlotMaskAsync(FolkIdleDbContext db, long playerId, CancellationToken stoppingToken)
        {
            var ledgers = await db.PlayerLegacyLedgers
                .AsNoTracking()
                .Where(l => l.PlayerId == playerId)
                .Select(l => l.CitizenMultiSlotsUnlocked)
                .ToListAsync(stoppingToken);

            int mask = 0;
            for (int i = 0; i < ledgers.Count; i++)
            {
                mask |= ledgers[i];
            }
            return mask;
        }
    }
}

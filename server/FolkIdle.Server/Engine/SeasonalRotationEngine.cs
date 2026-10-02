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

        // Modul: THE ADMIN'S "END THE SEASON NOW" (2026-09-28). The request
        // cannot run the rollover itself: the rollover disconnects every
        // client, the caller included, and takes as long as the roster does.
        // So it sets this and wakes the loop, and the rollover runs on the
        // cron's own path - the one the tests already cover. Static because
        // the HTTP layer has no reference to this instance; tests that set it
        // must clear it (CLAUDE.md, "the queues are STATIC").
        private static int _endNowRequested;
        private static readonly SemaphoreSlim Wake = new(0, 1);

        /// <summary>Ends the active season at the next loop pass, paused or not.</summary>
        public static void RequestEndNow()
        {
            Interlocked.Exchange(ref _endNowRequested, 1);
            if (Wake.CurrentCount == 0)
            {
                try { Wake.Release(); } catch (SemaphoreFullException) { }
            }
        }

        internal static bool EndNowPending => Volatile.Read(ref _endNowRequested) == 1;

        internal static void ClearEndNowForTests() => Interlocked.Exchange(ref _endNowRequested, 0);

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

                // Five minutes, or at once when an admin asks for the end.
                await Wake.WaitAsync(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        internal async Task ExecuteEraCheckAsync(CancellationToken stoppingToken)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool forced = Interlocked.Exchange(ref _endNowRequested, 0) == 1;
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
                    // An era still has to exist: the shard ledgers are keyed
                    // on it, rebirth included. Created paused, since a date no
                    // longer ends one (below).
                    db.SeasonalEraRecords.Add(new SeasonalEraRecord
                    {
                        EndTimestamp = now + EraDurationSeconds,
                        IsActive = true,
                        IsRolloverPaused = true
                    });
                    await db.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);
                    return;
                }

                // Modul: THE CALENDAR NO LONGER ENDS A SEASON (task 88, owner
                // decision 2026-09-30). A player ends their own run with a
                // rebirth (RebirthEngine), whenever they choose; the date on
                // the era is kept for the admin panel and ignored here, paused
                // or not. The only global rollover left is the admin's typed
                // "END SEASON" - an explicit act by the owner, not a clock.
                // SeasonControlTests pins both halves.
                if (!forced)
                {
                    await transaction.CommitAsync(stoppingToken);
                    return;
                }

                activeEra.IsActive = false;
                closedEraId = activeEra.EraId;
                db.SeasonalEraRecords.Add(new SeasonalEraRecord
                {
                    EndTimestamp = now + EraDurationSeconds,
                    IsActive = true,
                    // A paused season that the admin ends by hand starts a
                    // paused one - pausing is a standing decision, not a
                    // one-season exception.
                    IsRolloverPaused = activeEra.IsRolloverPaused
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
                await AwardLegacyShardsAsync(db, closedEraId, onlyPlayerId: null, stoppingToken);

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
                //
                // GLOBAL ONLY. A rebirth (task 88) is one player leaving a
                // roster that is not ending, so there is nothing to place in.
                await AwardPlacementRewardsAsync(db, closedEraId, stoppingToken);

                await ResetPlayersAsync(db, onlyPlayerId: null, stoppingToken);

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
        /// Pays the season's legacy shards into each player's ledger row for
        /// <paramref name="eraId"/> - every player, or only
        /// <paramref name="onlyPlayerId"/> (a rebirth). Returns the shards paid
        /// to that one player, or 0 for the whole-roster run.
        /// </summary>
        /// <remarks>
        /// Modul: ONE CODE PATH FOR BOTH (task 88). A rebirth is this rollover
        /// for a single player; a second copy of the shard formula, the wipe
        /// list or the cull would be two sources of one truth, which is this
        /// codebase's dominant bug class. The caller owns the transaction.
        /// </remarks>
        internal static async Task<int> AwardLegacyShardsAsync(
            FolkIdleDbContext db,
            int eraId,
            long? onlyPlayerId,
            CancellationToken stoppingToken)
        {
            int paidToOnlyPlayer = 0;

            // Modul: the level comes off PlayerRecords, which is where a
            // player's level has always actually lived. This block used to
            // sum level-squared across CharacterRecords - a column nothing
            // in the server ever wrote outside the dev fixture - so on the
            // live box every term was 1 and the season's whole level
            // component was a COUNT OF CHARACTERS. A level-88 account and a
            // fresh one with the same roster size earned identical shards.
            var playerQuery = db.PlayerRecords.AsNoTracking();
            if (onlyPlayerId.HasValue)
            {
                long only = onlyPlayerId.Value;
                playerQuery = playerQuery.Where(p => p.Id == only);
            }

            var players = await playerQuery
                .OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.CurrentLevel })
                .ToListAsync(stoppingToken);

            var levelByPlayer = players.ToDictionary(p => p.Id, p => p.CurrentLevel);
            var playerIds = players.Select(p => p.Id).ToList();

            var chunks = playerIds.Chunk(PlayerBatchSize).ToArray();
            foreach (var chunk in chunks)
            {
                var chunkIds = chunk.ToList();
                var goldDict = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => chunkIds.Contains(c.PlayerId) && c.ItemId == "gold")
                    .ToDictionaryAsync(c => c.PlayerId, c => c.Quantity, stoppingToken);

                var eqByPlayer = await db.EquipmentInstances
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
                    .Where(l => l.EraId == eraId && chunkIds.Contains(l.PlayerId))
                    .ToDictionaryAsync(l => l.PlayerId, stoppingToken);

                foreach (var playerId in chunk)
                {
                    // Squared, as before, so the reward stays superlinear
                    // in how far the season was actually pushed.
                    long playerLevel = Math.Max(1, levelByPlayer.GetValueOrDefault(playerId, 1));
                    long levelSquareSum = playerLevel * playerLevel;

                    var eq = eqByPlayer.GetValueOrDefault(playerId, new List<EquipmentInstance>());
                    var mEq = marketByPlayer.GetValueOrDefault(playerId, new List<MarketEquipmentInstance>());
                    long inventoryScore = CalculateInventoryScore(eq, mEq);

                    long totalGold = Math.Max(0L, goldDict.GetValueOrDefault(playerId, 0L));
                    int shardsEarned = CalculateLegacyShards(totalGold, levelSquareSum, inventoryScore);
                    if (onlyPlayerId.HasValue && playerId == onlyPlayerId.Value)
                    {
                        paidToOnlyPlayer = shardsEarned;
                    }

                    int inheritedSlots = await LoadUnlockedSlotMaskAsync(db, playerId, stoppingToken);

                    if (ledgers.TryGetValue(playerId, out var ledger))
                    {
                        // A second rebirth in the same era lands here - the
                        // ledger is keyed (player, era), and SafeAdd stacks.
                        ledger.LegacyShardBalance = SafeAdd(ledger.LegacyShardBalance, shardsEarned);
                    }
                    else
                    {
                        var newLedger = new PlayerLegacyLedger
                        {
                            PlayerId = playerId,
                            EraId = eraId,
                            LegacyShardBalance = shardsEarned,
                            CitizenMultiSlotsUnlocked = inheritedSlots
                        };
                        db.PlayerLegacyLedgers.Add(newLedger);
                    }
                }

                await db.SaveChangesAsync(stoppingToken); // save ledgers per chunk to avoid memory bloat
            }

            return paidToOnlyPlayer;
        }

        /// <summary>
        /// The reset half of a rollover: everything a season takes back, for
        /// every player or only <paramref name="onlyPlayerId"/> (a rebirth).
        /// The caller owns the transaction.
        /// </summary>
        /// <remarks>
        /// Modul: TRUNCATE for the whole roster, DELETE ... WHERE for one
        /// player. The statements are otherwise the same list in the same
        /// order, and that is the point - see AwardLegacyShardsAsync. A
        /// per-player DELETE does not RESTART IDENTITY, so the recycled-id
        /// hazard the equip-pointer comment below describes cannot arise from
        /// a rebirth; the pointers are still cleared first, because a pointer
        /// at a deleted row is wrong whether or not the id is ever reused.
        /// </remarks>
        internal static async Task ResetPlayersAsync(
            FolkIdleDbContext db,
            long? onlyPlayerId,
            CancellationToken stoppingToken)
        {
            bool one = onlyPlayerId.HasValue;
            long only = onlyPlayerId ?? 0L;

            // " AND <column> = <player>" for a rebirth, "" for the roster. The
            // id is a long, never a string, so inlining it cannot inject.
            string And(string column) => one ? $" AND {column} = {only}" : string.Empty;
            string Where(string column) => one ? $" WHERE {column} = {only}" : string.Empty;

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
            // Modul: AvailableSkillPoints resets to what the SEALS pay, not
            // to zero.
            //
            // "Each Seal grants +2 permanent skill points, EVERY SEASON,
            // forever" is the coupling the whole Book of Deeds exists for -
            // it gives the tree a second source of points, earned by
            // exploring rather than levelling. Zeroing the column outright
            // would pay a Seal exactly once, in the season it was earned,
            // and quietly turn a permanent reward into a one-off.
            //
            // Expressed as arithmetic on the mask rather than a lookup, so
            // it is one statement over the whole roster like everything
            // else in this method. Two points per bit, five bits.
            //
            // Modul: THE ATTRIBUTES GO BACK TO LEVEL 1 WITH THE LEVEL (task 88).
            //
            // STR/DEX/CON/LCK and UnspentAttributePoints are 7 points per level
            // (RaceAttributeGrowth) and nothing else - PlayerRecord says "never
            // modified directly except by level-up growth". This statement put
            // the level back to 1 and left every one of those points standing,
            // so the second climb paid them AGAIN on top of the first. Harmless
            // while a season was 90 days; with rebirth on demand it is linear,
            // uncapped stacking on a count the player controls, which is the
            // one shape PowerCeilingTests forbids. Back to the starting values
            // and nothing unspent - the state a level-1 account is in.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"PlayerRecords\" SET \"CurrentLevel\" = 1, \"CurrentXp\" = 0, \"ActiveOffensivePotionId\" = 0, \"OffensivePotionDurationMs\" = 0, \"ActiveDefensivePotionId\" = 0, \"DefensivePotionDurationMs\" = 0, \"FreeRespecUsed\" = FALSE, " +
                "\"BaseStrength\" = {1}, \"BaseDexterity\" = {2}, \"BaseConstitution\" = {3}, \"BaseLuck\" = {4}, \"UnspentAttributePoints\" = 0, " +
                "\"AvailableSkillPoints\" = {0} * (" +
                "  (\"SealsEarnedMask\" & 1) + ((\"SealsEarnedMask\" >> 1) & 1) + ((\"SealsEarnedMask\" >> 2) & 1)" +
                "+ ((\"SealsEarnedMask\" >> 3) & 1) + ((\"SealsEarnedMask\" >> 4) & 1))" +
                Where("\"Id\""),
                new object[]
                {
                    DeedRegistry.SkillPointsPerSeal,
                    AttributeRegistry.StartingValue(AttributeRegistry.Might),
                    AttributeRegistry.StartingValue(AttributeRegistry.Finesse),
                    AttributeRegistry.StartingValue(AttributeRegistry.Vigour),
                    AttributeRegistry.StartingValue(AttributeRegistry.Fortune),
                },
                stoppingToken);

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
            await db.Database.ExecuteSqlRawAsync(
                one ? $"DELETE FROM \"player_skill_tree\"{Where("\"PlayerId\"")}" : "TRUNCATE TABLE \"player_skill_tree\" RESTART IDENTITY",
                stoppingToken);

            // Modul: the free respec comes back with the season, and
            // PAID GRANTS DELIBERATELY DO NOT RESET. They are bought, so
            // an unspent one has to survive a rollover - wiping it would
            // be taking something a player paid real money for. Only the
            // free one is a per-season allowance. See PlayerRecord.

            // Modul: per-character equipment. The six equip pointers moved
            // off "PlayerRecords" onto "characters", so the seasonal wipe
            // needs a second statement or every character would come out of
            // the rollover still pointing at gear the wipe deleted.
            //
            // Modul: ELEVEN SLOTS, NOT EIGHT (task 88). This stopped at
            // EquippedRingId - the exact truncation CLAUDE.md warns about - so
            // the three worn TOOLS (Axe, Pickaxe, Rod) survived a TRUNCATE ...
            // RESTART IDENTITY pointing at ids the next crafted item anywhere
            // would take. Found wiring rebirth through this statement.
            //
            // And every character goes IDLE: a level-1 character left on the
            // region-5 fight it had yesterday dies on every tick.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"characters\" SET \"EquippedWeaponId\" = NULL, \"EquippedHelmetId\" = NULL, \"EquippedChestId\" = NULL, \"EquippedGlovesId\" = NULL, \"EquippedLeggingsId\" = NULL, \"EquippedBootsId\" = NULL, \"EquippedAmuletId\" = NULL, \"EquippedRingId\" = NULL, " +
                "\"EquippedAxeId\" = NULL, \"EquippedPickaxeId\" = NULL, \"EquippedRodId\" = NULL, \"ActiveActivityId\" = 0" +
                Where("\"PlayerId\""),
                stoppingToken);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"CommodityRecords\" SET \"Quantity\" = 0 WHERE \"ItemId\" = 'gold'" + And("\"PlayerId\""), stoppingToken);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"ItemId\" <> 'gold'" + And("\"PlayerId\""), stoppingToken);

            // Modul 41: unconditional full-table wipes use TRUNCATE ...
            // RESTART IDENTITY CASCADE rather than DELETE FROM. Unlike
            // DELETE, TRUNCATE deallocates pages directly and produces a
            // small, fixed WAL footprint regardless of row count, avoiding
            // WAL bloat and the long-held-lock/gateway-timeout risk of
            // per-row tombstones on large tables at season-reset scale.
            // CASCADE is a no-op safety net here (this schema does not
            // enforce real FK constraints on these tables) but protects
            // against a future FK addition silently breaking this reset.
            await db.Database.ExecuteSqlRawAsync(
                one ? $"DELETE FROM \"EquipmentInstances\"{Where("\"PlayerId\"")}" : "TRUNCATE TABLE \"EquipmentInstances\" RESTART IDENTITY CASCADE",
                stoppingToken);

            // Modul: a Workshop commission in progress is equipment on its way
            // (task 83). Left standing, a rebirth would hand a level-1 run the
            // Epic region-5 piece the old run paid for - the one piece of gear
            // that would survive the wipe above.
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"PlayerCraftingSlots\"{Where("\"PlayerId\"")}", stoppingToken);

            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketOrderRecords\" o USING \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE AND o.\"Status\" = 0 AND o.\"OrderType\" = 'SELL'" + And("e.\"PlayerId\""), stoppingToken);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"MarketOrderRecords\" o SET \"EquipmentInstanceId\" = NULL FROM \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE" + And("e.\"PlayerId\""), stoppingToken);
            // Modul 41: this TRUNCATE must run after the two statements
            // above, which still need to query MarketEquipmentInstances -
            // truncating it earlier would leave those queries with nothing
            // to match against.
            //
            // A rebirth deletes only the player's UNESCROWED listings: an
            // escrowed piece is half of somebody else's trade in flight, and
            // pulling it out from under them is not this player's reset to make.
            await db.Database.ExecuteSqlRawAsync(
                one
                    ? $"DELETE FROM \"MarketEquipmentInstances\" WHERE \"IsLockedInEscrow\" = FALSE{And("\"PlayerId\"")}"
                    : "TRUNCATE TABLE \"MarketEquipmentInstances\" RESTART IDENTITY CASCADE",
                stoppingToken);
            // A season returns every character to a fresh adult.
            //
            // THE TICKS ARE THE ADULT THRESHOLD, NOT ZERO. This used to set
            // `AgeTicks = 0, AgePhase = 1`, which reads as "everybody starts
            // the new season grown" and did the opposite: ProcessAgeSlot
            // derives the phase from the ticks, and zero ticks is a CHILD.
            // The Wiki's promise that "the whole roster is breeding-age on
            // day one" would have been a roster of children for the first
            // hour of every season, on every account.
            //
            // The statement used to reset a "Level" column too; that column
            // is gone - nothing but the dev fixture ever wrote it, and
            // breeding gated on it - so resetting it here was resetting a
            // constant.
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE characters SET \"AgeTicks\" = {AgePhaseCurve.ChildEndTicks}, \"AgePhase\" = {AgePhaseCurve.Adult}" + Where("\"PlayerId\""),
                stoppingToken);
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
            // Modul: THE VILLAGE ROSTER WAS DOCUMENTED AS SEASONAL AND WAS
            // NEVER WIPED.
            //
            // VillageNewcomer's own comment says so in capitals - "SEASONAL,
            // unlike the lineage; newcomers are wiped at the rollover along
            // with the village they came to; only BORN CHILDREN carry
            // forward" - and nothing here touched the table. So a season
            // ended with its whole gene pool intact, including the elders
            // who had already married in, and the two starter villagers
            // would never be dealt again.
            //
            // That is not a cosmetic drift. The village is the ONE thing a
            // player rebuilds each season, and the deal that makes rebuilding
            // worth doing is that this season's Inn decides what blood you
            // can marry into this season's line. A village that persists
            // makes the Inn a one-time purchase and the second season's
            // gene pool a leftover.
            //
            // The arrival clock and the recruitment counter go with it: zero
            // means "never settled", which is what deals the season's two
            // starters on the first login, and a price that escalated all
            // last season must not still be escalated on day one of this
            // one.
            await db.Database.ExecuteSqlRawAsync(
                one ? $"DELETE FROM \"village_newcomers\"{Where("\"PlayerId\"")}" : "TRUNCATE TABLE \"village_newcomers\" RESTART IDENTITY",
                stoppingToken);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerRecords\" SET \"LastVillagerArrivalEpoch\" = 0, \"VillagerRecruitmentsThisSeason\" = 0" + Where("\"Id\""), stoppingToken);

            // Modul: character_lineage_registry is NOT wiped, and that is
            // the intent rather than an oversight - see the list above.
            // Aptitudes are the axis a season is meant to leave standing,
            // so they survive on purpose, and the rollover test asserts it
            // rather than leaving it to be true by accident.
            //
            // What DOES happen to it is a cull to the Hall's cap. Without
            // one, ninety days of breeding accumulates every child ever
            // born and the last week of a season is worth exactly as much
            // as the first - which is the choice this whole system exists
            // to create. Ten slots, fourteen bought; who stays is the
            // player's mark first and the strongest blood after, and the
            // main character can never be the one let go because their id
            // IS the account's PlayerGuid.
            //
            // Runs LAST, after the level and gear wipes, so the surviving
            // roster is the one that has already been reset - a cull that
            // ran first would renumber slots the statements above then
            // write over.
            await HallOfAncestorsEngine.CullToCapAsync(db, stoppingToken, onlyPlayerId);

            // player_race_unlocks was never in this method and stays out:
            // a race you have earned is yours.
            await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerChroniclePasses\" SET \"PassLevel\" = 0, \"AccumulatedXp\" = 0, \"ClaimedMilestonesBitmask\" = 0" + Where("\"PlayerId\""), stoppingToken);
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

            // Modul: the Book of Deeds asks "did you ever finish a season in
            // the top fifty", and the roster is already ranked right here. A
            // separate pass over the leaderboard later would be a second
            // ordering that could disagree with the one that paid the prizes.
            //
            // BEST (lowest) rank ever, never overwritten by a worse season: it
            // is a record of a thing that happened, and a bad season does not
            // un-happen a good one. 0 means "never placed".
            var placedInTopFifty = new List<long>();
            for (int i = 0; i < standings.Count && i < 50; i++)
            {
                placedInTopFifty.Add(standings[i].PlayerId);
            }

            for (int i = 0; i < placedInTopFifty.Count; i++)
            {
                // The params overload would read the CancellationToken as a
                // third SQL parameter - EF answers that with "no store type
                // mapping for CancellationToken", which reads like a schema
                // problem and is an argument-list one. Pass the values as an
                // explicit array.
                await db.Database.ExecuteSqlRawAsync(
                    @"UPDATE ""PlayerRecords""
                      SET ""BestSeasonRank"" = {0}
                      WHERE ""Id"" = {1} AND (""BestSeasonRank"" = 0 OR ""BestSeasonRank"" > {0})",
                    new object[] { i + 1, placedInTopFifty[i] },
                    stoppingToken);
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
                    AnnouncementText.SeasonPlacement(closedEraId, i + 1, who));
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

        // Modul: the bank is retired - see the RetireTheBank migration. Its
        // rows were moved into EquipmentInstances, which this already counts,
        // so the shard payout is unchanged by the merge.
        internal static long CalculateInventoryScore(List<EquipmentInstance> equipment, List<MarketEquipmentInstance> marketEquipment)
        {
            long score = 0L;
            for (int i = 0; i < equipment.Count; i++) score += Math.Max(1, equipment[i].QualityTier);
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

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

        // Modul: NO SEASONS (owner 2026-10-14). The calendar stopped ending
        // them on 2026-09-30 (task 88) and the admin's typed "END SEASON" was
        // the last way to roll every account over at once; it is gone, with
        // the placement rewards only a global rollover paid. A run ends when
        // its player chooses - RebirthEngine - and this engine is now the
        // shared reset that rebirth calls, plus the loop that keeps one era
        // record alive because the shard ledgers are keyed on it.
        //
        // EVERY RESET HERE TAKES ONE PLAYER. There is no code path left that
        // can wipe the whole roster, and the TRUNCATE branches that did so
        // were removed rather than left unreachable.
        private static readonly SemaphoreSlim Wake = new(0, 1);

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

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, stoppingToken);

            bool any = await db.SeasonalEraRecords
                .FromSqlRaw("SELECT * FROM \"SeasonalEraRecords\" WHERE \"IsActive\" = TRUE ORDER BY \"EndTimestamp\" LIMIT 1 FOR UPDATE")
                .AnyAsync(stoppingToken);

            if (!any)
            {
                // An era still has to exist: the shard ledgers are keyed on
                // it, rebirth included. Its date ends nothing.
                db.SeasonalEraRecords.Add(new SeasonalEraRecord
                {
                    EndTimestamp = now + EraDurationSeconds,
                    IsActive = true,
                    IsRolloverPaused = true
                });
                await db.SaveChangesAsync(stoppingToken);
            }
            await transaction.CommitAsync(stoppingToken);
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
            long onlyPlayerId,
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
            var playerQuery = db.PlayerRecords.AsNoTracking().Where(p => p.Id == onlyPlayerId);

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
                    if (playerId == onlyPlayerId)
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
        /// The reset a rebirth performs, for one player. The caller owns the
        /// transaction.
        /// </summary>
        /// <remarks>
        /// Modul: ONE PLAYER, ALWAYS. This used to TRUNCATE the whole roster
        /// for a season and DELETE ... WHERE for a rebirth, one list of
        /// statements with a branch in each. Seasons are gone (2026-10-14),
        /// so the roster branch was deleted rather than left reachable. The
        /// equip pointers are still cleared first, because a pointer at a
        /// deleted row is wrong whether or not the id is ever reused.
        /// </remarks>
        internal static async Task ResetPlayersAsync(
            FolkIdleDbContext db,
            long onlyPlayerId,
            CancellationToken stoppingToken,
            IReadOnlyCollection<long>? keepEquipmentIds = null)
        {
            // Task 114: equipment a rebirth paid to keep (RebirthEngine priced
            // and checked it).
            long[] keepIds = keepEquipmentIds != null ? keepEquipmentIds.ToArray() : Array.Empty<long>();
            long only = onlyPlayerId;

            // The id is a long, never a string, so inlining it cannot inject.
            string And(string column) => $" AND {column} = {only}";
            string Where(string column) => $" WHERE {column} = {only}";

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
                // Modul: THE LARDER GOES WITH THE CHEST (2026-10-20). The chest's
                // fish were deleted below and the three larder slots - fish
                // already moved out of the chest - were never touched, so the
                // owner's first rebirth carried 588 + 9999 + 9999 rations into
                // a level-1 life. A new run starts with the starter food only.
                "\"LifeHighestRegion\" = 0, " +
                "\"LarderSlot1ItemId\" = 0, \"LarderSlot1Count\" = 0, \"LarderSlot2ItemId\" = 0, \"LarderSlot2Count\" = 0, \"LarderSlot3ItemId\" = 0, \"LarderSlot3Count\" = 0, " +
                "\"AvailableSkillPoints\" = {0} * (" +
                "  (\"SealsEarnedMask\" & 1) + ((\"SealsEarnedMask\" >> 1) & 1) + ((\"SealsEarnedMask\" >> 2) & 1)" +
                "+ ((\"SealsEarnedMask\" >> 3) & 1) + ((\"SealsEarnedMask\" >> 4) & 1))" +
                // Task 114: and what the rebirths pay - RebirthRules.SkillPointsFor,
                // written out as SQL so the roster stays one statement.
                " + {5} * LEAST(\"RebirthCount\", {6})" +
                Where("\"Id\""),
                new object[]
                {
                    DeedRegistry.SkillPointsPerSeal,
                    AttributeRegistry.StartingValue(AttributeRegistry.Might),
                    AttributeRegistry.StartingValue(AttributeRegistry.Finesse),
                    AttributeRegistry.StartingValue(AttributeRegistry.Vigour),
                    AttributeRegistry.StartingValue(AttributeRegistry.Fortune),
                    RebirthRules.SkillPointsPerRebirth,
                    RebirthRules.RewardedRebirths,
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
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM \"player_skill_tree\"{Where("\"PlayerId\"")}",
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

            // Everything but what the rebirth paid to keep.
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM \"EquipmentInstances\"{Where("\"PlayerId\"")}" + (keepIds.Length > 0 ? $" AND \"Id\" NOT IN ({string.Join(",", keepIds)})" : string.Empty),
                stoppingToken);

            // Modul: a Workshop commission in progress is equipment on its way
            // (task 83). Left standing, a rebirth would hand a level-1 run the
            // Epic region-5 piece the old run paid for - the one piece of gear
            // that would survive the wipe above.
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"PlayerCraftingSlots\"{Where("\"PlayerId\"")}", stoppingToken);

            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketOrderRecords\" o USING \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE AND o.\"Status\" = 0 AND o.\"OrderType\" = 'SELL'" + And("e.\"PlayerId\""), stoppingToken);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"MarketOrderRecords\" o SET \"EquipmentInstanceId\" = NULL FROM \"MarketEquipmentInstances\" e WHERE o.\"EquipmentInstanceId\" = e.\"Id\" AND e.\"IsLockedInEscrow\" = FALSE" + And("e.\"PlayerId\""), stoppingToken);
            // Modul 41: this DELETE must run after the two statements above,
            // which still need to query MarketEquipmentInstances - deleting
            // first would leave those queries with nothing to match against.
            //
            // A rebirth deletes only the player's UNESCROWED listings: an
            // escrowed piece is half of somebody else's trade in flight, and
            // pulling it out from under them is not this player's reset to make.
            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM \"MarketEquipmentInstances\" WHERE \"IsLockedInEscrow\" = FALSE{And("\"PlayerId\"")}",
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
                $"DELETE FROM \"village_newcomers\"{Where("\"PlayerId\"")}",
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

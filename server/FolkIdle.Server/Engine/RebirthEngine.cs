using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FolkIdle.Server.Engine
{
    public enum RebirthResult
    {
        Ok = 0,
        /// <summary>The count the request was made against has moved - a double submit or a stale page.</summary>
        AlreadyReborn = 1,
        NotFound = 2,
        Failed = 3,
        /// <summary>A rebirth for this player is already in flight on the tick.</summary>
        InFlight = 4,
    }

    public readonly record struct RebirthOutcome(
        RebirthResult Result,
        int RebirthCount,
        int RenownedRebirths,
        int DamageBonusPct,
        int ShardsEarned,
        bool Renowned);

    /// <summary>What a rebirth would do right now. Read-only.</summary>
    public sealed class RebirthPreview
    {
        public int RebirthCount { get; set; }
        public int RenownedRebirths { get; set; }
        public int Level { get; set; }
        public int RenownLevel { get; set; }
        public bool Renowned { get; set; }
        public int DamageBonusPctNow { get; set; }
        public int DamageBonusPctAfter { get; set; }
        public int DamageBonusPctCap { get; set; }
        public int ShardsEarned { get; set; }

        // What goes.
        public long Gold { get; set; }
        public int MaterialStacks { get; set; }
        public int EquipmentPieces { get; set; }
        public int MarketListings { get; set; }
        public int SkillTreeLevels { get; set; }
        public int AttributePoints { get; set; }
        public int VillageNewcomers { get; set; }
        public List<string> HallLetGo { get; set; } = new();

        // What stays.
        public int Seals { get; set; }
        public int SkillPointsFromSeals { get; set; }
        public int InheritanceLevels { get; set; }
        public int ShardBalance { get; set; }
        public int Diamonds { get; set; }
        public int HallMembersKept { get; set; }
        public int VillageBuildingLevels { get; set; }
    }

    /// <summary>
    /// Rebirth on demand (task 88): the season rollover, for one player, when
    /// they choose. Replaces the calendar: see SeasonalRotationEngine's era
    /// check, which no longer ends a season on its date.
    ///
    /// Modul: THIS FILE HOLDS NO RESET OF ITS OWN. What a rebirth keeps and
    /// takes is exactly what the season rollover keeps and takes, and it gets
    /// there by calling the rollover's own AwardLegacyShardsAsync and
    /// ResetPlayersAsync with a player filter. A second list of "what resets"
    /// here would drift from the first the day somebody added a table to one
    /// of them - two sources of one truth, this codebase's dominant bug class.
    ///
    /// Modul: THE LIVE SESSION. This method only ever runs AFTER the player's
    /// live payload has been suspended and flushed (RebirthTickCoordinator, as
    /// the continuation of the flush), or when there is no live payload at
    /// all. What protects the reset from a stale copy of the old life:
    ///   - the Redis frame and buffers are purged, or RedisWriteBehindEngine
    ///     would write the old level back and bank the old gold buffer;
    ///   - LogicEpochCounter is raised by RebirthRules.EpochFence, so any
    ///     snapshot of the old payload still queued is refused by FlushState's
    ///     epoch check rather than written over the reset.
    /// </summary>
    public sealed class RebirthEngine
    {
        private readonly IServiceProvider _serviceProvider;

        public RebirthEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<RebirthOutcome> RebirthAsync(long playerId, int expectedRebirthCount, CancellationToken cancellationToken = default)
        {
            await PurgeRedisAsync(playerId);

            RebirthOutcome outcome;
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                // ReadCommitted + the row lock, not Serializable: this touches
                // one player's rows, the account stripe already serialises the
                // request, and a serialization failure against an unrelated
                // write would read to the player as a rebirth that failed.
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .AsNoTracking()
                        .SingleOrDefaultAsync(cancellationToken);

                    if (player == null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new RebirthOutcome(RebirthResult.NotFound, 0, 0, 0, 0, false);
                    }

                    // THE IDEMPOTENCY. The request names the count its preview
                    // showed; a second submit of that preview (a double tap, a
                    // retry after a slow answer, a second tab) finds it moved
                    // and changes nothing. Said with its own result, never a
                    // silent success - and never a second rebirth.
                    if (player.RebirthCount != expectedRebirthCount)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new RebirthOutcome(RebirthResult.AlreadyReborn, player.RebirthCount, player.RenownedRebirths,
                            RebirthRules.DamageBonusPct(player.RenownedRebirths), 0, false);
                    }

                    int eraId = await EnsureActiveEraAsync(db, cancellationToken);
                    bool renowned = RebirthRules.IsRenowned(player.CurrentLevel);

                    int shards = await SeasonalRotationEngine.AwardLegacyShardsAsync(db, eraId, playerId, cancellationToken);
                    await SeasonalRotationEngine.ResetPlayersAsync(db, playerId, cancellationToken);

                    // Modul: A NEW RUN STARTS LIKE A NEW ACCOUNT. The reset
                    // takes every piece of gear and every fish, and a naked
                    // level-1 character is exactly the account that died to the
                    // first monster in the game until the starter weapon existed
                    // (2026-09-28). So the reborn account gets what registration
                    // gives - the claymore and ten fish in the chest, the three
                    // Normal tools worn - through the same StarterEquipmentGrant
                    // both registration paths call. All worthless on the market,
                    // so a rebirth is not a way to farm them.
                    var starterTools = StarterEquipmentGrant.Seed(db, playerId);
                    await db.SaveChangesAsync(cancellationToken);
                    await StarterEquipmentGrant.EquipOnAsync(db, player.PlayerGuid, starterTools);
                    await StarterEquipmentGrant.SeedStarterFoodAsync(db, playerId);
                    await db.SaveChangesAsync(cancellationToken);

                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"PlayerRecords\" SET \"RebirthCount\" = \"RebirthCount\" + 1, " +
                        "\"RenownedRebirths\" = \"RenownedRebirths\" + {0}, " +
                        "\"LogicEpochCounter\" = \"LogicEpochCounter\" + {1} WHERE \"Id\" = {2}",
                        new object[] { renowned ? 1 : 0, RebirthRules.EpochFence, playerId },
                        cancellationToken);

                    await transaction.CommitAsync(cancellationToken);

                    int renownAfter = player.RenownedRebirths + (renowned ? 1 : 0);
                    outcome = new RebirthOutcome(RebirthResult.Ok, player.RebirthCount + 1, renownAfter,
                        RebirthRules.DamageBonusPct(renownAfter), shards, renowned);

                    Console.WriteLine($"[rebirth] player {playerId}: level {player.CurrentLevel} -> 1, rebirth #{outcome.RebirthCount}, renowned {renowned}, shards {shards}, damage bonus {outcome.DamageBonusPct}%");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    Console.WriteLine($"[rebirth] player {playerId} FAILED: {ex}");
                    return new RebirthOutcome(RebirthResult.Failed, expectedRebirthCount, 0, 0, 0, false);
                }
            }

            // Again after the commit: a frame the tick stored between the
            // first purge and the commit (it should not - the payload is
            // suspended - but a frame is cheap to drop and costly to keep).
            await PurgeRedisAsync(playerId);
            return outcome;
        }

        public async Task<RebirthPreview?> PreviewAsync(long playerId, CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var player = await db.PlayerRecords.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken);
            if (player == null) return null;

            var commodities = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId)
                .Select(c => new { c.ItemId, c.Quantity })
                .ToListAsync(cancellationToken);
            long gold = Math.Max(0L, commodities.Where(c => c.ItemId == "gold").Sum(c => c.Quantity));

            var equipment = await db.EquipmentInstances.AsNoTracking().Where(e => e.PlayerId == playerId).ToListAsync(cancellationToken);
            var listings = await db.MarketEquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == playerId && !e.IsLockedInEscrow).ToListAsync(cancellationToken);

            int level = Math.Max(1, player.CurrentLevel);
            bool renowned = RebirthRules.IsRenowned(level);
            int shards = SeasonalRotationEngine.CalculateLegacyShards(
                gold, (long)level * level, SeasonalRotationEngine.CalculateInventoryScore(equipment, listings));

            var characters = await db.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == playerId).CountAsync(cancellationToken);
            var letGo = await HallOfAncestorsEngine.WouldReleaseAsync(db, playerId, cancellationToken);

            int placed =
                (player.BaseStrength - AttributeRegistry.StartingValue(AttributeRegistry.Might))
                + (player.BaseDexterity - AttributeRegistry.StartingValue(AttributeRegistry.Finesse))
                + (player.BaseConstitution - AttributeRegistry.StartingValue(AttributeRegistry.Vigour))
                + (player.BaseLuck - AttributeRegistry.StartingValue(AttributeRegistry.Fortune));

            return new RebirthPreview
            {
                RebirthCount = player.RebirthCount,
                RenownedRebirths = player.RenownedRebirths,
                Level = level,
                RenownLevel = RebirthRules.RenownLevel,
                Renowned = renowned,
                DamageBonusPctNow = RebirthRules.DamageBonusPct(player.RenownedRebirths),
                DamageBonusPctAfter = RebirthRules.DamageBonusPct(player.RenownedRebirths + (renowned ? 1 : 0)),
                DamageBonusPctCap = RebirthRules.MaxDamageBonusPct,
                ShardsEarned = shards,

                Gold = gold,
                MaterialStacks = commodities.Count(c => c.ItemId != "gold" && c.Quantity > 0),
                EquipmentPieces = equipment.Count,
                MarketListings = listings.Count,
                SkillTreeLevels = await db.PlayerSkillTreeNodes.AsNoTracking()
                    .Where(n => n.PlayerId == playerId).SumAsync(n => (int?)n.Level, cancellationToken) ?? 0,
                AttributePoints = Math.Max(0, placed) + Math.Max(0, player.UnspentAttributePoints),
                VillageNewcomers = await db.VillageNewcomers.AsNoTracking().CountAsync(v => v.PlayerId == playerId, cancellationToken),
                HallLetGo = letGo,

                Seals = DeedRegistry.SealCount(player.SealsEarnedMask),
                SkillPointsFromSeals = DeedRegistry.SkillPointsFrom(player.SealsEarnedMask),
                InheritanceLevels = await db.PlayerInheritanceStats.AsNoTracking()
                    .Where(s => s.PlayerId == playerId).SumAsync(s => (int?)s.Level, cancellationToken) ?? 0,
                ShardBalance = await db.PlayerLegacyLedgers.AsNoTracking()
                    .Where(l => l.PlayerId == playerId).SumAsync(l => (int?)l.LegacyShardBalance, cancellationToken) ?? 0,
                Diamonds = player.PremiumDiamonds,
                HallMembersKept = Math.Max(0, characters - letGo.Count),
                VillageBuildingLevels = await db.VillageInfrastructures.AsNoTracking()
                    .Where(v => v.PlayerId == playerId).SumAsync(v => (int?)v.CurrentLevel, cancellationToken) ?? 0,
            };
        }

        /// <summary>
        /// The era the shard ledger row goes to. Normally the one standing
        /// era; created (paused) if there is none, the same way
        /// LegacyStoreEngine does, since a ledger row needs a real era.
        /// </summary>
        private static async Task<int> EnsureActiveEraAsync(FolkIdleDbContext db, CancellationToken cancellationToken)
        {
            var era = await db.SeasonalEraRecords
                .Where(e => e.IsActive)
                .OrderBy(e => e.EndTimestamp)
                .FirstOrDefaultAsync(cancellationToken);
            if (era != null) return era.EraId;

            var created = new SeasonalEraRecord
            {
                EndTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 90L * 24L * 60L * 60L,
                IsActive = true,
                IsRolloverPaused = true,
            };
            db.SeasonalEraRecords.Add(created);
            await db.SaveChangesAsync(cancellationToken);
            return created.EraId;
        }

        private async Task PurgeRedisAsync(long playerId)
        {
            var redis = _serviceProvider.GetService<IConnectionMultiplexer>();
            if (redis == null || !redis.IsConnected) return;

            try
            {
                var rdb = redis.GetDatabase();
                await rdb.KeyDeleteAsync(new RedisKey[]
                {
                    RedisSessionCache.SessionStateKey(playerId),
                    RedisSessionCache.GoldBufferKey(playerId),
                    RedisSessionCache.WoodBufferKey(playerId),
                    RedisSessionCache.StoneBufferKey(playerId),
                    RedisSessionCache.IronOreBufferKey(playerId),
                });
                await rdb.SetRemoveAsync(RedisSessionCache.DirtyPlayersSetKey, playerId);
            }
            catch (Exception ex)
            {
                // Not fatal: the epoch fence still refuses a stale checkpoint.
                // The frame is the one thing it cannot stop - see the risks in
                // docs/superpowers/specs/2026-09-30-rebirth-on-demand.md.
                Console.WriteLine($"[rebirth] Redis purge for player {playerId} failed: {ex.Message}");
            }
        }
    }
}

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
        /// <summary>The request did not carry the typed confirmation word - see RebirthRules.ConfirmationWord.</summary>
        ConfirmationRequired = 5,
        /// <summary>Never returned since 2026-11-13 (rebirth is unlimited); kept so old clients keep their wording.</summary>
        Capped = 6,
        /// <summary>The gold held does not cover the equipment chosen to keep.</summary>
        NotEnoughGold = 7,
        /// <summary>A piece chosen to keep is not this account's (or is listed on the market).</summary>
        KeepNotOwned = 8,
        /// <summary>The character is below RebirthRules.MinimumLevel.</summary>
        LevelTooLow = 9,
        /// <summary>The trial asked for does not exist or is already finished (RebirthTrials).</summary>
        TrialUnavailable = 10,
        /// <summary>The Trial of Iron was asked for with gear chosen for the Vault.</summary>
        TrialForbidsVault = 11,
    }

    public readonly record struct RebirthOutcome(
        RebirthResult Result,
        int RebirthCount,
        int RenownedRebirths,
        int DamageBonusPct,
        int ShardsEarned,
        bool Renowned);

    /// <summary>One Rebirth Trial as the Rebirth screen shows it.</summary>
    public sealed class TrialView
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Rule { get; set; } = string.Empty;
        public string Reward { get; set; } = string.Empty;
        public string TitleName { get; set; } = string.Empty;
        public bool ForbidsVault { get; set; }
        public bool Completed { get; set; }
        public bool Active { get; set; }

        public static List<TrialView> For(int activeTrial, int completedMask)
        {
            var list = new List<TrialView>();
            foreach (var t in RebirthTrials.All)
            {
                list.Add(new TrialView
                {
                    Id = t.Id,
                    Name = t.Name,
                    Rule = t.Rule,
                    Reward = t.Reward,
                    TitleName = t.TitleName,
                    ForbidsVault = t.ForbidsVault,
                    Completed = RebirthTrials.IsCompleted(completedMask, t.Id),
                    Active = t.Id == activeTrial,
                });
            }
            return list;
        }
    }

    /// <summary>One Ancestral Boon as the Rebirth screen shows it.</summary>
    public sealed class BoonView
    {
        /// <summary>The TargetUnlockId a purchase sends (LegacyStoreEngine's perk ids).</summary>
        public uint UnlockId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Effect { get; set; } = string.Empty;
        public int Rank { get; set; }
        public int MaxRank { get; set; }
        /// <summary>Shards for the next rank, or -1 at the cap.</summary>
        public int NextCost { get; set; }
        /// <summary>How many ranks the shard balance buys at once (LegacyStoreEngine.RanksAffordable).</summary>
        public int AffordableRanks { get; set; }
        /// <summary>What those ranks cost together.</summary>
        public long AffordableCost { get; set; }

        public static List<BoonView> For(long legacyPerks, long shardBalance = 0)
        {
            var list = new List<BoonView>(3);
            void Add(uint id, int offset, string name, string effect)
            {
                int max = LegacyPerkResolver.MaxRankFor(offset);
                int rank = Math.Min(LegacyPerkResolver.GetPerkRank(legacyPerks, offset), max);
                var (affordable, affordableCost) = LegacyStoreEngine.RanksAffordable(offset, rank, shardBalance, LegacyStoreEngine.MaxRanksPerPurchase);
                list.Add(new BoonView
                {
                    UnlockId = id,
                    Name = name,
                    Effect = effect,
                    Rank = rank,
                    MaxRank = max,
                    NextCost = rank >= max ? -1 : LegacyPerkResolver.CalculatePerkRankCost(offset, rank),
                    AffordableRanks = affordable,
                    AffordableCost = affordableCost,
                });
            }
            Add(LegacyStoreEngine.XpMultiplierPerkUnlockId, LegacyPerkResolver.XpMultiplierBitOffset, "Ancestors' Wisdom", "+1% combat XP per rank");
            Add(LegacyStoreEngine.GoldDropRatePerkUnlockId, LegacyPerkResolver.GoldDropRateBitOffset, "Ancestors' Fortune", "+1% gold from kills per rank");
            Add(LegacyStoreEngine.CombatSpeedPerkUnlockId, LegacyPerkResolver.CombatSpeedBitOffset, "Ancestors' Fury", "+1% damage per rank");
            return list;
        }
    }

    /// <summary>What a rebirth would do right now. Read-only.</summary>
    public sealed class RebirthPreview
    {
        public int RebirthCount { get; set; }
        public int RenownedRebirths { get; set; }
        public int Level { get; set; }
        public int RenownLevel { get; set; }
        /// <summary>The lowest level a rebirth can be taken at (RebirthRules.MinimumLevel).</summary>
        public int MinimumLevel { get; set; }
        public bool Renowned { get; set; }
        public int DamageBonusPctNow { get; set; }
        public int DamageBonusPctAfter { get; set; }
        public int DamageBonusPctCap { get; set; }
        public int ShardsEarned { get; set; }

        // Task 114: what a rebirth pays now, and its ceiling.
        /// <summary>RebirthRules.BloodlineRebirths - the count that maxes the bloodline ceiling. Not a cap any more.</summary>
        public int MaxRebirths { get; set; }
        public bool Capped { get; set; }
        /// <summary>The last rebirth that pays more than the bloodline ceiling (RebirthRules.RewardedRebirths).</summary>
        public int RewardedRebirths { get; set; }
        /// <summary>BreedingAptitudes.CeilingFor the count now, and after this rebirth.</summary>
        public int BloodlineCeilingNow { get; set; }
        public int BloodlineCeilingAfter { get; set; }
        public int SkillPointsFromRebirthsNow { get; set; }
        public int SkillPointsFromRebirthsAfter { get; set; }
        public int ReLevelXpBonusPct { get; set; }
        /// <summary>The best level after this rebirth - below it, kills pay ReLevelXpBonusPct more XP.</summary>
        public int PeakLevelAfter { get; set; }
        public int SecondPetSlotRebirths { get; set; }
        /// <summary>Gold to keep one piece, by quality tier 0..13 (RebirthRules.KeepPrice).</summary>
        public long[] KeepPriceByTier { get; set; } = Array.Empty<long>();
        /// <summary>What every rebirth 1..MaxRebirths pays - Domain.Progression.RebirthRoadmap.</summary>
        public IReadOnlyList<Domain.Progression.RebirthRoadmapStep> Roadmap { get; set; } = Array.Empty<Domain.Progression.RebirthRoadmapStep>();

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

        /// <summary>The Ancestral Boons the shards buy (LegacyPerkResolver), with their prices.</summary>
        public List<BoonView> Boons { get; set; } = new();

        /// <summary>The Rebirth Trials, and the one this life is under (0 = none).</summary>
        public List<TrialView> Trials { get; set; } = new();
        public int ActiveTrial { get; set; }

        /// <summary>Life records (RebirthRules.LifeRecord): seconds, 0 = none. LifeSeconds is this life so far, 0 if untimed.</summary>
        public long LifeSeconds { get; set; }
        public long BestLifeSecondsTo50 { get; set; }
        public long BestLifeSecondsTo100 { get; set; }
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

        public async Task<RebirthOutcome> RebirthAsync(long playerId, int expectedRebirthCount, IReadOnlyCollection<long>? keepEquipmentIds = null, CancellationToken cancellationToken = default, int trialId = RebirthTrials.None)
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
                        await transaction.SafeRollbackAsync(cancellationToken);
                        return new RebirthOutcome(RebirthResult.NotFound, 0, 0, 0, 0, false);
                    }

                    // THE IDEMPOTENCY. The request names the count its preview
                    // showed; a second submit of that preview (a double tap, a
                    // retry after a slow answer, a second tab) finds it moved
                    // and changes nothing. Said with its own result, never a
                    // silent success - and never a second rebirth.
                    if (player.RebirthCount != expectedRebirthCount)
                    {
                        await transaction.SafeRollbackAsync(cancellationToken);
                        return new RebirthOutcome(RebirthResult.AlreadyReborn, player.RebirthCount, player.RenownedRebirths,
                            RebirthRules.DamageBonusPct(player.RenownedRebirths), 0, false);
                    }

                    // No cap since 2026-11-13 (RebirthRules.BloodlineRebirths);
                    // RebirthResult.Capped stays in the enum for old clients.

                    // The level floor, read from the row this transaction just
                    // locked - the online path flushes the live payload first,
                    // so it is the level the player sees.
                    if (!RebirthRules.IsLevelEnough(player.CurrentLevel))
                    {
                        await transaction.SafeRollbackAsync(cancellationToken);
                        return new RebirthOutcome(RebirthResult.LevelTooLow, player.RebirthCount, player.RenownedRebirths,
                            RebirthRules.DamageBonusPct(player.RenownedRebirths), 0, false);
                    }

                    // Rebirth Trials: the next life's rule, refused before
                    // anything is written. A finished trial cannot be taken
                    // again; Iron forbids the Vault.
                    long[] keepRequested = (keepEquipmentIds ?? Array.Empty<long>()).Distinct().ToArray();
                    if (trialId != RebirthTrials.None)
                    {
                        if (!RebirthTrials.IsChoosable(trialId, player.CompletedRebirthTrials))
                        {
                            await transaction.SafeRollbackAsync(cancellationToken);
                            return new RebirthOutcome(RebirthResult.TrialUnavailable, player.RebirthCount, player.RenownedRebirths,
                                RebirthRules.DamageBonusPct(player.RenownedRebirths), 0, false);
                        }
                        if (RebirthTrials.Find(trialId)!.ForbidsVault && keepRequested.Length > 0)
                        {
                            await transaction.SafeRollbackAsync(cancellationToken);
                            return new RebirthOutcome(RebirthResult.TrialForbidsVault, player.RebirthCount, player.RenownedRebirths,
                                RebirthRules.DamageBonusPct(player.RenownedRebirths), 0, false);
                        }
                    }

                    // Task 114: the equipment the player pays to keep. Priced
                    // and checked BEFORE anything is written, and paid from the
                    // gold row before the shards are counted - gold the reset
                    // would wipe still buys shards, so keeping gear costs them.
                    long[] keep = (keepEquipmentIds ?? Array.Empty<long>()).Distinct().ToArray();
                    if (keep.Length > 0)
                    {
                        var kept = await db.EquipmentInstances.AsNoTracking()
                            .Where(e => e.PlayerId == playerId && keep.Contains(e.Id))
                            .Select(e => e.QualityTier)
                            .ToListAsync(cancellationToken);
                        if (kept.Count != keep.Length)
                        {
                            await transaction.SafeRollbackAsync(cancellationToken);
                            return new RebirthOutcome(RebirthResult.KeepNotOwned, player.RebirthCount, player.RenownedRebirths,
                                RebirthRules.DamageBonusPct(player.RenownedRebirths), 0, false);
                        }
                        long price = kept.Sum(RebirthRules.KeepPrice);
                        int paid = await db.Database.ExecuteSqlRawAsync(
                            "UPDATE \"CommodityRecords\" SET \"Quantity\" = \"Quantity\" - {0} WHERE \"PlayerId\" = {1} AND \"ItemId\" = 'gold' AND \"Quantity\" >= {0}",
                            new object[] { price, playerId }, cancellationToken);
                        if (paid == 0)
                        {
                            await transaction.SafeRollbackAsync(cancellationToken);
                            return new RebirthOutcome(RebirthResult.NotEnoughGold, player.RebirthCount, player.RenownedRebirths,
                                RebirthRules.DamageBonusPct(player.RenownedRebirths), 0, false);
                        }
                    }

                    int eraId = await EnsureActiveEraAsync(db, cancellationToken);
                    bool renowned = RebirthRules.IsRenowned(player.CurrentLevel);

                    int shards = await SeasonalRotationEngine.AwardLegacyShardsAsync(db, eraId, playerId, cancellationToken);

                    // Counted (and the last life's level recorded) BEFORE the
                    // reset: the reset hands back the skill points the count
                    // pays (RebirthRules.SkillPointsFor), so it must see the new one.
                    //
                    // Modul: THE LIFE JUST ENDED, NOT THE BEST ONE (2026-10-20).
                    // This was GREATEST(PeakLevel, CurrentLevel), so one life at
                    // 112 paid +50% XP up to 112 in every later life - and a
                    // player could rebirth at 50 again and again, each a
                    // Renowned rebirth, climbing with the bonus every time. Now
                    // the bonus window is exactly what this life reached: rebirth
                    // at 50 and the next life's bonus ends at 50.
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"PlayerRecords\" SET \"RebirthCount\" = \"RebirthCount\" + 1, " +
                        "\"PeakLevel\" = \"CurrentLevel\" WHERE \"Id\" = {0}",
                        new object[] { playerId }, cancellationToken);

                    await SeasonalRotationEngine.ResetPlayersAsync(db, playerId, cancellationToken, keep);

                    // The Vault lock: each kept piece waits for this life to
                    // reach its region again (EquipmentInstance.VaultLockRegion).
                    // ResetPlayersAsync put LifeHighestRegion back to 0.
                    if (keep.Length > 0)
                    {
                        var keptPieces = await db.EquipmentInstances
                            .Where(e => e.PlayerId == playerId && keep.Contains(e.Id))
                            .ToListAsync(cancellationToken);
                        foreach (var piece in keptPieces)
                        {
                            piece.VaultLockRegion = Math.Max(1, ContentRegistry.GetRegionTierForBaseId(piece.BaseItemId));
                        }
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    // Modul: A NEW RUN STARTS LIKE A NEW ACCOUNT. The reset
                    // takes every piece of gear and every fish, and a naked
                    // level-1 character is exactly the account that died to the
                    // first monster in the game until the starter weapon existed
                    // (2026-09-28). So the reborn account gets what registration
                    // gives - the claymore and forty fish in the chest, the three
                    // Normal tools worn - through the same StarterEquipmentGrant
                    // both registration paths call. All worthless on the market,
                    // so a rebirth is not a way to farm them.
                    var starterTools = StarterEquipmentGrant.Seed(db, playerId);
                    await db.SaveChangesAsync(cancellationToken);
                    // On whoever the cull left in slot 1 - the tools belong to
                    // the slot, and the founder need not be standing in it.
                    Guid slotOne = await Domain.Combat.EquipmentSlotEngine.FieldedCharacterIdAsync(db, playerId, 0);
                    await StarterEquipmentGrant.EquipOnAsync(db, slotOne == Guid.Empty ? player.PlayerGuid : slotOne, starterTools);
                    await StarterEquipmentGrant.SeedStarterFoodAsync(db, playerId);
                    await db.SaveChangesAsync(cancellationToken);

                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"PlayerRecords\" SET " +
                        "\"RenownedRebirths\" = \"RenownedRebirths\" + {0}, " +
                        "\"LogicEpochCounter\" = \"LogicEpochCounter\" + {1} WHERE \"Id\" = {2}",
                        new object[] { renowned ? 1 : 0, RebirthRules.EpochFence, playerId },
                        cancellationToken);

                    // Task 114: the last REWARDED rebirth (the tenth, not the
                    // twentieth) is worn as a title, and brings the Phoenix
                    // (pets survive a rebirth, so it stays).
                    int countAfter = player.RebirthCount + 1;
                    if (RebirthRules.IsFinalRewardedRebirth(countAfter))
                    {
                        await Domain.Progression.TitleEngine.GrantAsync(db, playerId, Domain.Progression.TitleRegistry.RebirthTitleSlug, GameClock.UtcNow);
                        await Domain.Progression.PetEngine.GrantAsync(db, playerId, Domain.Progression.PetRegistry.PhoenixId, GameClock.UtcNow);
                    }
                    // The milestone titles past ten (20, 30, 50). At or past,
                    // and the grant ignores a duplicate - so an account that
                    // reached 20 before the list existed is paid on its next one.
                    foreach (var titleMilestone in RebirthRules.TitleMilestones)
                    {
                        if (countAfter >= titleMilestone.Rebirths)
                            await Domain.Progression.TitleEngine.GrantAsync(db, playerId, titleMilestone.TitleSlug, GameClock.UtcNow);
                    }

                    // Rebirth Trials: the life this rebirth ends was under a
                    // trial and reached the rebirth level (checked above), so
                    // the trial is finished - its bit and its title. Then the
                    // next life's trial, already validated.
                    int completedTrials = player.CompletedRebirthTrials;
                    var finishedTrial = RebirthTrials.Find(player.ActiveRebirthTrial);
                    if (finishedTrial != null)
                    {
                        completedTrials |= RebirthTrials.BitOf(finishedTrial.Id);
                        await Domain.Progression.TitleEngine.GrantAsync(db, playerId, finishedTrial.TitleSlug, GameClock.UtcNow);
                    }
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"PlayerRecords\" SET \"CompletedRebirthTrials\" = {0}, \"ActiveRebirthTrial\" = {1} WHERE \"Id\" = {2}",
                        new object[] { completedTrials, trialId, playerId }, cancellationToken);

                    // Life records (RebirthRules.LifeRecord): this life, timed
                    // from the rebirth that began it, then the next life's clock.
                    long nowSeconds = GameClock.UtcNowOffset.ToUnixTimeSeconds();
                    long lifeSeconds = player.LifeStartedAtEpoch > 0 ? nowSeconds - player.LifeStartedAtEpoch : 0;
                    long best50 = RebirthRules.LifeRecord(player.BestLifeSecondsTo50, lifeSeconds, player.CurrentLevel, RebirthRules.RecordLevelLow);
                    long best100 = RebirthRules.LifeRecord(player.BestLifeSecondsTo100, lifeSeconds, player.CurrentLevel, RebirthRules.RecordLevelHigh);
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE \"PlayerRecords\" SET \"LifeStartedAtEpoch\" = {0}, \"BestLifeSecondsTo50\" = {1}, \"BestLifeSecondsTo100\" = {2} WHERE \"Id\" = {3}",
                        new object[] { nowSeconds, best50, best100, playerId }, cancellationToken);
                    // The milestone avatars (3, 5, 7, 10). Cosmetics survive the
                    // reset; granted once, so a replay cannot add a second.
                    foreach (var milestone in RebirthRules.AvatarMilestones)
                    {
                        if (milestone.Rebirths != countAfter) continue;
                        bool owned = await db.CosmeticItems.AnyAsync(c => c.PlayerId == playerId && c.DefinitionId == milestone.AvatarId, cancellationToken);
                        if (owned) continue;
                        var def = Domain.Progression.CosmeticRegistry.Find(milestone.AvatarId);
                        db.CosmeticItems.Add(new CosmeticItem
                        {
                            PlayerId = playerId,
                            Kind = (byte)Domain.Progression.CosmeticKind.Avatar,
                            DefinitionId = milestone.AvatarId,
                            Rarity = (byte)(def?.Rarity ?? Domain.Progression.CosmeticRegistry.Epic),
                            Source = (byte)Domain.Progression.CosmeticSource.Rebirth,
                            AcquiredAtUtc = GameClock.UtcNow,
                            IsListed = false,
                        });
                        await db.SaveChangesAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);

                    int renownAfter = player.RenownedRebirths + (renowned ? 1 : 0);
                    outcome = new RebirthOutcome(RebirthResult.Ok, player.RebirthCount + 1, renownAfter,
                        RebirthRules.DamageBonusPct(renownAfter), shards, renowned);

                    Console.WriteLine($"[rebirth] player {playerId}: level {player.CurrentLevel} -> 1, rebirth #{outcome.RebirthCount}, renowned {renowned}, shards {shards}, damage bonus {outcome.DamageBonusPct}%");
                }
                catch (Exception ex)
                {
                    await transaction.SafeRollbackAsync(CancellationToken.None);
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
            // The same number AwardLegacyShardsAsync pays (2026-11-13).
            int shards = RebirthRules.ShardsForLevel(level);

            var characters = await db.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == playerId).CountAsync(cancellationToken);
            var letGo = await HallOfAncestorsEngine.WouldReleaseAsync(db, playerId, cancellationToken);

            int placed =
                (player.BaseStrength - AttributeRegistry.StartingValue(AttributeRegistry.Might))
                + (player.BaseDexterity - AttributeRegistry.StartingValue(AttributeRegistry.Finesse))
                + (player.BaseConstitution - AttributeRegistry.StartingValue(AttributeRegistry.Vigour))
                + (player.BaseLuck - AttributeRegistry.StartingValue(AttributeRegistry.Fortune));

            var preview = new RebirthPreview
            {
                RebirthCount = player.RebirthCount,
                RenownedRebirths = player.RenownedRebirths,
                Level = level,
                RenownLevel = RebirthRules.RenownLevel,
                MinimumLevel = RebirthRules.MinimumLevel,
                Renowned = renowned,
                DamageBonusPctNow = RebirthRules.DamageBonusPct(player.RenownedRebirths),
                DamageBonusPctAfter = RebirthRules.DamageBonusPct(player.RenownedRebirths + (renowned ? 1 : 0)),
                DamageBonusPctCap = RebirthRules.MaxDamageBonusPct,
                ShardsEarned = shards,

                MaxRebirths = RebirthRules.BloodlineRebirths,
                Capped = false,
                RewardedRebirths = RebirthRules.RewardedRebirths,
                BloodlineCeilingNow = BreedingAptitudes.CeilingFor(player.RebirthCount),
                BloodlineCeilingAfter = BreedingAptitudes.CeilingFor(player.RebirthCount + 1),
                SkillPointsFromRebirthsNow = RebirthRules.SkillPointsFor(player.RebirthCount),
                SkillPointsFromRebirthsAfter = RebirthRules.SkillPointsFor(player.RebirthCount + 1),
                ReLevelXpBonusPct = RebirthRules.ReLevelXpBonusPct,
                PeakLevelAfter = level,
                SecondPetSlotRebirths = RebirthRules.SecondPetSlotRebirths,
                KeepPriceByTier = Enumerable.Range(0, RebirthRules.TopQualityTier + 1).Select(t => RebirthRules.KeepPrice(t)).ToArray(),
                Roadmap = Domain.Progression.RebirthRoadmap.Build(),

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
                Boons = BoonView.For(player.LegacyPerks),
                Trials = TrialView.For(player.ActiveRebirthTrial, player.CompletedRebirthTrials),
                ActiveTrial = player.ActiveRebirthTrial,
                LifeSeconds = player.LifeStartedAtEpoch > 0 ? Math.Max(0, GameClock.UtcNowOffset.ToUnixTimeSeconds() - player.LifeStartedAtEpoch) : 0,
                BestLifeSecondsTo50 = player.BestLifeSecondsTo50,
                BestLifeSecondsTo100 = player.BestLifeSecondsTo100,
                VillageBuildingLevels = await db.VillageInfrastructures.AsNoTracking()
                    .Where(v => v.PlayerId == playerId).SumAsync(v => (int?)v.CurrentLevel, cancellationToken) ?? 0,
            };
            // The Boons again, now that the balance is known: each row says how
            // many ranks "buy all" would take (several ranks in one command).
            preview.Boons = BoonView.For(player.LegacyPerks, preview.ShardBalance);
            return preview;
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
                EndTimestamp = GameClock.UtcNowOffset.ToUnixTimeSeconds() + 90L * 24L * 60L * 60L,
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

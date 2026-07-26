using System;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Shared
{
    // Modul: larder. Where auto-eat kicks in for a player who has never
    // touched the slider. 30 percent leaves enough headroom for one more
    // monster hit to land before the heal resolves at 10Hz, without burning
    // food on scratches.
    public static class AutoEatDefaults
    {
        public const int ThresholdPct = 30;
    }

    // Modul: multi-slot simulation. The two starting values every character
    // slot hydrates with, named once so slot 1's long-standing literals and
    // slots 2-3's new ones cannot drift apart.
    public static class CharacterSlotDefaults
    {
        // Milli-HP, matching the engine's thousandths convention everywhere
        // else (the outbound packet divides by 1000).
        public const int MilliHp = 100000;
        public const int RequiredProgressTicks = 50;
    }

    public class StateCheckpointManager
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<long, TickStatePayload> _dirtyStates = new();
        private readonly RedisSessionCache? _redisSessionCache;

        private Action<long>? _forceDisconnectCallback;

        public void RegisterDisconnectCallback(Action<long> callback)
        {
            _forceDisconnectCallback = callback;
        }

        public StateCheckpointManager(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _redisSessionCache = serviceProvider.GetService<RedisSessionCache>();
        }

        public void TrackState(ref TickStatePayload state)
        {
            bool reachedCheckpointBoundary = state.TicksSinceLastFlush >= 3000 || state.InventorySpaceRemaining <= 0;
            if (_redisSessionCache != null && (state.IsDirty || state.RequiresRedisFlush || reachedCheckpointBoundary))
            {
                if (_redisSessionCache.TryStoreFrame(ref state))
                {
                    if (reachedCheckpointBoundary)
                    {
                        state.TicksSinceLastFlush = 0;
                    }

                    state.IsDirty = false;
                    _dirtyStates[state.PlayerId] = state;
                    return;
                }
            }

            if (reachedCheckpointBoundary)
            {
                bool committed = FlushStateAndAdvance(ref state);
                if (committed)
                {
                    state.TicksSinceLastFlush = 0;
                    state.IsDirty = false;
                    _dirtyStates.TryRemove(state.PlayerId, out _);
                }
                else
                {
                    // The flush failed - either a Serializable conflict that
                    // exhausted its retries, or another DbException. Never
                    // silently discard progress here: TicksSinceLastFlush is
                    // left as-is so the next TrackState call re-attempts the
                    // checkpoint immediately, and IsDirty/_dirtyStates are
                    // forced so this player is requeued for the next flush
                    // cycle (including FlushAllGracefully at shutdown)
                    // regardless of what IsDirty held on entry.
                    state.IsDirty = true;
                    _dirtyStates[state.PlayerId] = state;
                }
            }
            else if (state.IsDirty)
            {
                _dirtyStates[state.PlayerId] = state;
            }
        }

        public bool FlushStateAndAdvance(ref TickStatePayload state)
        {
            _redisSessionCache?.TryStoreFrame(ref state);
            bool committed = FlushState(state).GetAwaiter().GetResult();
            if (committed)
            {
                state.LogicEpochCounter++;
                state.IsDirty = false;
            }
            return committed;
        }

        public async Task<bool> FlushState(TickStatePayload state)
        {
            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

            // Modul: the retried delegate below is only ever allowed to
            // return false via the explicit split-brain branch (a detected,
            // expected condition, not a failure) - it deliberately does NOT
            // catch-and-return-false on a thrown exception. A thrown
            // Serializable-conflict (40001) or deadlock (40P01) must
            // propagate out of the delegate for CreateExecutionStrategy to
            // retry it; catching it here would silently defeat that retry
            // and reproduce the exact silent-data-loss bug this method was
            // refactored to close. Only the outer catch, reached once
            // retries are exhausted or the exception is not retryable,
            // reports failure to the caller.
            var strategy = dbContext.Database.CreateExecutionStrategy();
            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    dbContext.ChangeTracker.Clear();

                    using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                    // Pessimistic row-level epoch lock. FOR UPDATE prevents concurrent epoch modification.
                    var player = await dbContext.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", state.PlayerId)
                        .FirstOrDefaultAsync();

                    if (player != null)
                    {
                        // Split-brain vector timestamp sieve: if db epoch is strictly ahead, a concurrent node already wrote.
                        if (player.LogicEpochCounter > state.LogicEpochCounter)
                        {
                            await transaction.RollbackAsync();

                            // Calculate asset delta and compensate via Gold mailbox write (Module 31.2.2).
                            long epochDelta = player.LogicEpochCounter - state.LogicEpochCounter;
                            long compensationGold = epochDelta * 500L;

                            TelemetryStreamer.TryWrite(new TelemetryEvent
                            {
                                PlayerId = state.PlayerId,
                                EventType = 5,
                                Value1 = (int)(player.LogicEpochCounter & 0x7FFFFFFF),
                                Value2 = (int)(state.LogicEpochCounter & 0x7FFFFFFF),
                                Timestamp = Environment.TickCount64
                            });

                            long capturedPlayerId = state.PlayerId;
                            long capturedGold = compensationGold;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var bgScope = _serviceProvider.CreateScope();
                                    var bgDb = bgScope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                                    using var bgTx = await bgDb.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                                    bgDb.MailboxInstances.Add(new MailboxInstance
                                    {
                                        PlayerId = capturedPlayerId,
                                        BaseItemId = "GOLD_COMPENSATION",
                                        QualityTier = 0,
                                        Quantity = 0,
                                        GoldAttachment = capturedGold,
                                        IsClaimed = false,
                                        IsPending = false,
                                        ReceivedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                    });
                                    await bgDb.SaveChangesAsync();
                                    await bgTx.CommitAsync();
                                }
                                catch (Exception bgEx)
                                {
                                    Console.WriteLine($"Split-brain mailbox compensation failed for player {capturedPlayerId}: {bgEx.Message}");
                                }
                            });

                            _forceDisconnectCallback?.Invoke(state.PlayerId);
                            _dirtyStates.TryRemove(state.PlayerId, out _);
                            return false;
                        }

                        player.CurrentLevel = state.CurrentLevel;
                        player.CurrentXp = state.CurrentXp;
                        player.SelectedLineageId = state.SelectedLineageId;
                        player.LastLogoutTimestamp = state.LastLogoutTimestamp;
                        player.AccumulatedTimeBankSeconds = (int)(state.AccumulatedTimeBankMs / 1000L);
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.OffensivePotionDurationMs = state.OffensivePotionDurationMs;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.DefensivePotionDurationMs = state.DefensivePotionDurationMs;

                        // Modul: larder. The auto-eat step consumes from these
                        // slots every time it fires, so the payload - not the
                        // PlayerRecords row LarderEngine wrote - is the current
                        // truth about what is left. Without this, food eaten
                        // during a session was restored in full at the next
                        // login: infinite sustain from one stocking.
                        player.LarderSlot1ItemId = state.Food1_Count > 0 ? state.Food1_ItemId : 0;
                        player.LarderSlot1Count = state.Food1_Count;
                        player.LarderSlot2ItemId = state.Food2_Count > 0 ? state.Food2_ItemId : 0;
                        player.LarderSlot2Count = state.Food2_Count;
                        player.LarderSlot3ItemId = state.Food3_Count > 0 ? state.Food3_ItemId : 0;
                        player.LarderSlot3Count = state.Food3_Count;
                        player.AutoEatThresholdPct = state.AutoEatThreshold;
                        player.LogicEpochCounter = state.LogicEpochCounter + 1;
                        player.BankedChronoSeconds = state.BankedChronoSeconds;
                        player.IsChronoAccelerating = state.IsChronoAccelerating;
                        player.Quarantine_Active = state.Quarantine_Active;
                        player.IsQuarantined = state.IsQuarantined;
                        player.BaseStrength = state.STR;
                        player.BaseDexterity = state.DEX;
                        player.BaseConstitution = state.CON;
                        player.BaseLuck = state.LCK;
                        // Modul: per-character equipment. The flush used to
                        // mirror the payload's equipped ids back onto
                        // PlayerRecords. Equipment now lives on CharacterRecord
                        // and EquipmentSlotEngine is its only writer, committing
                        // inside its own Serializable transaction - so the
                        // payload's copy is a read-through cache and writing it
                        // back here would let a stale register overwrite a fresh
                        // equip that landed between two checkpoints.
                        long consumableFlushEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.ActiveOffensivePotionExpiresEpoch = state.OffensivePotionDurationMs > 0 ? consumableFlushEpoch + state.OffensivePotionDurationMs / 1000L : 0L;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.ActiveDefensivePotionExpiresEpoch = state.DefensivePotionDurationMs > 0 ? consumableFlushEpoch + state.DefensivePotionDurationMs / 1000L : 0L;
                        player.ActiveFoodId = state.ActiveFoodBuffId;
                        player.ActiveFoodExpiresEpoch = state.FoodBuffDurationMs > 0 ? consumableFlushEpoch + state.FoodBuffDurationMs / 1000L : 0L;
                        player.XpPenaltyExpiresEpoch = state.XpPenaltyExpiresEpoch;
                        player.PremiumDiamonds = state.PremiumCurrency;
                        player.AvailableSkillPoints = state.AvailableSkillPoints;

                        // Modul: larder. The auto-eat step consumes from these
                        // slots every time it fires, so the payload - not the
                        // PlayerRecords row LarderEngine wrote - is the current
                        // truth about what is left. Without this, food eaten
                        // during a session was restored in full at the next
                        // login: infinite sustain from one stocking.
                        player.LarderSlot1ItemId = state.Food1_Count > 0 ? state.Food1_ItemId : 0;
                        player.LarderSlot1Count = state.Food1_Count;
                        player.LarderSlot2ItemId = state.Food2_Count > 0 ? state.Food2_ItemId : 0;
                        player.LarderSlot2Count = state.Food2_Count;
                        player.LarderSlot3ItemId = state.Food3_Count > 0 ? state.Food3_ItemId : 0;
                        player.LarderSlot3Count = state.Food3_Count;
                        player.AutoEatThresholdPct = state.AutoEatThreshold;
                        await UpsertAccountChronoRegistryAsync(dbContext, state);
                        await UpsertChroniclePassAsync(dbContext, state);
                        await UpsertLifetimeAchievementsAsync(dbContext, player, state);
                        await QuestEngine.UpsertDailyQuestProgressAsync(dbContext, state);
                    }
                    else
                    {
                        dbContext.PlayerRecords.Add(new PlayerRecord
                        {
                            Id = state.PlayerId,
                            CurrentLevel = state.CurrentLevel,
                            CurrentXp = state.CurrentXp,
                            SelectedLineageId = state.SelectedLineageId,
                            LastLogoutTimestamp = state.LastLogoutTimestamp,
                            AccumulatedTimeBankSeconds = (int)(state.AccumulatedTimeBankMs / 1000L),
                            LogicEpochCounter = state.LogicEpochCounter + 1,
                            BankedChronoSeconds = state.BankedChronoSeconds,
                            IsChronoAccelerating = state.IsChronoAccelerating,
                            Quarantine_Active = state.Quarantine_Active,
                            IsQuarantined = state.IsQuarantined
                        });
                        await UpsertAccountChronoRegistryAsync(dbContext, state);
                        await UpsertChroniclePassAsync(dbContext, state);
                    }

                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return true;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to flush state for player {state.PlayerId}: {ex.Message}");
                return false;
            }
        }

        public async Task<TickStatePayload> LoadPlayerState(long playerId)
        {
            // Modul: login-time state hydration - retry-configured so both
            // this method's LoadOrUpdateAccountChronoRegistryAsync call
            // (which opens its own Serializable transaction) and every
            // other read below transparently survive a transient failure
            // or Serializable conflict during a concurrent login burst
            // (cold-boot recovery, many logins at once) instead of failing
            // the session outright.
            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

            var player = await dbContext.PlayerRecords.FindAsync(playerId);
            if (player == null)
            {
                var defaultPayload = new TickStatePayload
                {
                    PlayerId = playerId,
                    ActiveActivityId = 1,
                    CurrentProgressTicks = 0,
                    RequiredProgressTicks = 50,
                    InventorySpaceRemaining = 20,
                    PlayerHp = 100000,
                    CurrentGold = 10000,
                    PremiumCurrency = 0,
                    SpeedMultiplier = 1,
                    LogicEpochCounter = 0,
                    LegacyShardBalance = 0,
                    CitizenMultiSlotsUnlocked = 0,
                    GuildLogisticsCurrentStock = 0L,
                    GuildLogisticsTargetRequirement = 0L,
                    CombatSimulationMatchId = 0L,
                    CombatSimulationTurnCounter = 0,
                    CombatSimulationDamageDelta = 0,
                    AccountId = ResolveAccountId(playerId, Guid.Empty),
                    ActiveMentorPlayerId = 0L,
                    MentorshipExpBonusMultiplier = 1.0,
                    ForgeLevel = 0,
                    InnLevel = 0,
                    BreedingLevel = 0,
                    AcademyLevel = 0,
                    CurrentPopulationCount = 0,
                    ActiveMentorshipContractCount = 0,
                    CachedMaxPopulationCapacity = VillageManagementEngine.CalculatePopulationCapacity(0),
                    CachedInnMaturationBonus = 0,
                    CachedCurrentToolTier = 0,
                    IsQuarantined = false,
                    ActiveLanguageState = 1,
                    ActiveChroniclePassLevel = 0,
                    AccumulatedSeasonalXp = 0,
                    CurrentMana = ActiveSkillEngine.ComputeMaxMana(1),
                    AvailableSkillPoints = 0,
                    UnlockedSkillsBitmask = 0
                };
                defaultPayload.InitializeObfuscation(GenerateSessionXorKey(playerId, 0));
                return defaultPayload;
            }

            int miningMonolith = 0;
            int woodMonolith = 0;
            long guildLogisticsStock = 0L;
            long guildLogisticsTarget = 0L;
            long combatMatchId = 0L;
            int combatTurnCounter = 0;
            Guid activeCrossShardMatchId = Guid.Empty;
            int activeMatchMmr = 0;
            long globalNodeRemainingHp = 0L;
            long activeGuildWarId = 0L;
            if (player.GuildId > 0)
            {
                var guild = await dbContext.GuildRecords.FindAsync(player.GuildId);
                if (guild != null)
                {
                    miningMonolith = guild.MiningMonolithLevel;
                    woodMonolith = guild.WoodcuttingMonolithLevel;
                }

                guildLogisticsStock = await dbContext.GuildLogisticsDepots
                    .AsNoTracking()
                    .Where(d => d.GuildId == player.GuildId)
                    .SumAsync(d => (long?)d.CurrentStock) ?? 0L;
                guildLogisticsTarget = await dbContext.GuildLogisticsDepots
                    .AsNoTracking()
                    .Where(d => d.GuildId == player.GuildId)
                    .SumAsync(d => (long?)d.TargetRequirement) ?? 0L;

                var activeCombatMatch = await dbContext.GuildWarActiveMatches
                    .AsNoTracking()
                    .Where(m => m.AttackingGuildId == player.GuildId || m.DefendingGuildId == player.GuildId)
                    .OrderBy(m => m.MatchId)
                    .FirstOrDefaultAsync();
                if (activeCombatMatch != null)
                {
                    combatMatchId = activeCombatMatch.MatchId;
                    combatTurnCounter = (int)GuildCombatSimulationEngine.ExtractTurnCounter(activeCombatMatch.CurrentStateBitmask);
                }

                var crossShardMatch = await dbContext.GuildMatchmakingSnapshots
                    .AsNoTracking()
                    .Where(m => !m.IsComplete && (m.AttackerGuildId == player.GuildId || m.DefenderGuildId == player.GuildId))
                    .OrderBy(m => m.TournamentGroupIndex)
                    .FirstOrDefaultAsync();
                if (crossShardMatch != null)
                {
                    activeCrossShardMatchId = crossShardMatch.MatchUuid;
                    activeMatchMmr = crossShardMatch.ActiveMatchMmr;
                    globalNodeRemainingHp = crossShardMatch.GlobalNodeRemainingHp;
                }

                // Modul: Play Mode audit fix. TickStatePayload.ActiveGuildWarId
                // gates every live contribution to the weekly guild-war
                // scoreboard (combat kills, tier-5 crafts, and
                // ContributeToWarSupply all check "> 0" in SimulationEngine
                // before enqueueing any points) and drives the client's
                // entire UiGuildWarPanel active/inactive state - but nothing
                // anywhere ever assigned it, so every session hydrated with
                // it permanently 0 even during a real active war. This is
                // the same GuildWarMatches row BuildGuildWarGroup's own
                // scoreboard reads from once populated live, distinct from
                // GuildWarActiveMatches (the turn-based combat sim match
                // above) and GuildMatchmakingSnapshots (cross-shard).
                var activeGuildWar = await dbContext.GuildWarMatches
                    .AsNoTracking()
                    .Where(m => m.IsActive && (m.GuildA_Id == player.GuildId || m.GuildB_Id == player.GuildId))
                    .FirstOrDefaultAsync();
                if (activeGuildWar != null)
                {
                    activeGuildWarId = activeGuildWar.MatchId;
                }
            }

            var characters = await dbContext.CharacterRecords
                .Include(c => c.Lineage)
                .Where(c => c.PlayerId == playerId && !c.IsLockedInEscrow && !dbContext.MentorshipAcademyAssignments.Any(m => m.CharacterId == c.Id))
                .Take(3)
                .ToListAsync();

            var achievements = await dbContext.PlayerAchievements.FindAsync(playerId);
            int achievementFlags = achievements?.ClaimedAchievementFlags ?? 0;

            var codexEntries = await dbContext.MonsterCodexEntries.Where(c => c.PlayerId == playerId).ToListAsync();
            int completedAreas = 0;
            for (int region = 1; region <= 10; region++)
            {
                var monstersInRegion = ContentRegistry.Monsters.ToArray().Where(m => ContentRegistry.GetMonsterRegionTier(m.Id) == region).ToList();
                if (monstersInRegion.Count > 0 && monstersInRegion.All(m => codexEntries.Any(c => c.MonsterId == m.Id && c.KillCount >= 1000)))
                {
                    completedAreas |= (1 << region);
                }
            }

            // Modul 13 fix: RaceId filters here previously used raw literals (1, 3, 4)
            // that predate RaceIds and never matched it - see the same fix in
            // SimulationEngine's MasteryUpdateQueue dispatcher for details.
            var masteries = await dbContext.PlayerRaceMasteries.Where(m => m.PlayerId == playerId).ToListAsync();
            int humanMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Human)?.MasteryLevel ?? 0;
            int vilaMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Vila)?.MasteryLevel ?? 0;
            int draugrMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Draugr)?.MasteryLevel ?? 0;
            int koboldMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Kobold)?.MasteryLevel ?? 0;
            int vodnikMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Vodnik)?.MasteryLevel ?? 0;
            int moosleuteMastery = masteries.FirstOrDefault(m => m.RaceId == RaceIds.Moosleute)?.MasteryLevel ?? 0;

            // Modul: inventory census. Hydration used to write "20 + bonus"
            // directly into InventorySpaceRemaining without ever looking at the
            // backpack, which meant a relogin was the only way to get inventory
            // space back - and gave a player with a genuinely full pack twenty
            // phantom slots. Counted here from the real rows, once per login, by
            // the same helper the per-kill census uses so the two can never
            // disagree about what a slot is.
            int backpackCapacity = SimulationEngine.DefaultBackpackCapacity + RaceMasteryResolver.GetHumanVaultBonusSlots(humanMastery);
            int occupiedBackpackSlots = await CombatLootEngine.CountOccupiedBackpackSlotsAsync(dbContext, playerId);

            // Modul 16/21: EquippedWeaponId/ArmorId are persisted, but the
            // derived stat totals StatsCalculator reads every tick are not - they
            // must be recomputed once at login rather than starting zeroed until
            // the player's next equip action.
            // Modul: per-character equipment. Totals are per character now, so
            // this resolves the main character's gear rather than the account's.
            // Slots 2 and 3 get the same treatment further down, where their
            // parked activity state is filled in - each character has to fight
            // in its own armour.
            CharacterRecord? mainCharacterRecord = characters.Count > 0 ? characters[0] : null;

            EquippedAffixTotals equippedAffixTotals = default;
            int equippedWeaponSetId = 0, equippedArmorSetId = 0, equippedLeggingsSetId = 0;
            if (mainCharacterRecord != null)
            {
                (equippedAffixTotals, equippedWeaponSetId, equippedArmorSetId, equippedLeggingsSetId) =
                    await EquipmentSlotEngine.ComputeEquippedTotalsAsync(dbContext, mainCharacterRecord);
            }

            var mentorCount = await dbContext.MentorshipAcademyAssignments
                .CountAsync(m => m.PlayerId == playerId);

            var mentorshipContract = await dbContext.MentorshipContracts
                .AsNoTracking()
                .Where(m => m.MenteePlayerId == playerId)
                .FirstOrDefaultAsync();

            // Modul 16: resolve any upgrade that matured while this player was
            // offline before hydrating the login payload, so a returning
            // player never sees a stale CurrentLevel or a queue slot that is
            // actually already free.
            await VillageManagementEngine.ResolveMaturedUpgradesAsync(dbContext, playerId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var villageRows = await dbContext.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId)
                .ToListAsync();
            int forgeLevel = 0;
            int innLevel = 0;
            int breedingLevel = 0;
            int academyLevel = 0;
            int lumberjackLevel = 0;
            int quarryLevel = 0;
            int mineLevel = 0;
            int warehouseLevel = 0;
            int townHallLevel = 0;
            int craftingWorkshopLevel = 0;
            byte pendingUpgradeBuildingId = 0;
            long pendingUpgradeCompletesAtEpoch = 0;
            for (int i = 0; i < villageRows.Count; i++)
            {
                if (villageRows[i].BuildingId == VillageManagementEngine.ForgeBuildingId) forgeLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.InnBuildingId) innLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.BreedingGroundsBuildingId) breedingLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.MentorshipAcademyBuildingId) academyLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.LumberjackBuildingId) lumberjackLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.QuarryBuildingId) quarryLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.MineBuildingId) mineLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.WarehouseBuildingId) warehouseLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.TownHallBuildingId) townHallLevel = villageRows[i].CurrentLevel;
                else if (villageRows[i].BuildingId == VillageManagementEngine.CraftingWorkshopBuildingId) craftingWorkshopLevel = villageRows[i].CurrentLevel;

                if (villageRows[i].UpgradeTargetLevel > 0)
                {
                    pendingUpgradeBuildingId = (byte)villageRows[i].BuildingId;
                    pendingUpgradeCompletesAtEpoch = villageRows[i].UpgradeCompletesAtEpoch;
                }
            }

            var villageCommodityRows = await dbContext.CommodityRecords
                .AsNoTracking()
                .Where(c => c.PlayerId == playerId && (
                    c.ItemId == VillageManagementEngine.WoodCommodityId ||
                    c.ItemId == VillageManagementEngine.StoneCommodityId ||
                    c.ItemId == VillageManagementEngine.IronOreCommodityId))
                .ToListAsync();
            long woodStock = villageCommodityRows.FirstOrDefault(c => c.ItemId == VillageManagementEngine.WoodCommodityId)?.Quantity ?? 0L;
            long stoneStock = villageCommodityRows.FirstOrDefault(c => c.ItemId == VillageManagementEngine.StoneCommodityId)?.Quantity ?? 0L;
            long ironOreStock = villageCommodityRows.FirstOrDefault(c => c.ItemId == VillageManagementEngine.IronOreCommodityId)?.Quantity ?? 0L;

            // Active Skill Tree: hydrate the persisted unlock set into a
            // bitmask once at login (see ActiveSkillEngine) - never queried
            // from the 10 Hz hot loop again this session.
            var unlockedSkillRows = await dbContext.PlayerSkillUnlocks
                .AsNoTracking()
                .Where(s => s.PlayerId == playerId)
                .ToListAsync();
            uint unlockedSkillsBitmask = 0;
            for (int i = 0; i < unlockedSkillRows.Count; i++)
            {
                unlockedSkillsBitmask = ActiveSkillEngine.WithSkillUnlocked(unlockedSkillsBitmask, unlockedSkillRows[i].SkillId);
            }

            int activeResidentCount = await dbContext.VillageResidents
                .AsNoTracking()
                .CountAsync(v => v.PlayerId == playerId && v.IsActive);

            int activeMentorshipContracts = await dbContext.MentorshipContracts
                .AsNoTracking()
                .CountAsync(m => m.MenteePlayerId == playerId || m.MentorPlayerId == playerId);

            var legacyRows = await dbContext.PlayerLegacyLedgers
                .AsNoTracking()
                .Where(l => l.PlayerId == playerId)
                .ToListAsync();
            long shardTotal = 0L;
            int unlockedSlots = 0;
            for (int i = 0; i < legacyRows.Count; i++)
            {
                shardTotal += legacyRows[i].LegacyShardBalance;
                unlockedSlots |= legacyRows[i].CitizenMultiSlotsUnlocked;
            }
            if (shardTotal > int.MaxValue) shardTotal = int.MaxValue;

            int totalAchievements = await dbContext.PlayerLifetimeAchievements
                .AsNoTracking()
                .CountAsync(a => a.PlayerId == playerId && a.IsClaimed);

            var chroniclePass = await dbContext.PlayerChroniclePasses
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlayerId == playerId);

            long currentUnixTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var accountChrono = await LoadOrUpdateAccountChronoRegistryAsync(dbContext, player, currentUnixTs);
            bool chronoAccelerationActive = accountChrono.BankedChronoSeconds > 0 &&
                accountChrono.ActiveSpeedMultiplier > 1.0 &&
                accountChrono.AccelerationTerminationEpoch > currentUnixTs;

            (float codexYieldMultiplier, float codexDamageMultiplier) = await CodexEngine.CalculateActiveMultipliersAsync(playerId, dbContext);

            long questLoadEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var dailyQuests = await QuestEngine.EnsureAndLoadDailyQuestsAsync(dbContext, playerId, questLoadEpochSeconds);
            await dbContext.SaveChangesAsync();

            // Modul: Play Mode audit fix. CurrentGold was previously hardcoded
            // to 10000 on every hydration regardless of the real balance -
            // AuthenticationEngine seeds CommodityRecords ItemId="gold" at
            // registration (1000, not 10000) and every gold-earning/-spending
            // path (RedisWriteBehindEngine's delta flush, MarketEscrowEngine,
            // MarketOrderBookEngine) reads/writes that same row, so it is the
            // one authoritative balance - a live Play Mode session confirmed
            // this by comparing a player's real CommodityRecords balance
            // against what every login/reconnect actually loaded.
            long loadedGold = await dbContext.CommodityRecords
                .AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity)
                .FirstOrDefaultAsync();

            // Modul: Deferred Part 5 Implementation, Part 2 - consumable
            // expiry hydration reference clock (see the ActiveOffensive/
            // Defensive/Food assignments in the payload build below).
            long nowEpochSeconds = questLoadEpochSeconds;

            var payload = new TickStatePayload
            {
                CachedCodexYieldMultiplier = codexYieldMultiplier,
                CachedCodexDamageMultiplier = codexDamageMultiplier,
                PlayerId = player.Id,
                AccountId = accountChrono.AccountId,
                CurrentLevel = player.CurrentLevel,
                CurrentXp = player.CurrentXp,
                SelectedLineageId = player.SelectedLineageId,
                LastLogoutTimestamp = player.LastLogoutTimestamp,
                AccumulatedTimeBankMs = player.AccumulatedTimeBankSeconds * 1000L,
                // Modul: Deploy activation fix. Was hardcoded to 1. The block
                // further down overwrites this with characters[0]'s real
                // persisted activity, but ONLY when that query returned a
                // character - and it excludes any character currently lent
                // out as a mentor (MentorshipAcademyAssignments). A player
                // whose only character is mentoring therefore silently
                // resumed as though deployed on activity 1 forever, with no
                // character to actually run it. Idle (0) is the honest
                // default for "no eligible character".
                ActiveActivityId = 0,
                CurrentProgressTicks = 0,
                RequiredProgressTicks = 50,
                // Modul: inventory census. Was "20 + bonus" unconditionally,
                // regardless of what the backpack actually held - so a player
                // who logged out with a full pack logged back in with twenty
                // free slots, and the number then only ever fell. Both fields
                // are now derived from a real count of occupied slots taken just
                // above; capacity is carried separately so the difference can be
                // recomputed rather than only decremented.
                InventoryCapacity = backpackCapacity,
                InventorySpaceRemaining = Math.Max(0, backpackCapacity - occupiedBackpackSlots),

                // Modul: larder. Restores the three auto-eat slots and the
                // player's chosen threshold. All four were previously
                // session-only fields with no storage behind them, so the larder
                // was empty at every login and the threshold reverted to the
                // default. A persisted 0 threshold means "never configured" -
                // taking it literally would mean auto-eat only fires at exactly
                // 0 HP, i.e. never.
                Food1_ItemId = player.LarderSlot1ItemId,
                Food1_Count = player.LarderSlot1Count,
                Food2_ItemId = player.LarderSlot2ItemId,
                Food2_Count = player.LarderSlot2Count,
                Food3_ItemId = player.LarderSlot3ItemId,
                Food3_Count = player.LarderSlot3Count,
                AutoEatThreshold = player.AutoEatThresholdPct > 0 ? player.AutoEatThresholdPct : AutoEatDefaults.ThresholdPct,
                PlayerHp = 100000,
                CurrentGold = loadedGold,
                PremiumCurrency = player.PremiumDiamonds,
                SpeedMultiplier = chronoAccelerationActive ? (int)accountChrono.ActiveSpeedMultiplier : 1,
                GuildId = player.GuildId,
                ActiveGuildWarId = activeGuildWarId,
                ActiveCrossShardMatchId = activeCrossShardMatchId,
                ActiveMatchMmr = activeMatchMmr,
                GlobalNodeRemainingHp = globalNodeRemainingHp,
                CachedMiningMonolithLevel = miningMonolith,
                CachedWoodcuttingMonolithLevel = woodMonolith,
                CachedMentorCount = mentorCount,
                ClaimedAchievementFlags = achievementFlags,
                TotalAchievementsClaimedCount = (uint)totalAchievements,
                CompletedAreaFlags = completedAreas,
                HumanMasteryLevel = humanMastery,
                VilaMasteryLevel = vilaMastery,
                DraugrMasteryLevel = draugrMastery,
                KoboldMasteryLevel = koboldMastery,
                VodnikMasteryLevel = vodnikMastery,
                MoosleuteMasteryLevel = moosleuteMastery,
                STR = player.BaseStrength,
                DEX = player.BaseDexterity,
                CON = player.BaseConstitution,
                LCK = player.BaseLuck,
                EquippedWeaponId = mainCharacterRecord?.EquippedWeaponId ?? 0L,
                EquippedHelmetId = mainCharacterRecord?.EquippedHelmetId ?? 0L,
                EquippedArmorId = mainCharacterRecord?.EquippedChestId ?? 0L,
                EquippedGlovesId = mainCharacterRecord?.EquippedGlovesId ?? 0L,
                EquippedLeggingsId = mainCharacterRecord?.EquippedLeggingsId ?? 0L,
                EquippedBootsId = mainCharacterRecord?.EquippedBootsId ?? 0L,
                XpPenaltyExpiresEpoch = player.XpPenaltyExpiresEpoch,

                // Modul: Deferred Part 5 Implementation, Part 2. Durable
                // consumable hydration - the persisted absolute expiry
                // epochs convert back to live millisecond countdowns
                // against the server clock; an already-expired buff loads
                // as inactive (id 0, countdown 0).
                ActiveOffensivePotionId = player.ActiveOffensivePotionExpiresEpoch > nowEpochSeconds ? player.ActiveOffensivePotionId : 0,
                OffensivePotionDurationMs = player.ActiveOffensivePotionExpiresEpoch > nowEpochSeconds ? (int)Math.Min(int.MaxValue, (player.ActiveOffensivePotionExpiresEpoch - nowEpochSeconds) * 1000L) : 0,
                ActiveDefensivePotionId = player.ActiveDefensivePotionExpiresEpoch > nowEpochSeconds ? player.ActiveDefensivePotionId : 0,
                DefensivePotionDurationMs = player.ActiveDefensivePotionExpiresEpoch > nowEpochSeconds ? (int)Math.Min(int.MaxValue, (player.ActiveDefensivePotionExpiresEpoch - nowEpochSeconds) * 1000L) : 0,
                ActiveFoodBuffId = player.ActiveFoodExpiresEpoch > nowEpochSeconds ? player.ActiveFoodId : 0,
                FoodBuffDurationMs = player.ActiveFoodExpiresEpoch > nowEpochSeconds ? (int)Math.Min(int.MaxValue, (player.ActiveFoodExpiresEpoch - nowEpochSeconds) * 1000L) : 0,
                CachedAffixTotals = equippedAffixTotals,
                CachedWeaponSetId = equippedWeaponSetId,
                CachedArmorSetId = equippedArmorSetId,
                CachedLeggingsSetId = equippedLeggingsSetId,
                LogicEpochCounter = player.LogicEpochCounter,
                BankedChronoSeconds = accountChrono.BankedChronoSeconds,
                IsChronoAccelerating = chronoAccelerationActive,
                ActiveChronoSpeedMultiplier = chronoAccelerationActive ? accountChrono.ActiveSpeedMultiplier : 1.0,
                ActiveChronoLockExpirationTicks = chronoAccelerationActive ? accountChrono.AccelerationTerminationEpoch : 0L,
                LegacyShardBalance = (int)shardTotal,
                CitizenMultiSlotsUnlocked = unlockedSlots,
                GuildLogisticsCurrentStock = guildLogisticsStock,
                GuildLogisticsTargetRequirement = guildLogisticsTarget,
                CombatSimulationMatchId = combatMatchId,
                CombatSimulationTurnCounter = combatTurnCounter,
                CombatSimulationDamageDelta = 0,
                ActiveMentorPlayerId = mentorshipContract?.MentorPlayerId ?? 0L,
                MentorshipExpBonusMultiplier = mentorshipContract?.ExpBonusMultiplier ?? 1.0,
                ForgeLevel = ClampByte(forgeLevel),
                InnLevel = ClampByte(innLevel),
                BreedingLevel = ClampByte(breedingLevel),
                AcademyLevel = ClampByte(academyLevel),
                CurrentPopulationCount = ClampByte(activeResidentCount),
                ActiveMentorshipContractCount = ClampByte(activeMentorshipContracts),
                LumberjackLevel = ClampByte(lumberjackLevel),
                QuarryLevel = ClampByte(quarryLevel),
                MineLevel = ClampByte(mineLevel),
                WarehouseLevel = ClampByte(warehouseLevel),
                TownHallLevel = townHallLevel,
                CraftingWorkshopLevel = ClampByte(craftingWorkshopLevel),
                PendingUpgradeBuildingId = pendingUpgradeBuildingId,
                PendingUpgradeCompletesAtEpoch = pendingUpgradeCompletesAtEpoch,
                CachedWoodStock = woodStock,
                CachedStoneStock = stoneStock,
                CachedIronOreStock = ironOreStock,
                CachedCurrentToolTier = forgeLevel,
                CachedLegacyPerks = player.LegacyPerks,
                CachedLogisticsGatheringSpeedBonusPct = player.LogisticsGatheringSpeedBonusPct,
                CachedMaxPopulationCapacity = VillageManagementEngine.CalculatePopulationCapacity(innLevel),
                CachedInnMaturationBonus = innLevel,
                Quarantine_Active = player.Quarantine_Active || player.IsQuarantined,
                IsQuarantined = player.IsQuarantined,
                ActiveLanguageState = 1,
                ActiveChroniclePassLevel = (uint)Math.Max(0, chroniclePass?.PassLevel ?? 0),
                AccumulatedSeasonalXp = (uint)Math.Max(0, chroniclePass?.AccumulatedXp ?? 0),
                CachedClaimedMilestonesBitmask = chroniclePass?.ClaimedMilestonesBitmask ?? 0UL,
                CurrentMana = ActiveSkillEngine.ComputeMaxMana(player.CurrentLevel),
                AvailableSkillPoints = player.AvailableSkillPoints,
                UnlockedSkillsBitmask = unlockedSkillsBitmask
            };

            payload.InitializeObfuscation(GenerateSessionXorKey(playerId, player.LogicEpochCounter));

            QuestEngine.ApplyToPayload(ref payload, dailyQuests, QuestEngine.GetUtcDateKey(questLoadEpochSeconds));

            // Modul: halt reasons. The query above deliberately excludes
            // escrowed characters and any character lent out as an Academy
            // mentor, so a player can legitimately own characters and still
            // have none eligible to deploy. That produced a hub that offered
            // no explanation for why nothing could be started.
            if (characters.Count == 0)
            {
                payload.ActivityHaltReason = Network.ActivityHaltReason.NoEligibleCharacter;
            }

            if (characters.Count > 0)
            {
                payload.Slot1_CharacterId = characters[0].Id;
                payload.Slot1_AgeTicks = characters[0].AgeTicks;
                payload.Slot1_AgePhase = characters[0].AgePhase;
                payload.Slot1_GeneticVector = characters[0].Lineage?.GeneticVector ?? 0;

                // Modul: Play Mode audit fix. ActiveActivityId was hardcoded
                // to 1 above regardless of what the character was actually
                // doing - SimulationEngine.ChangeCharacterActivityAsync
                // correctly persists a real activity onto characters[0] and
                // then immediately triggers a ReloadState to pick it back up
                // live, but LoadPlayerState never read it, so the reload
                // instantly reverted the character to idle. Confirmed live:
                // deploying a fresh character against a monster wrote
                // ActiveActivityId=55 onto its characters row correctly, but
                // every subsequent broadcast kept reporting activity 0/1 and
                // combat never resolved (CurrentMonsterHp stayed 0 forever).
                payload.ActiveActivityId = characters[0].ActiveActivityId;

                // Modul 13.4.3: inherited genetic loci for the active (Slot1)
                // character only - combat/growth are always evaluated against
                // whichever character occupies Slot1, matching activeRaceId's
                // existing derivation.
                var slot1Lineage = characters[0].Lineage;
                if (slot1Lineage != null)
                {
                    var slot1GeneVec = new GeneticVector(slot1Lineage.GeneticVector);
                    payload.LocusSpeed = slot1GeneVec.LocusSpeed.Dominant;
                    payload.LocusCrit = slot1GeneVec.LocusCrit.Dominant;
                    payload.LocusYield = slot1GeneVec.LocusYield.Dominant;
                    payload.IsEpicMutation = slot1Lineage.IsEpicMutation;
                    payload.IsInbred = slot1Lineage.IsInbred;
                }
            }
            if (characters.Count > 1)
            {
                payload.Slot2_CharacterId = characters[1].Id;
                payload.Slot2_AgeTicks = characters[1].AgeTicks;
                payload.Slot2_AgePhase = characters[1].AgePhase;
                payload.Slot2_GeneticVector = characters[1].Lineage?.GeneticVector ?? 0;

                // Modul: multi-slot simulation. These slots' persisted activity
                // assignments were loaded nowhere - only characters[0]'s was
                // read - so a second or third character came back from every
                // login idle regardless of what the player had assigned, and
                // nothing simulated them anyway. Both halves are fixed now.
                payload.Slot2Activity.ActiveActivityId = characters[1].ActiveActivityId;
                payload.Slot2Activity.PlayerHp = CharacterSlotDefaults.MilliHp;
                payload.Slot2Activity.RequiredProgressTicks = CharacterSlotDefaults.RequiredProgressTicks;
                payload.Slot2Activity = await HydrateSlotEquipmentAsync(dbContext, characters[1], payload.Slot2Activity);
            }
            if (characters.Count > 2)
            {
                payload.Slot3_CharacterId = characters[2].Id;
                payload.Slot3_AgeTicks = characters[2].AgeTicks;
                payload.Slot3_AgePhase = characters[2].AgePhase;
                payload.Slot3_GeneticVector = characters[2].Lineage?.GeneticVector ?? 0;

                payload.Slot3Activity.ActiveActivityId = characters[2].ActiveActivityId;
                payload.Slot3Activity.PlayerHp = CharacterSlotDefaults.MilliHp;
                payload.Slot3Activity.RequiredProgressTicks = CharacterSlotDefaults.RequiredProgressTicks;
                payload.Slot3Activity = await HydrateSlotEquipmentAsync(dbContext, characters[2], payload.Slot3Activity);
            }

            return payload;
        }

        // Modul: per-character equipment. Loads one non-main character's gear
        // and the stat totals derived from it into its parked slot state.
        //
        // The equipped ids are persisted but the derived totals are not, so a
        // character whose gear was never recomputed at login would fight naked
        // until its owner happened to re-equip something - the exact bug the
        // main character's login-time recompute was added to prevent, repeated
        // once per extra slot.
        // Taken and returned by value rather than by ref: async methods cannot
        // have ref parameters, and a struct copy at login time costs nothing.
        private static async Task<CharacterActivityState> HydrateSlotEquipmentAsync(FolkIdleDbContext dbContext, CharacterRecord character, CharacterActivityState slot)
        {
            slot.EquippedWeaponId = character.EquippedWeaponId ?? 0L;
            slot.EquippedHelmetId = character.EquippedHelmetId ?? 0L;
            slot.EquippedChestId = character.EquippedChestId ?? 0L;
            slot.EquippedGlovesId = character.EquippedGlovesId ?? 0L;
            slot.EquippedLeggingsId = character.EquippedLeggingsId ?? 0L;
            slot.EquippedBootsId = character.EquippedBootsId ?? 0L;

            (EquippedAffixTotals totals, int weaponSetId, int armorSetId, int leggingsSetId) =
                await EquipmentSlotEngine.ComputeEquippedTotalsAsync(dbContext, character);

            slot.CachedAffixTotals = totals;
            slot.CachedWeaponSetId = weaponSetId;
            slot.CachedArmorSetId = armorSetId;
            slot.CachedLeggingsSetId = leggingsSetId;

            return slot;
        }

        private static byte ClampByte(int value)
        {
            if (value <= 0) return 0;
            if (value >= byte.MaxValue) return byte.MaxValue;
            return (byte)value;
        }

        private static long GenerateSessionXorKey(long playerId, long epoch)
        {
            ulong x = (ulong)playerId;
            x ^= (ulong)epoch + 0x9E3779B97F4A7C15UL + (x << 6) + (x >> 2);
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            long key = unchecked((long)x);
            return key == 0L ? 0x5F3759DF5F3759DFL : key;
        }

        private static Guid ResolveAccountId(long playerId, Guid playerGuid)
        {
            if (playerGuid != Guid.Empty)
            {
                return playerGuid;
            }

            byte[] bytes = new byte[16];
            BitConverter.GetBytes(playerId).CopyTo(bytes, 0);
            bytes[15] = 0x67;
            return new Guid(bytes);
        }

        private static async Task<AccountChronoRegistry> LoadOrUpdateAccountChronoRegistryAsync(FolkIdleDbContext dbContext, PlayerRecord player, long currentUnixTimestamp)
        {
            Guid accountId = ResolveAccountId(player.Id, player.PlayerGuid);

            var strategy = dbContext.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                // player was loaded by the caller before this retry boundary
                // and is mutated below - ChangeTracker.Clear() would detach
                // it (dropping those mutations from the next SaveChangesAsync)
                // unless it is re-attached immediately after clearing.
                dbContext.ChangeTracker.Clear();
                dbContext.Attach(player);

                await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var registry = await dbContext.AccountChronoRegistries
                    .FromSqlRaw("SELECT * FROM account_chrono_registry WHERE \"AccountId\" = {0} FOR UPDATE", accountId)
                    .FirstOrDefaultAsync();

                if (registry == null)
                {
                    registry = new AccountChronoRegistry
                    {
                        AccountId = accountId,
                        BankedChronoSeconds = ChronoBufferEngine.ClampBankedSeconds(player.BankedChronoSeconds),
                        ActiveSpeedMultiplier = 1.0,
                        AccelerationTerminationEpoch = 0L,
                        LastClockSyncEpoch = currentUnixTimestamp
                    };
                    dbContext.AccountChronoRegistries.Add(registry);
                }
                else
                {
                    ChronoBufferEngine.ProcessLoginHandshake(registry, currentUnixTimestamp);
                    if (registry.AccelerationTerminationEpoch <= currentUnixTimestamp || registry.BankedChronoSeconds <= 0)
                    {
                        registry.ActiveSpeedMultiplier = 1.0;
                        registry.AccelerationTerminationEpoch = 0L;
                    }
                }

                player.BankedChronoSeconds = registry.BankedChronoSeconds;
                player.IsChronoAccelerating = registry.ActiveSpeedMultiplier > 1.0 && registry.AccelerationTerminationEpoch > currentUnixTimestamp;

                await dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
                return registry;
            });
        }

        private static async Task UpsertAccountChronoRegistryAsync(FolkIdleDbContext dbContext, TickStatePayload state)
        {
            Guid accountId = state.AccountId == Guid.Empty ? ResolveAccountId(state.PlayerId, Guid.Empty) : state.AccountId;
            var registry = await dbContext.AccountChronoRegistries
                .FromSqlRaw("SELECT * FROM account_chrono_registry WHERE \"AccountId\" = {0} FOR UPDATE", accountId)
                .FirstOrDefaultAsync();

            int bankedSeconds = ChronoBufferEngine.ClampBankedSeconds(state.BankedChronoSeconds);
            double speedMultiplier = state.IsChronoAccelerating && (state.SpeedMultiplier == 2 || state.SpeedMultiplier == 4)
                ? state.SpeedMultiplier
                : 1.0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long terminationEpoch = speedMultiplier > 1.0 ? Math.Max(now, state.ActiveChronoLockExpirationTicks) : 0L;

            if (registry == null)
            {
                dbContext.AccountChronoRegistries.Add(new AccountChronoRegistry
                {
                    AccountId = accountId,
                    BankedChronoSeconds = bankedSeconds,
                    ActiveSpeedMultiplier = speedMultiplier,
                    AccelerationTerminationEpoch = terminationEpoch,
                    LastClockSyncEpoch = now
                });
                return;
            }

            registry.BankedChronoSeconds = bankedSeconds;
            registry.ActiveSpeedMultiplier = speedMultiplier;
            registry.AccelerationTerminationEpoch = terminationEpoch;
            registry.LastClockSyncEpoch = now;
        }

        private static async Task UpsertChroniclePassAsync(FolkIdleDbContext dbContext, TickStatePayload state)
        {
            var pass = await dbContext.PlayerChroniclePasses
                .FromSqlRaw("SELECT * FROM \"PlayerChroniclePasses\" WHERE \"PlayerId\" = {0} FOR UPDATE", state.PlayerId)
                .FirstOrDefaultAsync();

            int passLevel = (int)Math.Min(50U, state.ActiveChroniclePassLevel);
            int seasonalXp = (int)Math.Min(int.MaxValue, state.AccumulatedSeasonalXp);

            if (pass == null)
            {
                dbContext.PlayerChroniclePasses.Add(new PlayerChroniclePass
                {
                    PlayerId = state.PlayerId,
                    PassLevel = passLevel,
                    AccumulatedXp = seasonalXp,
                    ClaimedMilestonesBitmask = 0UL
                });
                return;
            }

            if (pass.PassLevel < passLevel)
            {
                pass.PassLevel = passLevel;
            }

            if (pass.AccumulatedXp < seasonalXp)
            {
                pass.AccumulatedXp = seasonalXp;
            }
        }

        // Modul 13: auto-awarded tiered (I-IV) achievements, evaluated against
        // the live counters accumulated in TickStatePayload since the last
        // checkpoint. Distinct from the pre-existing player-claimed "kill 10000
        // monsters" achievement (AchievementId 1, handled by
        // AchievementEngine.ProcessClaimsQueueAsync) - these auto-award, no
        // client claim action required.
        private static async Task UpsertLifetimeAchievementsAsync(FolkIdleDbContext dbContext, PlayerRecord player, TickStatePayload state)
        {
            await EvaluateAndAwardTierAsync(dbContext, player, state.PlayerId, AchievementMilestones.TreasuryAchievementId,
                AchievementMilestones.EvaluateTreasuryTier(state.CurrentGold), state.CurrentGold);

            await EvaluateAndAwardTierAsync(dbContext, player, state.PlayerId, AchievementMilestones.ForgingAchievementId,
                AchievementMilestones.EvaluateForgingTier(state.ForgeUpgradeCount, state.HighestForgeSynthesisTier), state.ForgeUpgradeCount);

            await EvaluateAndAwardTierAsync(dbContext, player, state.PlayerId, AchievementMilestones.LogisticsAchievementId,
                AchievementMilestones.EvaluateLogisticsTier(state.HarvestLoopCount), state.HarvestLoopCount);
        }

        private static async Task EvaluateAndAwardTierAsync(FolkIdleDbContext dbContext, PlayerRecord player, long playerId, int achievementId, int newTier, long currentProgress)
        {
            var record = await dbContext.PlayerLifetimeAchievements
                .FromSqlInterpolated($"SELECT * FROM \"player_lifetime_achievements\" WHERE \"PlayerId\" = {playerId} AND \"AchievementId\" = {achievementId} FOR UPDATE")
                .FirstOrDefaultAsync();

            if (record == null)
            {
                record = new PlayerLifetimeAchievement
                {
                    PlayerId = playerId,
                    AchievementId = achievementId,
                    CurrentProgress = 0,
                    CompletedTier = 0,
                    IsClaimed = false
                };
                dbContext.PlayerLifetimeAchievements.Add(record);
            }

            if (newTier > record.CompletedTier)
            {
                int diamondsAwarded = AchievementMilestones.GetDiamondsForTiersCrossed(achievementId, record.CompletedTier, newTier);
                int statBonusAwarded = AchievementMilestones.GetStatBonusForTiersCrossed(achievementId, record.CompletedTier, newTier);
                record.CompletedTier = newTier;
                player.PremiumDiamonds += diamondsAwarded;

                if (achievementId == AchievementMilestones.LogisticsAchievementId && statBonusAwarded > 0)
                {
                    player.LogisticsGatheringSpeedBonusPct += statBonusAwarded;
                }
            }

            record.CurrentProgress = currentProgress;
        }

        public void FlushAllGracefully()
        {
            var states = _dirtyStates.Values.ToList();
            bool committed = FlushBatch(states).GetAwaiter().GetResult();
            if (committed)
            {
                _dirtyStates.Clear();
            }
            else
            {
                // Shutdown-time flush failed even after retries - there is
                // no "next cycle" left to requeue onto since the process is
                // exiting, so this is a genuine, unavoidable loss for this
                // batch. Left in _dirtyStates (not cleared) and logged
                // loudly rather than silently discarded, so this is visible
                // in shutdown logs instead of vanishing the same way the
                // per-tick path used to.
                Console.WriteLine($"FlushAllGracefully: failed to persist {states.Count} dirty player state(s) during shutdown flush.");
            }
        }

        public async Task<bool> FlushBatch(System.Collections.Generic.IEnumerable<TickStatePayload> states)
        {
            var stateList = new System.Collections.Generic.List<TickStatePayload>(states);
            if (stateList.Count == 0)
            {
                return true;
            }

            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var dbContext = new FolkIdleDbContext(retryingOptions.Options);

            var strategy = dbContext.Database.CreateExecutionStrategy();
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    dbContext.ChangeTracker.Clear();

                    using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                    foreach (var state in stateList)
                    {
                        var player = await dbContext.PlayerRecords
                            .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", state.PlayerId)
                            .FirstOrDefaultAsync();

                        if (player == null) continue;

                        // Split-brain sieve on batch: skip divergent records silently (they were handled in single-flush path).
                        if (player.LogicEpochCounter > state.LogicEpochCounter) continue;

                        player.CurrentLevel = state.CurrentLevel;
                        player.CurrentXp = state.CurrentXp;
                        player.SelectedLineageId = state.SelectedLineageId;
                        player.LastLogoutTimestamp = state.LastLogoutTimestamp;
                        player.AccumulatedTimeBankSeconds = (int)(state.AccumulatedTimeBankMs / 1000L);
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.OffensivePotionDurationMs = state.OffensivePotionDurationMs;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.DefensivePotionDurationMs = state.DefensivePotionDurationMs;
                        player.LogicEpochCounter = state.LogicEpochCounter + 1;
                        player.BankedChronoSeconds = state.BankedChronoSeconds;
                        player.IsChronoAccelerating = state.IsChronoAccelerating;
                        player.Quarantine_Active = state.Quarantine_Active;
                        player.IsQuarantined = state.IsQuarantined;
                        player.BaseStrength = state.STR;
                        player.BaseDexterity = state.DEX;
                        player.BaseConstitution = state.CON;
                        player.BaseLuck = state.LCK;
                        // Modul: per-character equipment. The flush used to
                        // mirror the payload's equipped ids back onto
                        // PlayerRecords. Equipment now lives on CharacterRecord
                        // and EquipmentSlotEngine is its only writer, committing
                        // inside its own Serializable transaction - so the
                        // payload's copy is a read-through cache and writing it
                        // back here would let a stale register overwrite a fresh
                        // equip that landed between two checkpoints.
                        long consumableFlushEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        player.ActiveOffensivePotionId = state.ActiveOffensivePotionId;
                        player.ActiveOffensivePotionExpiresEpoch = state.OffensivePotionDurationMs > 0 ? consumableFlushEpoch + state.OffensivePotionDurationMs / 1000L : 0L;
                        player.ActiveDefensivePotionId = state.ActiveDefensivePotionId;
                        player.ActiveDefensivePotionExpiresEpoch = state.DefensivePotionDurationMs > 0 ? consumableFlushEpoch + state.DefensivePotionDurationMs / 1000L : 0L;
                        player.ActiveFoodId = state.ActiveFoodBuffId;
                        player.ActiveFoodExpiresEpoch = state.FoodBuffDurationMs > 0 ? consumableFlushEpoch + state.FoodBuffDurationMs / 1000L : 0L;
                        player.XpPenaltyExpiresEpoch = state.XpPenaltyExpiresEpoch;
                        player.PremiumDiamonds = state.PremiumCurrency;
                        await UpsertAccountChronoRegistryAsync(dbContext, state);
                        await UpsertChroniclePassAsync(dbContext, state);

                        if (state.Slot1_CharacterId != System.Guid.Empty)
                        {
                            var c1 = await dbContext.CharacterRecords.FindAsync(state.Slot1_CharacterId);
                            if (c1 != null) { c1.AgeTicks = state.Slot1_AgeTicks; c1.AgePhase = state.Slot1_AgePhase; }
                        }
                        if (state.Slot2_CharacterId != System.Guid.Empty)
                        {
                            var c2 = await dbContext.CharacterRecords.FindAsync(state.Slot2_CharacterId);
                            if (c2 != null) { c2.AgeTicks = state.Slot2_AgeTicks; c2.AgePhase = state.Slot2_AgePhase; }
                        }
                        if (state.Slot3_CharacterId != System.Guid.Empty)
                        {
                            var c3 = await dbContext.CharacterRecords.FindAsync(state.Slot3_CharacterId);
                            if (c3 != null) { c3.AgeTicks = state.Slot3_AgeTicks; c3.AgePhase = state.Slot3_AgePhase; }
                        }
                    }

                    await dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();
                });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to flush batch: {ex.Message}");
                return false;
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
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
                        player.LogicEpochCounter = state.LogicEpochCounter + 1;
                        player.BankedChronoSeconds = state.BankedChronoSeconds;
                        player.IsChronoAccelerating = state.IsChronoAccelerating;
                        player.Quarantine_Active = state.Quarantine_Active;
                        player.IsQuarantined = state.IsQuarantined;
                        player.BaseStrength = state.STR;
                        player.BaseDexterity = state.DEX;
                        player.BaseConstitution = state.CON;
                        player.BaseLuck = state.LCK;
                        player.EquippedWeaponId = state.EquippedWeaponId == 0L ? null : state.EquippedWeaponId;
                        player.EquippedArmorId = state.EquippedArmorId == 0L ? null : state.EquippedArmorId;
                        player.XpPenaltyExpiresEpoch = state.XpPenaltyExpiresEpoch;
                        player.PremiumDiamonds = state.PremiumCurrency;
                        player.AvailableSkillPoints = state.AvailableSkillPoints;
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

            // Modul 16/21: EquippedWeaponId/ArmorId are persisted, but the
            // derived stat totals StatsCalculator reads every tick are not - they
            // must be recomputed once at login rather than starting zeroed until
            // the player's next equip action.
            (int equippedAttack, int equippedDefense, int equippedCrit, int equippedLuck) =
                await EquipmentSlotEngine.ComputeEquippedTotalsAsync(dbContext, player.EquippedWeaponId, player.EquippedArmorId);

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
                ActiveActivityId = 1,
                CurrentProgressTicks = 0,
                RequiredProgressTicks = 50,
                InventorySpaceRemaining = 20 + RaceMasteryResolver.GetHumanVaultBonusSlots(humanMastery),
                PlayerHp = 100000,
                CurrentGold = 10000,
                PremiumCurrency = player.PremiumDiamonds,
                SpeedMultiplier = chronoAccelerationActive ? (int)accountChrono.ActiveSpeedMultiplier : 1,
                GuildId = player.GuildId,
                ActiveCrossShardMatchId = activeCrossShardMatchId,
                ActiveMatchMmr = activeMatchMmr,
                GlobalNodeRemainingHp = globalNodeRemainingHp,
                CachedMiningMonolithLevel = miningMonolith,
                CachedWoodcuttingMonolithLevel = woodMonolith,
                ActiveOffensivePotionId = player.ActiveOffensivePotionId,
                OffensivePotionDurationMs = player.OffensivePotionDurationMs,
                ActiveDefensivePotionId = player.ActiveDefensivePotionId,
                DefensivePotionDurationMs = player.DefensivePotionDurationMs,
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
                EquippedWeaponId = player.EquippedWeaponId ?? 0L,
                EquippedArmorId = player.EquippedArmorId ?? 0L,
                XpPenaltyExpiresEpoch = player.XpPenaltyExpiresEpoch,
                CachedEquippedFlatAttack = equippedAttack,
                CachedEquippedFlatDefense = equippedDefense,
                CachedEquippedCritBonus = equippedCrit,
                CachedEquippedLuckBonus = equippedLuck,
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

            if (characters.Count > 0)
            {
                payload.Slot1_CharacterId = characters[0].Id;
                payload.Slot1_AgeTicks = characters[0].AgeTicks;
                payload.Slot1_AgePhase = characters[0].AgePhase;
                payload.Slot1_GeneticVector = characters[0].Lineage?.GeneticVector ?? 0;

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
            }
            if (characters.Count > 2)
            {
                payload.Slot3_CharacterId = characters[2].Id;
                payload.Slot3_AgeTicks = characters[2].AgeTicks;
                payload.Slot3_AgePhase = characters[2].AgePhase;
                payload.Slot3_GeneticVector = characters[2].Lineage?.GeneticVector ?? 0;
            }

            return payload;
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
                        player.EquippedWeaponId = state.EquippedWeaponId == 0L ? null : state.EquippedWeaponId;
                        player.EquippedArmorId = state.EquippedArmorId == 0L ? null : state.EquippedArmorId;
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

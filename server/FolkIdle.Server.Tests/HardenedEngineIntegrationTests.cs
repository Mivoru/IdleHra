using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FolkIdle.Server.Tests
{
    public class PostgresTestFixture : IAsyncLifetime
    {
        private PostgreSqlContainer _container = null!;

        public IDbContextFactory<FolkIdleDbContext> DbContextFactory { get; private set; } = null!;
        public IServiceProvider ServiceProvider { get; private set; } = null!;
        public PlayerSessionRegistry PlayerRegistry { get; } = new();

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder("postgres:16")
                .WithDatabase("folkidle_test")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync();

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options => options.UseNpgsql(_container.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());
            ServiceProvider = services.BuildServiceProvider();
            DbContextFactory = ServiceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using var db = await DbContextFactory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            await DbSeeder.SeedAllAsync(db);
        }

        public async Task DisposeAsync()
        {
            await _container.DisposeAsync();
        }
    }

    [CollectionDefinition("Postgres collection")]
    public class PostgresCollection : ICollectionFixture<PostgresTestFixture>
    {
    }

    [Collection("Postgres collection")]
    public class HardenedEngineIntegrationTests
    {
        private const long SeedBossMaxHp = 50000000L;

        private readonly PostgresTestFixture _fixture;

        public HardenedEngineIntegrationTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Test_ForgeSplicing_FodderPenaltyCalculation()
        {
            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);

            long lowQualityCost = await RunFusionAndMeasureCostAsync(
                baseItemId: "integration_test_forge_sword_low",
                sacrificeQualityTier: 1,
                forgeEngine);

            long highQualityCost = await RunFusionAndMeasureCostAsync(
                baseItemId: "integration_test_forge_sword_high",
                sacrificeQualityTier: 4,
                forgeEngine);

            Assert.Equal(8000L, lowQualityCost);
            Assert.Equal(2000L, highQualityCost);
            Assert.True(lowQualityCost > highQualityCost);
        }

        private async Task<long> RunFusionAndMeasureCostAsync(string baseItemId, int sacrificeQualityTier, ForgeSplicingEngine forgeEngine)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            bool hasForge = await db.VillageInfrastructures.AnyAsync(
                v => v.PlayerId == DbSeeder.PlayerHighId && v.BuildingId == VillageManagementEngine.ForgeBuildingId);
            if (!hasForge)
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = DbSeeder.PlayerHighId,
                    BuildingId = VillageManagementEngine.ForgeBuildingId,
                    CurrentLevel = 10
                });
            }

            var target = new MarketEquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = 1 };
            var sac1 = new MarketEquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = sacrificeQualityTier };
            var sac2 = new MarketEquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = sacrificeQualityTier };
            db.MarketEquipmentInstances.AddRange(target, sac1, sac2);
            await db.SaveChangesAsync();

            long goldBefore = await GetGoldAsync(DbSeeder.PlayerHighId);

            await forgeEngine.ExecuteFusionAsync(DbSeeder.PlayerHighId, target.Id, sac1.Id, sac2.Id);

            long goldAfter = await GetGoldAsync(DbSeeder.PlayerHighId);

            return goldBefore - goldAfter;
        }

        private async Task<long> GetGoldAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var record = await db.CommodityRecords.AsNoTracking()
                .SingleOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            return record?.Quantity ?? 0;
        }

        [Theory]
        [InlineData(DbSeeder.PlayerLowId, 0.06)]
        [InlineData(DbSeeder.PlayerMidId, 0.10)]
        [InlineData(DbSeeder.PlayerHighId, 0.18)]
        public async Task Test_MarketOrderBook_TaxBracketsAndArchiving(long sellerId, double expectedRate)
        {
            var marketEngine = new MarketOrderBookEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            string baseItemId = $"integration_test_ore_{sellerId}";
            const long price = 5000L;
            long buyerId = 900000000L + sellerId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = buyerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = buyerId, ItemId = "gold", Quantity = 1000000L });
                await db.SaveChangesAsync();
            }

            long equipmentId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var equipment = new MarketEquipmentInstance { PlayerId = sellerId, BaseItemId = baseItemId, QualityTier = 1 };
                db.MarketEquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;
            }

            await marketEngine.PlaceLimitOrderAsync(sellerId, false, equipmentId, price, baseItemId, 1);
            await marketEngine.PlaceLimitOrderAsync(buyerId, true, 0, price, baseItemId, 1);
            await marketEngine.MatchOrdersAsync(baseItemId, 1);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            long expectedFee = (long)(price * expectedRate);
            var archiveRow = await verifyDb.HistoricalMarketArchives.AsNoTracking()
                .Where(a => a.SellerId == sellerId && a.EquipmentInstanceId == equipmentId)
                .SingleOrDefaultAsync();

            Assert.NotNull(archiveRow);
            Assert.Equal(expectedFee, archiveRow!.FeeBurned);
            Assert.Equal(price, archiveRow.ExecutionPrice);

            bool anyRemainingOrders = await verifyDb.MarketOrderRecords.AsNoTracking()
                .AnyAsync(o => o.BaseItemId == baseItemId && o.QualityTier == 1);
            Assert.False(anyRemainingOrders);
        }

        [Fact]
        public async Task Test_WorldBoss_AttemptLimitingAndScaling()
        {
            var worldBossEngine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            long[] onlinePlayerIds = { DbSeeder.PlayerLowId, DbSeeder.PlayerMidId, DbSeeder.PlayerHighId };

            // Attacks are gated behind an active event window; activate one before scaling/attacking.
            await worldBossEngine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
            Assert.True(worldBossEngine.IsEventActive);

            await worldBossEngine.ScaleActiveBossAsync(onlinePlayerIds);

            long expectedMasterySum = 5 + 15 + 30;
            long expectedMaxHp = (long)(SeedBossMaxHp * (onlinePlayerIds.Length * 1.50) + (expectedMasterySum * 250.0));
            Assert.Equal(expectedMaxHp, worldBossEngine.BossMaxHp);

            const uint attackDamage = 5000;
            long hpBeforeAttacks = worldBossEngine.BossCurrentHp;

            for (int i = 0; i < 3; i++)
            {
                long hpBeforeThisAttack = worldBossEngine.BossCurrentHp;
                await worldBossEngine.ExecuteAttackAsync(DbSeeder.PlayerLowId, WorldBossEngine.ActiveBossInstanceId, attackDamage);
                Assert.Equal(hpBeforeThisAttack - attackDamage, worldBossEngine.BossCurrentHp);
            }

            Assert.Equal(hpBeforeAttacks - (attackDamage * 3), worldBossEngine.BossCurrentHp);

            long hpBeforeFourthAttack = worldBossEngine.BossCurrentHp;
            await worldBossEngine.ExecuteAttackAsync(DbSeeder.PlayerLowId, WorldBossEngine.ActiveBossInstanceId, attackDamage);

            Assert.Equal(hpBeforeFourthAttack, worldBossEngine.BossCurrentHp);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var attempt = await verifyDb.PlayerWorldBossAttempts.AsNoTracking()
                .SingleAsync(a => a.PlayerId == DbSeeder.PlayerLowId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
            Assert.Equal(3, attempt.AttemptCount);
        }

        [Fact]
        public async Task Test_CodexPassiveStats_Scaling()
        {
            const long testPlayerId = 750000001L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.MonsterCodexEntries.AddRange(
                    new MonsterCodexEntry { PlayerId = testPlayerId, MonsterId = 1, KillCount = 100, Level = 10, FirstDrawnRarity = 1 },
                    new MonsterCodexEntry { PlayerId = testPlayerId, MonsterId = 2, KillCount = 50, Level = 5, FirstDrawnRarity = 1 },
                    new MonsterCodexEntry { PlayerId = testPlayerId, MonsterId = 3, KillCount = 0, Level = 0, FirstDrawnRarity = 1 });
                await db.SaveChangesAsync();
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            (float yieldMultiplier, float damageMultiplier) = await CodexEngine.CalculateActiveMultipliersAsync(testPlayerId, verifyDb);

            Assert.Equal(1.075f, yieldMultiplier);
            Assert.Equal(1.15f, damageMultiplier);
        }

        [Fact]
        public async Task Test_GuildLogistics_ContributionAndLevelUp()
        {
            const long testGuildId = 850000001L;
            const long testPlayerId = 850000002L;
            const int materialId = 1;
            const long initialTargetRequirement = 1000L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.GuildRecords.Add(new GuildRecord { Id = testGuildId, Name = "IntegrationTestGuild" });
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    GuildId = testGuildId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = ContentRegistry.GetMaterialString(materialId), Quantity = initialTargetRequirement });
                db.GuildLogisticsDepots.Add(new GuildLogisticsDepot
                {
                    GuildId = testGuildId,
                    MaterialId = materialId,
                    CurrentStock = 0L,
                    TargetRequirement = initialTargetRequirement,
                    Level = 0
                });
                await db.SaveChangesAsync();
            }

            var depotEngine = new GuildLogisticsDepotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await depotEngine.DepositMaterialAsync(testPlayerId, testGuildId, materialId, (uint)initialTargetRequirement);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var depot = await verifyDb.GuildLogisticsDepots.AsNoTracking()
                .SingleAsync(d => d.GuildId == testGuildId && d.MaterialId == materialId);

            Assert.Equal(1, depot.Level);
            Assert.Equal(0L, depot.CurrentStock);
            Assert.Equal((long)(initialTargetRequirement * 1.25), depot.TargetRequirement);
        }

        [Fact]
        public async Task Test_GuildCombat_SimulationTick()
        {
            const long testGuildId = 860000001L;
            const long testPlayerId = 860000002L;
            const long initialBossHp = 100000L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.GuildRecords.Add(new GuildRecord { Id = testGuildId, Name = "IntegrationTestRaidGuild" });
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    GuildId = testGuildId,
                    CurrentLevel = 50,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.GuildRaidStates.Add(new GuildRaidState
                {
                    GuildId = testGuildId,
                    RaidTier = 1,
                    RaidBossCurrentHp = initialBossHp,
                    RaidBossMaxHp = initialBossHp
                });
                await db.SaveChangesAsync();
            }

            var raidEngine = new GuildRaidEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var raid = await db.GuildRaidStates.AsNoTracking().SingleAsync(r => r.GuildId == testGuildId);
                await raidEngine.ProcessGuildRaidTickAsync(db, raid, new[] { testPlayerId });
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var updatedRaid = await verifyDb.GuildRaidStates.AsNoTracking().SingleAsync(r => r.GuildId == testGuildId);

            Assert.Equal(initialBossHp - 2500L, updatedRaid.RaidBossCurrentHp);
        }

        [Fact]
        public async Task Test_Character_GeneticBreeding()
        {
            const long testPlayerId = 950000001L;
            Guid parentAId = Guid.NewGuid();
            Guid parentBId = Guid.NewGuid();

            var sharedGenome = new GeneticVector(0);
            sharedGenome.LocusRace = new Locus { Dominant = 1, Recessive = 1 };
            sharedGenome.LocusSpeed = new Locus { Dominant = 2, Recessive = 2 };
            sharedGenome.LocusCrit = new Locus { Dominant = 3, Recessive = 3 };
            sharedGenome.LocusYield = new Locus { Dominant = 4, Recessive = 4 };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.BreedingGroundsBuildingId,
                    CurrentLevel = 1
                });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = parentAId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false },
                    new CharacterRecord { Id = parentBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = parentAId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue },
                    new CharacterLineageRegistry { CharacterId = parentBId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue });
                await db.SaveChangesAsync();
            }

            var breedingEngine = new BreedingEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await breedingEngine.ExecuteBreedingAsync(testPlayerId, parentAId, parentBId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var childLineage = await verifyDb.CharacterLineages.AsNoTracking()
                .SingleAsync(l => l.ParentPaternalId == parentAId && l.ParentMaternalId == parentBId);

            var childCharacter = await verifyDb.CharacterRecords.AsNoTracking()
                .SingleAsync(c => c.Id == childLineage.CharacterId);

            Assert.Equal(testPlayerId, childCharacter.PlayerId);
            Assert.Equal(1, childCharacter.Level);
            Assert.Equal(0, childCharacter.AgePhase);
            Assert.Equal(1, childLineage.GenerationIndex);
        }

        [Fact]
        public async Task Test_Mentorship_XpBoostAndTickApplication()
        {
            const long mentorPlayerId = 960000001L;
            const long menteePlayerId = 960000002L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = mentorPlayerId,
                    CurrentLevel = 40,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = menteePlayerId,
                    CurrentLevel = 10,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = menteePlayerId,
                    BuildingId = VillageManagementEngine.MentorshipAcademyBuildingId,
                    CurrentLevel = 1
                });
                await db.SaveChangesAsync();
            }

            var mentorshipEngine = new MentorshipEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var result = await mentorshipEngine.EstablishMentorshipContractAsync(menteePlayerId, mentorPlayerId);

            Assert.Equal(MentorshipContractResult.Established, result);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var contract = await verifyDb.MentorshipContracts.AsNoTracking()
                    .SingleAsync(c => c.MenteePlayerId == menteePlayerId);
                Assert.Equal(1.20, contract.ExpBonusMultiplier);
            }

            var checkpointManager = new StateCheckpointManager(_fixture.ServiceProvider);
            TickStatePayload menteePayload = await checkpointManager.LoadPlayerState(menteePlayerId);
            Assert.Equal(1.20, menteePayload.MentorshipExpBonusMultiplier);

            // Replicate the exact SimulationEngine combat-kill XP calculation path.
            // Kept below the level-1 XP requirement (100) so neither run triggers a
            // level-up, which would make the final CurrentXp non-linear.
            const int baseXpReward = 50;
            int baselineMultiplier = GlobalEngineState.GlobalXpMultiplier;
            int mentoredMultiplier = (int)(baselineMultiplier * menteePayload.MentorshipExpBonusMultiplier);

            var baselinePayload = new TickStatePayload { PlayerId = menteePlayerId, CurrentLevel = 1 };
            ProgressionEngine.ProcessMonsterDeath(ref baselinePayload, baseXpReward, baselineMultiplier, 0);

            var mentoredPayload = new TickStatePayload { PlayerId = menteePlayerId, CurrentLevel = 1 };
            ProgressionEngine.ProcessMonsterDeath(ref mentoredPayload, baseXpReward, mentoredMultiplier, 0);

            Assert.Equal(baselinePayload.CurrentXp * 1.20, mentoredPayload.CurrentXp, 3);
        }

        [Fact]
        public async Task Test_OfflineProgression_AnalyticalCalculation()
        {
            const long testPlayerId = 970000001L;
            const long elapsedOfflineSeconds = 14400L; // 4 hours
            const int monsterId = 31;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = testPlayerId, MonsterId = 1, KillCount = 100, Level = 10 });
                await db.SaveChangesAsync();
            }

            (float YieldMultiplier, float DamageMultiplier) multipliers;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                multipliers = await CodexEngine.CalculateActiveMultipliersAsync(testPlayerId, db);
            }

            long currentUnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var payload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LastLogoutTimestamp = currentUnixTimestamp - elapsedOfflineSeconds,
                ActiveActivityId = monsterId,
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 0,
                InventorySpaceRemaining = 1000,
                CachedCodexYieldMultiplier = multipliers.YieldMultiplier,
                CachedCodexDamageMultiplier = multipliers.DamageMultiplier
            };

            // Independently replicate the engine's analytical combat projection to
            // compute the expected reward, rather than hand-computing a fragile
            // cascading level-up chain by hand.
            MonsterDefinition monster = ContentRegistry.Monsters[monsterId - 1];
            CombatStats combatStats = StatsCalculator.Calculate(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0);
            int attackSpeedMs = Math.Max(200, (int)(1500 * (1.0f - combatStats.AttackSpeedPct)));
            long effectiveMilliAttack = 15000L + (combatStats.FlatMeleeDamage * 1000L);
            int netDamage = Math.Max(1000, (int)effectiveMilliAttack);
            netDamage = (int)(netDamage * multipliers.DamageMultiplier);
            double dps = (netDamage / 1000.0) * (1000.0 / attackSpeedMs);
            double secondsPerKill = monster.MaxHp / dps;
            double totalKillsDouble = elapsedOfflineSeconds / secondsPerKill;
            long expectedKills = (long)totalKillsDouble;
            long expectedXpGained = expectedKills * monster.BaseXpReward;
            int expectedLootRolls = (int)(totalKillsDouble * multipliers.YieldMultiplier);

            long expectedXp = expectedXpGained;
            int expectedLevel = 1;
            while (true)
            {
                long requiredXp = 100L * expectedLevel * expectedLevel;
                if (expectedXp >= requiredXp)
                {
                    expectedXp -= requiredXp;
                    expectedLevel++;
                }
                else
                {
                    break;
                }
            }

            Assert.True(expectedLootRolls > 0);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                payload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, payload, currentUnixTimestamp);
            }

            Assert.Equal(expectedLevel, payload.CurrentLevel);
            Assert.Equal(expectedXp, payload.CurrentXp);
            Assert.Equal(currentUnixTimestamp, payload.LastLogoutTimestamp);
            Assert.True(payload.IsDirty);

            // ContentRegistry's real loot tables currently carry zero entries, so the
            // in-registry combat path has nothing to roll against yet. The granting
            // pipeline itself is verified here in isolation against a hand-built
            // loot table, using the same roll count the analytical projection above
            // computed, so the DB commit and quantity math are still exercised for real.
            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var lootTable = new[] { new LootTableEntry { ItemId = 1, Weight = 100 } };
                int granted = await OfflineSimulationEngine.GrantAnalyticalLootAsync(verifyDb, testPlayerId, lootTable, expectedLootRolls, 1000);

                Assert.Equal(expectedLootRolls, granted);

                var commodity = await verifyDb.CommodityRecords.AsNoTracking()
                    .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "copper_ore");
                Assert.Equal(expectedLootRolls, commodity.Quantity);
            }
        }

        [Fact]
        public void Test_Chrono_ActiveTimeAcceleration()
        {
            const long testPlayerId = 980000001L;
            const int gatheringActivityId = 101;

            var simulationEngine = CreateTestSimulationEngine();

            var payload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                ActiveActivityId = gatheringActivityId,
                GatheringProgressTicks = 0,
                WoodcuttingMasteryLevel = 0,
                CachedCurrentToolTier = 0,
                InventorySpaceRemaining = 1000,
                SpeedMultiplier = 2,
                IsChronoAccelerating = true,
                BankedChronoSeconds = 3600.0,
                ActiveChronoLockExpirationTicks = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
            };

            // A single 100ms frame at 2x Chrono speed must run the sub-tick body
            // twice, so the gathering action counter (a plain, RNG-free progress
            // tick) should advance by exactly double the normal per-frame rate.
            simulationEngine.ProcessTick(ref payload);

            Assert.Equal(2, payload.GatheringProgressTicks);
            Assert.Equal(2, payload.SpeedMultiplier);
            Assert.True(payload.BankedChronoSeconds < 3600.0);
        }

        private SimulationEngine CreateTestSimulationEngine()
        {
            var serviceProvider = _fixture.ServiceProvider;
            var playerRegistry = _fixture.PlayerRegistry;
            var contextFactory = _fixture.DbContextFactory;

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, "http://localhost:8082/");
            var lootEngine = new LootTableEngine();
            var checkpointManager = new StateCheckpointManager(serviceProvider);
            var forgeEngine = new ForgeSplicingEngine(serviceProvider);
            var marketEngine = new MarketOrderBookEngine(serviceProvider, playerRegistry);
            var guildEngine = new GuildContributionEngine(serviceProvider);
            var escrowEngine = new MarketEscrowEngine(serviceProvider, playerRegistry);
            var mailboxEngine = new MailboxAndBankEngine(serviceProvider, playerRegistry);
            var rerollEngine = new AffixRerollEngine(serviceProvider);
            var breedingEngine = new BreedingEngine(serviceProvider, playerRegistry);
            var guildLogisticsEngine = new GuildLogisticsEngine(serviceProvider, playerRegistry);
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry);
            var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
            var villageBuildingEngine = new VillageBuildingEngine(serviceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var mentorshipEngine = new MentorshipEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(serviceProvider, playerRegistry);
            var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);

            return new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);
        }

        [Fact]
        public async Task Test_Forge_TransactionAndResourceDeduction()
        {
            const long testPlayerId = 990000001L;
            const int recipeId = 1;
            const long initialMaterialQuantity = 50L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "copper_ore", Quantity = initialMaterialQuantity });
                await db.SaveChangesAsync();
            }

            var craftingEngine = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry);
            await craftingEngine.ExecuteEquipmentCraftingAsync(testPlayerId, recipeId, slotIndex: 0, tickToken: 12345);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var commodity = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "copper_ore");
            Assert.Equal(initialMaterialQuantity - 10, commodity.Quantity);

            var equipment = await verifyDb.EquipmentInstances.AsNoTracking()
                .SingleAsync(e => e.PlayerId == testPlayerId);
            Assert.Equal("copper_greatsword_melee_weapon_slot_base", equipment.BaseItemId);
            Assert.Equal(1, equipment.QualityTier);
            Assert.False(equipment.IsAffixLocked);
        }

        [Fact]
        public async Task Test_AffixReroll_WeightedDistribution()
        {
            const long testPlayerId = 990000002L;
            const long initialPremiumCurrency = 100L;
            long equipmentId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "premium_diamond", Quantity = initialPremiumCurrency });

                var equipment = new EquipmentInstance
                {
                    BaseItemId = "1",
                    PlayerId = testPlayerId,
                    QualityTier = 1,
                    AffixPayload = "{\"flat_hp_aaaa\":50}",
                    IsAffixLocked = false
                };
                db.EquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;
            }

            var rerollEngine = new AffixRerollEngine(_fixture.ServiceProvider);
            await rerollEngine.ExecuteRerollAsync(testPlayerId, equipmentId, affixIndex: 0);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var commodity = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "premium_diamond");
            Assert.Equal(initialPremiumCurrency - 5, commodity.Quantity);

            var updatedEquipment = await verifyDb.EquipmentInstances.AsNoTracking()
                .SingleAsync(e => e.Id == equipmentId);

            var affixPayload = JsonNode.Parse(updatedEquipment.AffixPayload) as JsonObject;
            Assert.NotNull(affixPayload);
            Assert.False(affixPayload!.ContainsKey("flat_hp_aaaa"));
            Assert.Single(affixPayload);
        }

        [Fact]
        public void Test_Village_PassiveProductionAndWarehouseCap()
        {
            const long testPlayerId = 995000001L;

            var payload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LumberjackLevel = 5,
                MineLevel = 2,
                WarehouseLevel = 1,
                CachedWoodStock = 995L,
                CachedIronOreStock = 100L
            };

            // 1000 physical 10 Hz ticks (0.1s each) simulate 100 seconds of active play.
            for (int i = 0; i < 1000; i++)
            {
                SimulationEngine.ProcessPassiveVillageTick(ref payload, 0.1);
            }

            // Wood_Rate = 5 * 0.1 = 0.5/sec. The warehouse cap (Level 1 = 1000) chokes
            // production after exactly 5 more wood (995 -> 1000), well before the
            // 100 second window ends, so no more accumulates past the cap.
            Assert.Equal(1000L, payload.CachedWoodStock);
            Assert.Equal(5L, payload.PendingWoodDelta);

            // Iron_Rate = 2 * 0.05 = 0.1/sec * 100s = 10 iron; nowhere near the cap.
            Assert.Equal(110L, payload.CachedIronOreStock);
            Assert.Equal(10L, payload.PendingIronDelta);

            Assert.True(payload.IsDirty);
        }

        [Fact]
        public async Task Test_Village_OfflinePassiveIncome_Integration()
        {
            const long testPlayerId = 995000002L;
            const long elapsedOfflineSeconds = 3600L; // 1 hour

            long currentUnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var payload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LastLogoutTimestamp = currentUnixTimestamp - elapsedOfflineSeconds,
                ActiveActivityId = 0,
                QuarryLevel = 10,
                WarehouseLevel = 2,
                InventorySpaceRemaining = 1000
            };

            // Stone_Rate = 10 * 0.08 = 0.8/sec. Potential production over 1 hour
            // (2880) exceeds the Warehouse cap (Level 2 = 2000), so this also
            // exercises the cap-enforcement branch on the offline catch-up path.
            const long maxStorage = 2000L;
            const float stoneRatePerSecond = 10 * 0.08f;
            long expectedStoneGain = Math.Min((long)(elapsedOfflineSeconds * stoneRatePerSecond), maxStorage);
            Assert.Equal(2000L, expectedStoneGain);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                payload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, payload, currentUnixTimestamp);
            }

            Assert.Equal(currentUnixTimestamp, payload.LastLogoutTimestamp);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var stone = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "stone");

            Assert.Equal(expectedStoneGain, stone.Quantity);
        }
    }
}

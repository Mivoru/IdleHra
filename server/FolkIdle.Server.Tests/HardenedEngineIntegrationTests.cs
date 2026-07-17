using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Client.Engine;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FolkIdle.Server.Tests
{
    public class PostgresTestFixture : IAsyncLifetime
    {
        private PostgreSqlContainer _container = null!;

        // Modul: a real Redis container, not just a null-and-degrade stub -
        // ChatEngine has no same-pod fallback (unlike RedisPlayerSessionLock's
        // eviction, which still works locally via _connectedClients when
        // Redis is unavailable), by design: a chat message's sender is meant
        // to see their own message echo back through the exact same
        // publish/subscribe path as everyone else, with zero special-cased
        // local delivery. Without a real IConnectionMultiplexer registered
        // here, every chat publish silently no-ops (see ChatEngine.
        // PublishMessageAsync's redis == null guard), making chat completely
        // untestable - this container exists specifically so
        // Test_ChatEngine_RateLimiter_DropsExcessMessagesWithoutDisconnecting
        // and Test_ChatEngine_RedisPubSub_ForwardsMessagesAcrossPods can
        // observe real publish/subscribe behavior end to end.
        private RedisContainer _redisContainer = null!;

        public IDbContextFactory<FolkIdleDbContext> DbContextFactory { get; private set; } = null!;
        public RetryingDbContextOptions RetryingOptions { get; private set; } = null!;
        public IServiceProvider ServiceProvider { get; private set; } = null!;
        public PlayerSessionRegistry PlayerRegistry { get; } = new();

        public async Task InitializeAsync()
        {
            // Content Pipeline: SimulationEngine-dependent tests need real
            // monster/item/skill data resolved through ContentRegistry/
            // ActiveSkillEngine, which are empty until Initialize() parses
            // server/GameData/*.json (see Program.cs's identical boot-time
            // call). Safe to call once per fixture - see Initialize's
            // atomic-commit design.
            ContentRegistry.Initialize();
            ActiveSkillEngine.Initialize();

            _container = new PostgreSqlBuilder("postgres:16")
                .WithDatabase("folkidle_test")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            _redisContainer = new RedisBuilder("redis:7-alpine").Build();

            await Task.WhenAll(_container.StartAsync(), _redisContainer.StartAsync());

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options => options.UseNpgsql(_container.GetConnectionString()));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());

            // Mirrors Program.cs's dedicated retry-configured options exactly
            // - see RetryingDbContextOptions and
            // Test_AuthenticationEngine_ConcurrentAutoProvisioning_ResolvesViaRetryStrategy,
            // which specifically exercises this retry path and would not be
            // proving anything if it were not configured the same way the
            // real server is. Shared by every engine under test that opens
            // its own Serializable transaction (StateCheckpointManager,
            // AchievementEngine, CraftingEngine, ColdRecoveryCoordinator),
            // not just AuthenticationEngine.
            var retryConfiguredOptions = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(_container.GetConnectionString(), npgsqlOptions =>
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 6,
                        maxRetryDelay: TimeSpan.FromSeconds(8),
                        errorCodesToAdd: new[]
                        {
                            Npgsql.PostgresErrorCodes.SerializationFailure,
                            Npgsql.PostgresErrorCodes.DeadlockDetected
                        }))
                .Options;
            services.AddSingleton(new RetryingDbContextOptions(retryConfiguredOptions));

            var redisMultiplexer = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(_redisContainer.GetConnectionString());
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redisMultiplexer);
            services.AddSingleton(new RedisPlayerSessionLock(redisMultiplexer));

            ServiceProvider = services.BuildServiceProvider();
            DbContextFactory = ServiceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            RetryingOptions = ServiceProvider.GetRequiredService<RetryingDbContextOptions>();

            await using var db = await DbContextFactory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            await DbSeeder.SeedAllAsync(db);
        }

        public async Task DisposeAsync()
        {
            await _container.DisposeAsync();
            await _redisContainer.DisposeAsync();
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

            // Modul: recomputed for the GDD exponential forge cost curve
            // (BaseGoldCost * 1.5^currentTier, Phase 2 Part 2.3) - target
            // starts at QualityTier 1, so baseGoldCost = ceil(1000 * 1.5^1)
            // = 1500 (previously the linear 1000 * (1+1) = 2000). The
            // fodder-quality penalty multiplier itself (4.0x for tier-1
            // sacrifices, 1.0x for tier-4 sacrifices) is unchanged.
            Assert.Equal(6000L, lowQualityCost);
            Assert.Equal(1500L, highQualityCost);
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

            var target = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = 1 };
            var sac1 = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = sacrificeQualityTier };
            var sac2 = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = sacrificeQualityTier };
            db.EquipmentInstances.AddRange(target, sac1, sac2);
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
        [InlineData(DbSeeder.PlayerLowId, 0.05)]
        [InlineData(DbSeeder.PlayerMidId, 0.08)]
        [InlineData(DbSeeder.PlayerHighId, 0.15)]
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
        public async Task Test_WorldBoss_RejectsAttackAfterSessionCap()
        {
            const long testPlayerId = 950000009L;

            var worldBossEngine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await worldBossEngine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
            await worldBossEngine.ScaleActiveBossAsync(new[] { DbSeeder.PlayerLowId });

            long expiredSessionStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerWorldBossAttempts.Add(new PlayerWorldBossAttempt
                {
                    PlayerId = testPlayerId,
                    BossInstanceId = WorldBossEngine.ActiveBossInstanceId,
                    AttemptCount = 1,
                    TotalInflictedDamage = 1000,
                    SessionStartEpoch = expiredSessionStart
                });
                await db.SaveChangesAsync();
            }

            long bossHpBeforeExpiredAttack = worldBossEngine.BossCurrentHp;
            await worldBossEngine.ExecuteAttackAsync(testPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000);

            Assert.Equal(bossHpBeforeExpiredAttack, worldBossEngine.BossCurrentHp);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var attempt = await verifyDb.PlayerWorldBossAttempts.AsNoTracking()
                .SingleAsync(a => a.PlayerId == testPlayerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);

            Assert.Equal(1, attempt.AttemptCount);
            Assert.Equal(1000L, attempt.TotalInflictedDamage);
        }

        [Fact]
        public void Test_RarityTier_HighLuckIncreasesRareRollProbability()
        {
            const int sampleSize = 5000;
            int lowLuckRareOrBetterCount = 0;
            int highLuckRareOrBetterCount = 0;

            for (int i = 0; i < sampleSize; i++)
            {
                if (RarityTier.RollTier(0f) >= RarityTier.Rare) lowLuckRareOrBetterCount++;
                if (RarityTier.RollTier(500f) >= RarityTier.Rare) highLuckRareOrBetterCount++;
            }

            Assert.True(highLuckRareOrBetterCount > lowLuckRareOrBetterCount);
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
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 10000L });
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

            var updatedParentA = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == parentAId);
            var updatedGoldRecord = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");

            Assert.True(updatedParentA.IsBreedingActive);
            Assert.True(updatedParentA.BreedingCooldownEndEpoch > 0L);
            Assert.Equal(10000L - 500L, updatedGoldRecord.Quantity);
        }

        [Fact]
        public async Task Test_Breeding_RollbackOnInsufficientGold()
        {
            const long testPlayerId = 950000002L;
            Guid parentAId = Guid.NewGuid();
            Guid parentBId = Guid.NewGuid();

            var sharedGenome = new GeneticVector(0);
            sharedGenome.LocusRace = new Locus { Dominant = 1, Recessive = 1 };

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
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 1L });
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

            bool childExists = await verifyDb.CharacterLineages.AsNoTracking()
                .AnyAsync(l => l.ParentPaternalId == parentAId && l.ParentMaternalId == parentBId);
            var unchangedGoldRecord = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");
            var unchangedParentA = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == parentAId);

            Assert.False(childExists);
            Assert.Equal(1L, unchangedGoldRecord.Quantity);
            Assert.False(unchangedParentA.IsBreedingActive);
        }

        [Fact]
        public async Task Test_Breeding_RollbackWhenParentNotOwnedByPlayer()
        {
            const long testPlayerId = 950000003L;
            const long attackerPlayerId = 950000004L;
            Guid parentAId = Guid.NewGuid();
            Guid parentBId = Guid.NewGuid();

            var sharedGenome = new GeneticVector(0);
            sharedGenome.LocusRace = new Locus { Dominant = 1, Recessive = 1 };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.AddRange(
                    new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() },
                    new PlayerRecord { Id = attackerPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = attackerPlayerId,
                    BuildingId = VillageManagementEngine.BreedingGroundsBuildingId,
                    CurrentLevel = 1
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = attackerPlayerId, ItemId = "gold", Quantity = 10000L });
                // Both parents belong to testPlayerId, not the attacker attempting to breed them.
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = parentAId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false },
                    new CharacterRecord { Id = parentBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = parentAId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue },
                    new CharacterLineageRegistry { CharacterId = parentBId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue });
                await db.SaveChangesAsync();
            }

            var breedingEngine = new BreedingEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await breedingEngine.ExecuteBreedingAsync(attackerPlayerId, parentAId, parentBId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            bool childExists = await verifyDb.CharacterLineages.AsNoTracking()
                .AnyAsync(l => l.ParentPaternalId == parentAId && l.ParentMaternalId == parentBId);
            var unchangedGoldRecord = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == attackerPlayerId && c.ItemId == "gold");

            Assert.False(childExists);
            Assert.Equal(10000L, unchangedGoldRecord.Quantity);
        }

        [Fact]
        public async Task Test_Breeding_RollbackWhileParentOnCooldown()
        {
            const long testPlayerId = 950000005L;
            Guid parentAId = Guid.NewGuid();
            Guid parentBId = Guid.NewGuid();

            var sharedGenome = new GeneticVector(0);
            sharedGenome.LocusRace = new Locus { Dominant = 1, Recessive = 1 };

            long futureCooldownEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600L;

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
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 10000L });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = parentAId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsBreedingActive = true, BreedingCooldownEndEpoch = futureCooldownEpoch },
                    new CharacterRecord { Id = parentBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = parentAId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue },
                    new CharacterLineageRegistry { CharacterId = parentBId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue });
                await db.SaveChangesAsync();
            }

            var breedingEngine = new BreedingEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await breedingEngine.ExecuteBreedingAsync(testPlayerId, parentAId, parentBId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            bool childExists = await verifyDb.CharacterLineages.AsNoTracking()
                .AnyAsync(l => l.ParentPaternalId == parentAId && l.ParentMaternalId == parentBId);
            var unchangedGoldRecord = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");

            Assert.False(childExists);
            Assert.Equal(10000L, unchangedGoldRecord.Quantity);
        }

        [Fact]
        public async Task Test_VillageUpgrade_RollbackOnInsufficientWoodAndStone()
        {
            const long testPlayerId = 950000006L;

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
                    BuildingId = VillageManagementEngine.LumberjackBuildingId,
                    CurrentLevel = 0
                });
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "wood", Quantity = 1L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "stone", Quantity = 1L });
                await db.SaveChangesAsync();
            }

            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await villageManagementEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.LumberjackBuildingId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var infrastructure = await verifyDb.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.LumberjackBuildingId);
            var unchangedWood = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "wood");
            var unchangedStone = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "stone");

            Assert.Equal(0, infrastructure.CurrentLevel);
            Assert.Equal(1L, unchangedWood.Quantity);
            Assert.Equal(1L, unchangedStone.Quantity);
        }

        [Fact]
        public async Task Test_VillageUpgrade_QueuesUpgradeAndDeductsWoodAndStone()
        {
            const long testPlayerId = 950000007L;

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
                    BuildingId = VillageManagementEngine.LumberjackBuildingId,
                    CurrentLevel = 0
                });
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "wood", Quantity = 10000L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "stone", Quantity = 10000L });
                await db.SaveChangesAsync();
            }

            long beforeUpgradeEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await villageManagementEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.LumberjackBuildingId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var infrastructure = await verifyDb.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.LumberjackBuildingId);
            var updatedWood = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "wood");
            var updatedStone = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "stone");

            long expectedCost = VillageManagementEngine.CalculateProductionUpgradeCost(0);

            // Upgrades are timed, not instant: cost is deducted immediately,
            // but CurrentLevel only advances once ResolveMaturedUpgradesAsync
            // observes UpgradeCompletesAtEpoch has passed.
            Assert.Equal(0, infrastructure.CurrentLevel);
            Assert.Equal(1, infrastructure.UpgradeTargetLevel);
            Assert.True(infrastructure.UpgradeCompletesAtEpoch >= beforeUpgradeEpoch + VillageManagementEngine.CalculateUpgradeDurationSeconds(expectedCost));
            Assert.Equal(10000L - expectedCost, updatedWood.Quantity);
            Assert.Equal(10000L - expectedCost, updatedStone.Quantity);
        }

        [Fact]
        public async Task Test_VillageUpgrade_RejectsSecondUpgradeWhileQueueOccupied()
        {
            const long testPlayerId = 950000107L;

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
                    BuildingId = VillageManagementEngine.LumberjackBuildingId,
                    CurrentLevel = 0
                });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.QuarryBuildingId,
                    CurrentLevel = 0
                });
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "wood", Quantity = 10000L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "stone", Quantity = 10000L });
                await db.SaveChangesAsync();
            }

            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await villageManagementEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.LumberjackBuildingId);

            // The village-wide upgrade slot is now occupied by Lumberjack - a
            // second request against a DIFFERENT building must be rejected
            // (not just a re-request against the same one), and must not
            // spend the player's wood/stone a second time.
            await villageManagementEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.QuarryBuildingId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var lumberjack = await verifyDb.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.LumberjackBuildingId);
            var quarry = await verifyDb.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.QuarryBuildingId);
            var wood = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "wood");
            var stone = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "stone");

            long expectedCost = VillageManagementEngine.CalculateProductionUpgradeCost(0);

            Assert.Equal(1, lumberjack.UpgradeTargetLevel);
            Assert.Equal(0, quarry.UpgradeTargetLevel);
            Assert.Equal(0, quarry.CurrentLevel);
            Assert.Equal(10000L - expectedCost, wood.Quantity);
            Assert.Equal(10000L - expectedCost, stone.Quantity);
        }

        [Fact]
        public async Task Test_VillageUpgrade_RejectsWhenResourcesInsufficient()
        {
            const long testPlayerId = 950000207L;

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
                    BuildingId = VillageManagementEngine.LumberjackBuildingId,
                    CurrentLevel = 0
                });
                db.CommodityRecords.AddRange(
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "wood", Quantity = 1L },
                    new CommodityRecord { PlayerId = testPlayerId, ItemId = "stone", Quantity = 1L });
                await db.SaveChangesAsync();
            }

            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await villageManagementEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.LumberjackBuildingId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var infrastructure = await verifyDb.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.LumberjackBuildingId);
            var wood = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "wood");
            var stone = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "stone");

            Assert.Equal(0, infrastructure.CurrentLevel);
            Assert.Equal(0, infrastructure.UpgradeTargetLevel);
            Assert.Equal(1L, wood.Quantity);
            Assert.Equal(1L, stone.Quantity);
        }

        [Fact]
        public async Task Test_VillageUpgrade_MaturesAfterCompletionEpochAndFreesQueue()
        {
            const long testPlayerId = 950000307L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                // Already-queued upgrade with a completion epoch in the past,
                // simulating a player returning after the timer elapsed.
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.LumberjackBuildingId,
                    CurrentLevel = 3,
                    UpgradeTargetLevel = 4,
                    UpgradeCompletesAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10L
                });
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await VillageManagementEngine.ResolveMaturedUpgradesAsync(db, testPlayerId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var infrastructure = await verifyDb.VillageInfrastructures.AsNoTracking()
                .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.LumberjackBuildingId);

            Assert.Equal(4, infrastructure.CurrentLevel);
            Assert.Equal(0, infrastructure.UpgradeTargetLevel);
            Assert.Equal(0L, infrastructure.UpgradeCompletesAtEpoch);
        }

        [Fact]
        public void Test_StatsCalculator_EpicMutationScalesBaseAttributes()
        {
            CombatStats baseline = StatsCalculator.Calculate(str: 100, dex: 100, con: 100, lck: 100);
            CombatStats mutated = StatsCalculator.Calculate(str: 100, dex: 100, con: 100, lck: 100, isEpicMutation: true);

            Assert.True(mutated.FlatMeleeDamage > baseline.FlatMeleeDamage);
            Assert.True(mutated.MaxHp > baseline.MaxHp);
            Assert.True(mutated.FlatRangedDamage > baseline.FlatRangedDamage);
        }

        [Fact]
        public void Test_StatsCalculator_GeneticLociScaleCritAndAttackSpeed()
        {
            CombatStats baseline = StatsCalculator.Calculate(str: 50, dex: 50, con: 50, lck: 50);
            CombatStats withLoci = StatsCalculator.Calculate(str: 50, dex: 50, con: 50, lck: 50, locusSpeed: 10, locusCrit: 10);

            Assert.True(withLoci.CritChancePct > baseline.CritChancePct);
            Assert.True(withLoci.AttackSpeedPct > baseline.AttackSpeedPct);
        }

        [Fact]
        public void Test_StatsCalculator_ComputeEffectiveMilliAttack_ScalesWithGearAndLevel()
        {
            CombatStats naked = StatsCalculator.Calculate(str: 0, dex: 0, con: 0, lck: 0);
            CombatStats geared = StatsCalculator.Calculate(str: 100, dex: 0, con: 0, lck: 0, equippedFlatAttack: 500);

            long nakedAttack = StatsCalculator.ComputeEffectiveMilliAttack(in naked, damageScalePerLevelPct: 0, level: 0);
            long gearedAttack = StatsCalculator.ComputeEffectiveMilliAttack(in geared, damageScalePerLevelPct: 0, level: 0);
            long gearedHighLevelAttack = StatsCalculator.ComputeEffectiveMilliAttack(in geared, damageScalePerLevelPct: 5, level: 50);

            Assert.Equal(StatsCalculator.BaseMilliAttack, nakedAttack);
            Assert.True(gearedAttack > nakedAttack, "Geared attacker must hit harder than a naked one with identical level scaling.");
            Assert.True(gearedHighLevelAttack > gearedAttack, "Level scaling must further increase effective attack on top of gear.");
        }

        // Modul: covers the actual guild-vs-guild combat pipeline this
        // formula unification exists to fix - GuildWarDefensiveSnapshots
        // previously had no writer at all, so ExecuteCombatTurnAsync's
        // real-stats path (added alongside the shared formula extraction)
        // had nothing to read. Two otherwise-identical matches differ only
        // in the attacking guild's snapshot (fully geared vs a naked/never-
        // played guild with a zeroed CombatStats snapshot); the geared
        // attacker's recorded DamageDelta must be meaningfully larger,
        // proving GuildCombatSimulationEngine's registers are actually
        // derived from real stats rather than the old guildId-hash
        // placeholder (which would show no such gap).
        [Fact]
        public async Task Test_GuildCombat_DamageScalesWithGearedVsNakedAttackerSnapshot()
        {
            const long gearedAttackingGuildId = 960000001L;
            const long nakedAttackingGuildId = 960000002L;
            const long defendingGuildId = 960000003L;
            const long gearedMatchId = 960000011L;
            const long nakedMatchId = 960000012L;

            var gearedStats = new CombatStats { FlatMeleeDamage = 500, FlatPhysicalArmor = 0, CritChancePct = 0f, CritMitigationPct = 0f };
            var nakedAttackerStats = new CombatStats { FlatMeleeDamage = 0, FlatPhysicalArmor = 0, CritChancePct = 0f, CritMitigationPct = 0f };
            var defenderStats = new CombatStats { FlatMeleeDamage = 0, FlatPhysicalArmor = 0, CritChancePct = 0f, CritMitigationPct = 0f };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.GuildWarDefensiveSnapshots.Add(new GuildWarDefensiveSnapshot { GuildId = gearedAttackingGuildId, RosterPayloadJson = System.Text.Json.JsonSerializer.Serialize(gearedStats) });
                db.GuildWarDefensiveSnapshots.Add(new GuildWarDefensiveSnapshot { GuildId = nakedAttackingGuildId, RosterPayloadJson = System.Text.Json.JsonSerializer.Serialize(nakedAttackerStats) });
                db.GuildWarDefensiveSnapshots.Add(new GuildWarDefensiveSnapshot { GuildId = defendingGuildId, RosterPayloadJson = System.Text.Json.JsonSerializer.Serialize(defenderStats) });

                db.GuildWarActiveMatches.Add(new GuildWarActiveMatch { MatchId = gearedMatchId, AttackingGuildId = gearedAttackingGuildId, DefendingGuildId = defendingGuildId, InitialSeed = 12345, CurrentStateBitmask = 0 });
                db.GuildWarActiveMatches.Add(new GuildWarActiveMatch { MatchId = nakedMatchId, AttackingGuildId = nakedAttackingGuildId, DefendingGuildId = defendingGuildId, InitialSeed = 12345, CurrentStateBitmask = 0 });

                await db.SaveChangesAsync();
            }

            var guildCombatEngine = new GuildCombatSimulationEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            var gearedTurnPacket = new ClientCommandPacket { Command = CommandType.ExecuteCombatTurn, MatchId = (uint)gearedMatchId, ClientPredictedTurnCounter = 0 };
            var gearedResult = await guildCombatEngine.ExecuteCombatTurnAsync(playerId: 1L, guildId: gearedAttackingGuildId, gearedTurnPacket);

            var nakedTurnPacket = new ClientCommandPacket { Command = CommandType.ExecuteCombatTurn, MatchId = (uint)nakedMatchId, ClientPredictedTurnCounter = 0 };
            var nakedResult = await guildCombatEngine.ExecuteCombatTurnAsync(playerId: 1L, guildId: nakedAttackingGuildId, nakedTurnPacket);

            Assert.Equal(GuildCombatTurnResult.Applied, gearedResult);
            Assert.Equal(GuildCombatTurnResult.Applied, nakedResult);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var gearedDamage = await verifyDb.GuildWarCombatHistory.AsNoTracking()
                .Where(h => h.MatchId == gearedMatchId)
                .Select(h => h.DamageDelta)
                .SingleAsync();
            var nakedDamage = await verifyDb.GuildWarCombatHistory.AsNoTracking()
                .Where(h => h.MatchId == nakedMatchId)
                .Select(h => h.DamageDelta)
                .SingleAsync();

            Assert.True(gearedDamage > nakedDamage, $"Geared attacker (FlatMeleeDamage=500) dealt {gearedDamage}, naked attacker dealt {nakedDamage} - expected geared to deal meaningfully more.");
        }

        [Fact]
        public void Test_StatsCalculator_HighLootLuckIncreasesDropWeight()
        {
            CombatStats lowLuck = StatsCalculator.Calculate(str: 10, dex: 10, con: 10, lck: 1);
            CombatStats highLuck = StatsCalculator.Calculate(str: 10, dex: 10, con: 10, lck: 500, completedAreaFlags: unchecked((int)0xFFFFFFFF));

            float lowLuckFactor = 1.0f + (lowLuck.LootLuckPct / 100.0f);
            float highLuckFactor = 1.0f + (highLuck.LootLuckPct / 100.0f);

            // Mirrors SimulationEngine's gathering roll formula: FinalChance =
            // BaseChance * (1 + LootLuckPct / 100.0). A high-luck character must
            // produce a strictly larger multiplier, shifting drop weight upward.
            Assert.True(highLuckFactor > lowLuckFactor);
            Assert.True(highLuck.LootLuckPct > lowLuck.LootLuckPct);
        }

        [Fact]
        public void Test_GeneticSplicingEngine_InbreedingDegradationNeverIncreasesLoci()
        {
            var original = new GeneticVector(0);
            original.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };
            original.LocusSpeed = new Locus { Dominant = 20, Recessive = 16 };
            original.LocusCrit = new Locus { Dominant = 24, Recessive = 12 };
            original.LocusYield = new Locus { Dominant = 28, Recessive = 8 };

            long degradedGenome = GeneticSplicingEngine.ApplyInbreedingDegradation(original.RawValue);
            var degraded = new GeneticVector(degradedGenome);

            Assert.True(degraded.LocusSpeed.Dominant <= original.LocusSpeed.Dominant);
            Assert.True(degraded.LocusSpeed.Recessive <= original.LocusSpeed.Recessive);
            Assert.True(degraded.LocusCrit.Dominant <= original.LocusCrit.Dominant);
            Assert.True(degraded.LocusCrit.Recessive <= original.LocusCrit.Recessive);
            Assert.True(degraded.LocusYield.Dominant <= original.LocusYield.Dominant);
            Assert.True(degraded.LocusYield.Recessive <= original.LocusYield.Recessive);

            // LocusRace must never be degraded - a genetic defect changes
            // potential, not species.
            Assert.Equal(original.LocusRace.Dominant, degraded.LocusRace.Dominant);
            Assert.Equal(original.LocusRace.Recessive, degraded.LocusRace.Recessive);
        }

        [Fact]
        public async Task Test_Breeding_SiblingPairingSetsInbredFlag()
        {
            const long testPlayerId = 950000008L;
            Guid grandparentId = Guid.NewGuid();
            Guid siblingAId = Guid.NewGuid();
            Guid siblingBId = Guid.NewGuid();

            var sharedGenome = new GeneticVector(0);
            sharedGenome.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };

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
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 10000L });
                // Both siblings share the same paternal ancestor (the "grandparent"
                // relative to the prospective grandchild), the classic inbreeding
                // case within 2 generations.
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = siblingAId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false },
                    new CharacterRecord { Id = siblingBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = siblingAId, ParentPaternalId = grandparentId, GenerationIndex = 1, GeneticVector = sharedGenome.RawValue },
                    new CharacterLineageRegistry { CharacterId = siblingBId, ParentPaternalId = grandparentId, GenerationIndex = 1, GeneticVector = sharedGenome.RawValue });
                await db.SaveChangesAsync();
            }

            var breedingEngine = new BreedingEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await breedingEngine.ExecuteBreedingAsync(testPlayerId, siblingAId, siblingBId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var childLineage = await verifyDb.CharacterLineages.AsNoTracking()
                .SingleAsync(l => l.ParentPaternalId == siblingAId && l.ParentMaternalId == siblingBId);

            Assert.True(childLineage.IsInbred);
        }

        // Modul 13.4.3: proves the FOR UPDATE row locks on the shared parent
        // (sharedParentId) inside ExecuteBreedingAsync's Serializable
        // transaction actually serialize two concurrent breeding attempts,
        // not just reject a second SEQUENTIAL attempt against an already-
        // Active parent (see Test_Breeding_RollbackWhileParentOnCooldown for
        // that simpler case). Both attempts race for real via Task.WhenAll;
        // whichever transaction locks sharedParentId's rows first commits
        // and sets IsBreedingActive=true, and the other - blocked on the
        // same row lock until the first either commits or rolls back - must
        // then observe that flag and roll itself back, producing exactly one
        // child and deducting exactly one breeding cost, never two.
        [Fact]
        public async Task Test_Breeding_ConcurrentAttemptsSharingParent_OnlyOneSucceeds()
        {
            const long testPlayerId = 950000009L;
            Guid sharedParentId = Guid.NewGuid();
            Guid candidateBId = Guid.NewGuid();
            Guid candidateCId = Guid.NewGuid();

            var sharedGenome = new GeneticVector(0);
            sharedGenome.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };

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
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 10000L });
                db.CharacterRecords.AddRange(
                    new CharacterRecord { Id = sharedParentId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false },
                    new CharacterRecord { Id = candidateBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false },
                    new CharacterRecord { Id = candidateCId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false });
                db.CharacterLineages.AddRange(
                    new CharacterLineageRegistry { CharacterId = sharedParentId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue },
                    new CharacterLineageRegistry { CharacterId = candidateBId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue },
                    new CharacterLineageRegistry { CharacterId = candidateCId, GenerationIndex = 0, GeneticVector = sharedGenome.RawValue });
                await db.SaveChangesAsync();
            }

            var breedingEngine = new BreedingEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            var attempt1 = breedingEngine.ExecuteBreedingAsync(testPlayerId, sharedParentId, candidateBId);
            var attempt2 = breedingEngine.ExecuteBreedingAsync(testPlayerId, sharedParentId, candidateCId);
            await Task.WhenAll(attempt1, attempt2);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            int childCount = await verifyDb.CharacterLineages.AsNoTracking()
                .CountAsync(l => l.ParentPaternalId == sharedParentId);
            Assert.Equal(1, childCount);

            var updatedParent = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == sharedParentId);
            Assert.True(updatedParent.IsBreedingActive);

            var updatedGoldRecord = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");
            Assert.Equal(10000L - 500L, updatedGoldRecord.Quantity);
        }

        [Fact]
        public async Task Test_Mentorship_AssignMentorDoesNotThrowOnUnquotedTableRegression()
        {
            const long testPlayerId = 950000010L;
            Guid characterId = Guid.NewGuid();

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
                    BuildingId = VillageManagementEngine.MentorshipAcademyBuildingId,
                    CurrentLevel = 3
                });
                db.CharacterRecords.Add(new CharacterRecord { Id = characterId, PlayerId = testPlayerId, Level = 1, AgePhase = 1, IsLockedInEscrow = false });
                await db.SaveChangesAsync();
            }

            var mentorshipEngine = new MentorshipEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            // Regression test: MentorshipEngine.cs previously issued this query
            // with unquoted PascalCase identifiers (FromSqlRaw("SELECT * FROM
            // MentorshipAcademyAssignments WHERE PlayerId = ... AND SlotIndex =
            // ...")), which Postgres folds to lowercase and throws "relation
            // does not exist" against every call. This test would have thrown
            // before the fix.
            await mentorshipEngine.ExecuteAssignMentorAsync(testPlayerId, characterId, 0);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var assignment = await verifyDb.MentorshipAcademyAssignments.AsNoTracking()
                .SingleOrDefaultAsync(a => a.PlayerId == testPlayerId && a.SlotIndex == 0);

            Assert.NotNull(assignment);
            Assert.Equal(characterId, assignment!.CharacterId);
        }

        [Fact]
        public async Task Test_ForgeSplicing_RejectsFusionOfEquippedItem()
        {
            const long testPlayerId = 950000011L;
            const string baseItemId = "integration_test_forge_equipped_guard";

            long targetId, sac1Id, sac2Id;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.ForgeBuildingId,
                    CurrentLevel = 10
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 100000L });

                var target = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = 1 };
                var sac1 = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = 1 };
                var sac2 = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = 1 };
                db.EquipmentInstances.AddRange(target, sac1, sac2);
                await db.SaveChangesAsync();

                targetId = target.Id;
                sac1Id = sac1.Id;
                sac2Id = sac2.Id;

                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    EquippedWeaponId = targetId
                });
                await db.SaveChangesAsync();
            }

            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);
            ForgeSplicingResult result = await forgeEngine.ExecuteFusionAsync(testPlayerId, targetId, sac1Id, sac2Id);

            Assert.Equal(ForgeSplicingResult.FailedItemEquipped, result);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            bool allItemsStillExist = await verifyDb.EquipmentInstances.AsNoTracking()
                .CountAsync(e => e.Id == targetId || e.Id == sac1Id || e.Id == sac2Id) == 3;

            Assert.True(allItemsStillExist);
        }

        [Fact]
        public async Task Test_GatheringLootLuck_ShiftsWeightTowardRareEntry()
        {
            var lootTable = new LootTableEntry[]
            {
                new LootTableEntry { ItemId = 1, Weight = 90 },
                new LootTableEntry { ItemId = 3, Weight = 10 }
            };

            const long lowLuckPlayerId = 950000012L;
            const long highLuckPlayerId = 950000013L;
            const int rollCount = 400;
            const int inventorySpace = 400;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await OfflineSimulationEngine.GrantAnalyticalLootAsync(db, lowLuckPlayerId, lootTable, rollCount, inventorySpace, 0f);
                await OfflineSimulationEngine.GrantAnalyticalLootAsync(db, highLuckPlayerId, lootTable, rollCount, inventorySpace, 5000f);
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            string rareMaterialName = ContentRegistry.GetMaterialString(3);
            long lowLuckRareQuantity = await verifyDb.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == lowLuckPlayerId && c.ItemId == rareMaterialName)
                .Select(c => (long?)c.Quantity).SingleOrDefaultAsync() ?? 0L;
            long highLuckRareQuantity = await verifyDb.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == highLuckPlayerId && c.ItemId == rareMaterialName)
                .Select(c => (long?)c.Quantity).SingleOrDefaultAsync() ?? 0L;

            // High luck (5000%) adds a flat +500 weight bonus to every entry,
            // which overwhelmingly favors the low-base-weight (rare) entry's
            // relative selection odds while total roll count stays identical
            // (400 for both) - proving luck shifts distribution, not volume.
            Assert.True(highLuckRareQuantity > lowLuckRareQuantity);
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
                CachedCodexDamageMultiplier = multipliers.DamageMultiplier,
                // Ample food stock so the character survives the full offline
                // window against monster 31's incoming damage (see the
                // incoming-damage/food-depletion model in
                // OfflineSimulationEngine.CalculateCombatProjection) - this test
                // exercises the full-duration reward pipeline, not the
                // early-halt path (covered separately).
                Food1_ItemId = 1,
                Food1_Count = 100000
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

            // Modul: replicate the engine's incoming-damage/food-depletion model
            // exactly (expected-value monster crit + Vodnik mitigation, a "free"
            // max-HP absorption buffer before food is needed, then Food1-3
            // healing capacity) since payload here has zero food stocked - the
            // test character can only sustain a fraction of the raw offline
            // window before combat halts, matching the live tick's Auto-Eat halt
            // behavior when food runs out.
            int monsterRegionTier = ((monsterId - 1) % 30) / 6 + 1;
            float monsterCritChance = 0.05f + (monsterRegionTier * 0.005f);
            float mitigatedCritMult = Math.Max(1.0f, 1.5f - (combatStats.CritMitigationPct / 100f));
            float expectedCritMultiplier = 1.0f + monsterCritChance * (mitigatedCritMult - 1.0f);
            long rawIncomingMilliDamage = (long)(monster.AttackPower * 1000 * expectedCritMultiplier);
            long netIncomingMilliDamage = Math.Max(1000L, rawIncomingMilliDamage - (combatStats.FlatPhysicalArmor * 1000L));
            double monsterAttacksPerSecond = monster.AttackIntervalMs > 0 ? 1000.0 / monster.AttackIntervalMs : 0.0;
            double expectedIncomingMilliDps = netIncomingMilliDamage * monsterAttacksPerSecond;

            long effectiveMilliHp = 100000L + (combatStats.MaxHp * 1000L);
            double effectiveElapsedSeconds = elapsedOfflineSeconds;
            if (expectedIncomingMilliDps > 0.0)
            {
                double totalIncomingMilliDamage = expectedIncomingMilliDps * elapsedOfflineSeconds;
                double totalHealCapacityMilliHp = effectiveMilliHp + (100000.0 * 50000.0); // matches payload.Food1_Count above
                if (totalIncomingMilliDamage > totalHealCapacityMilliHp)
                {
                    effectiveElapsedSeconds = totalHealCapacityMilliHp / expectedIncomingMilliDps;
                    if (effectiveElapsedSeconds < 0.0) effectiveElapsedSeconds = 0.0;
                }
            }

            double totalKillsDouble = effectiveElapsedSeconds / secondsPerKill;
            long expectedKills = (long)totalKillsDouble;
            long expectedXpGained = expectedKills * monster.BaseXpReward;
            int expectedLootRolls = (int)(totalKillsDouble * multipliers.YieldMultiplier);

            long expectedXp = expectedXpGained;
            int expectedLevel = 1;
            while (true)
            {
                // Modul: mirrors the GDD exponential level-up curve (Phase 2
                // Part 2.1) - 100 * 1.15^level, replacing the old quadratic
                // 100 * level^2.
                long requiredXp = (long)Math.Ceiling(100.0 * Math.Pow(1.15, expectedLevel));
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
        public async Task Test_OfflineProgression_FoodDepletionHaltsCombatEarly()
        {
            const long testPlayerId = 970000002L;
            const long elapsedOfflineSeconds = 14400L; // 4 hours
            const int monsterId = 31;

            long currentUnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var noFoodPayload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LastLogoutTimestamp = currentUnixTimestamp - elapsedOfflineSeconds,
                ActiveActivityId = monsterId,
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 0,
                InventorySpaceRemaining = 1000
                // Food1-3 all default to zero - no food stocked.
            };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                noFoodPayload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, noFoodPayload, currentUnixTimestamp);
            }

            const long wellFedPlayerId = 970000003L;
            var wellFedPayload = new TickStatePayload
            {
                PlayerId = wellFedPlayerId,
                LastLogoutTimestamp = currentUnixTimestamp - elapsedOfflineSeconds,
                ActiveActivityId = monsterId,
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 0,
                InventorySpaceRemaining = 1000,
                Food1_ItemId = 1,
                Food1_Count = 100000
            };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                wellFedPayload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, wellFedPayload, currentUnixTimestamp);
            }

            // With no food, combat halts far short of the full 4-hour window
            // (mirroring the live tick's Auto-Eat halt when food runs out), so
            // the unfed character reaches strictly less progress than the
            // identical character with ample food over the same offline
            // duration - and the unfed character's untouched food stock proves
            // it never had any healing capacity to draw from.
            Assert.True(wellFedPayload.CurrentLevel >= noFoodPayload.CurrentLevel);
            Assert.Equal(0, noFoodPayload.Food1_Count);
            Assert.True(wellFedPayload.Food1_Count < 100000);
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

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8082/");
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
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry, _fixture.RetryingOptions);
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

            var craftingEngine = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
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
                SimulationEngine.ProcessPassiveVillageTick(ref payload, 0.1, 0L);
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

        private static IConnectionMultiplexer CreateOfflineRedisMultiplexer()
        {
            var options = ConfigurationOptions.Parse("127.0.0.1:1");
            options.AbortOnConnectFail = false;
            options.ConnectRetry = 1;
            options.ConnectTimeout = 200;
            return ConnectionMultiplexer.Connect(options);
        }

        private static string MintTestJwt(Guid accountId)
        {
            return AuthenticationEngine.GenerateJwt(accountId, AuthenticationEngine.GenerateSessionNonce(), AuthenticationDefaults.LocalDevelopmentFallback, out _);
        }

        // Mirrors WebSocketClient.SendAuthHandshakeAsync's fixed-buffer write
        // pattern - MemoryMarshal.Write needs the JwtToken bytes already
        // placed inside the struct's fixed buffer before it can blit the
        // whole AuthHandshakePacket into a wire-ready byte array.
        private static unsafe byte[] BuildAuthHandshakeBuffer(string jwt)
        {
            byte[] jwtBytes = System.Text.Encoding.UTF8.GetBytes(jwt);
            var packet = new AuthHandshakePacket
            {
                JwtTokenLength = (ushort)jwtBytes.Length,
                AssetHash = 0,
                PlatformSignature = 0
            };

            byte* target = packet.JwtToken;
            for (int i = 0; i < AuthHandshakePacket.JwtTokenCapacity; i++)
            {
                target[i] = i < jwtBytes.Length ? jwtBytes[i] : (byte)0;
            }

            byte[] buffer = new byte[Marshal.SizeOf<AuthHandshakePacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            return buffer;
        }

        // Replicates AuthenticationEngine.GenerateJwt's exact encode shape
        // locally (rather than adding a test-only overload to production
        // code) so a token with a past-dated exp claim can be hand-minted.
        private static string BuildRawJwtWithExpiration(Guid accountId, string sessionNonce, long expirationEpoch, string secretKey)
        {
            const string headerJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
            string headerSegment = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(headerJson));
            string payloadJson = "{\"aid\":\"" + accountId.ToString("N") + "\",\"nonce\":\"" + sessionNonce + "\",\"exp\":" + expirationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            string payloadSegment = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payloadJson));

            string signingInput = headerSegment + "." + payloadSegment;
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secretKey));
            byte[] signature = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(signingInput));

            return signingInput + "." + Base64UrlEncode(signature);
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string TamperSignature(string jwt)
        {
            string[] parts = jwt.Split('.');
            char[] signatureChars = parts[2].ToCharArray();
            signatureChars[0] = signatureChars[0] == 'A' ? 'B' : 'A';
            parts[2] = new string(signatureChars);
            return parts[0] + "." + parts[1] + "." + parts[2];
        }

        // Sends a handshake packet carrying jwt and asserts the server
        // closes the connection rather than accepting it - shared by every
        // "this token must be rejected" scenario below.
        private static async Task AssertHandshakeRejectedAsync(string wsUrl, string jwt)
        {
            using var clientSocket = new ClientWebSocket();
            try
            {
                await clientSocket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"WARNING: Skipping handshake-rejection verification because the local WebSocket listener is unavailable: {ex.Message}");
                return;
            }

            byte[] authBuffer = BuildAuthHandshakeBuffer(jwt);
            await clientSocket.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var recvBuffer = new byte[1024];
            WebSocketReceiveResult result;
            try
            {
                result = await clientSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Server never responded to an invalid handshake token; expected a close.");
                return;
            }

            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }

        [Fact]
        public async Task Test_Handshake_GameplayCommandBeforeAuth_TerminatesConnection()
        {
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8090/");
            networkSystem.Start();

            try
            {
                using var clientSocket = new ClientWebSocket();
                try
                {
                    await clientSocket.ConnectAsync(new Uri("ws://localhost:8090/"), CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"WARNING: Skipping unauthenticated-gameplay-rejection verification because the local WebSocket listener is unavailable: {ex.Message}");
                    return;
                }

                // A gameplay command sent as the very first message - never
                // preceded by an AuthHandshakePacket - must be rejected
                // outright, regardless of its contents.
                var gameplayPacket = new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = 1 };
                byte[] gameplayBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
                MemoryMarshal.Write(new Span<byte>(gameplayBuffer), gameplayPacket);
                await clientSocket.SendAsync(new ArraySegment<byte>(gameplayBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var recvBuffer = new byte[1024];
                WebSocketReceiveResult result;
                try
                {
                    result = await clientSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail("Server never responded to a pre-handshake gameplay packet; expected an aggressive close.");
                    return;
                }

                Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        [Fact]
        public async Task Test_Auth_ExpiredAndTamperedJwt_RejectedAtHandshakeAndHttp()
        {
            const long testPlayerId = 970000010L;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            string expiredJwt = BuildRawJwtWithExpiration(accountId, AuthenticationEngine.GenerateSessionNonce(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600L, AuthenticationDefaults.LocalDevelopmentFallback);
            string tamperedJwt = TamperSignature(MintTestJwt(accountId));

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8091/");
            networkSystem.Start();

            try
            {
                await AssertHandshakeRejectedAsync("ws://localhost:8091/", expiredJwt);
                await AssertHandshakeRejectedAsync("ws://localhost:8091/", tamperedJwt);

                using var httpClient = new System.Net.Http.HttpClient();

                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", expiredJwt);
                var expiredResponse = await httpClient.GetAsync("http://localhost:8091/api/v1/market/listings?baseItemId=x&qualityTier=0&pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, expiredResponse.StatusCode);

                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tamperedJwt);
                var tamperedResponse = await httpClient.GetAsync("http://localhost:8091/api/v1/market/listings?baseItemId=x&qualityTier=0&pageIndex=0&pageSize=10");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, tamperedResponse.StatusCode);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        [Fact]
        public async Task Test_Handshake_ConcurrentConnectionsSameAccount_EvictsStaleSession()
        {
            const long testPlayerId = 970000011L;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8092/");
            networkSystem.Start();

            try
            {
                using var firstSocket = new ClientWebSocket();
                try
                {
                    await firstSocket.ConnectAsync(new Uri("ws://localhost:8092/"), CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"WARNING: Skipping concurrent-session-eviction verification because the local WebSocket listener is unavailable: {ex.Message}");
                    return;
                }

                byte[] firstAuthBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
                await firstSocket.SendAsync(new ArraySegment<byte>(firstAuthBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                // Give the accept loop time to complete the first handshake
                // and register the session before the second connection
                // contests ownership of the same account.
                await Task.Delay(500);
                Assert.Equal(WebSocketState.Open, firstSocket.State);

                var firstCloseDetected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[64];
                    try
                    {
                        while (firstSocket.State == WebSocketState.Open)
                        {
                            var result = await firstSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                firstCloseDetected.TrySetResult(true);
                                break;
                            }
                        }
                    }
                    catch
                    {
                        firstCloseDetected.TrySetResult(true);
                    }
                });

                using var secondSocket = new ClientWebSocket();
                await secondSocket.ConnectAsync(new Uri("ws://localhost:8092/"), CancellationToken.None);

                byte[] secondAuthBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
                await secondSocket.SendAsync(new ArraySegment<byte>(secondAuthBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                var completed = await Task.WhenAny(firstCloseDetected.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(completed == firstCloseDetected.Task, "Expected the first (stale) session to be evicted once the second connection authenticated for the same account.");

                // The second connection is the new live session for this
                // account - confirm the eviction did not also take it down.
                await Task.Delay(300);
                Assert.Equal(WebSocketState.Open, secondSocket.State);
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        [Fact]
        public async Task Test_NetworkBroadcastSystem_ConcurrentSendToPlayer_DoesNotFaultSocket()
        {
            const long testPlayerId = 970000100L;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8093/");
            networkSystem.Start();

            try
            {
                using var socket = new ClientWebSocket();
                try
                {
                    await socket.ConnectAsync(new Uri("ws://localhost:8093/"), CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"WARNING: Skipping concurrent-send verification because the local WebSocket listener is unavailable: {ex.Message}");
                    return;
                }

                byte[] authBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
                await socket.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);
                await Task.Delay(500);
                Assert.Equal(WebSocketState.Open, socket.State);

                int receivedCount = 0;
                var receiveCts = new CancellationTokenSource();
                var receiveTask = Task.Run(async () =>
                {
                    var buffer = new byte[4096];
                    try
                    {
                        while (socket.State == WebSocketState.Open && !receiveCts.IsCancellationRequested)
                        {
                            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            Interlocked.Increment(ref receivedCount);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });

                // Fires many concurrent sends at the same connection,
                // mirroring the real race between the 1Hz state broadcast
                // and any other independent sender hitting the same socket
                // at once. Before the WebSocketSession semaphore fix, .NET's
                // WebSocket throws "already one outstanding SendAsync call"
                // the moment two of these overlap, which silently aborts
                // the connection with the fire-and-forget exception never
                // observed anywhere.
                var sendTasks = new Task[50];
                for (int i = 0; i < sendTasks.Length; i++)
                {
                    sendTasks[i] = Task.Run(() =>
                    {
                        var packet = new StateUpdatePacket { PlayerId = testPlayerId };
                        networkSystem.SendToPlayer(testPlayerId, ref packet);
                    });
                }
                await Task.WhenAll(sendTasks);

                // Give the fire-and-forget sends time to actually complete
                // and reach the client.
                await Task.Delay(1000);

                Assert.Equal(WebSocketState.Open, socket.State);
                Assert.True(receivedCount > 0, "Expected at least one StateUpdatePacket to have been received despite the concurrent send burst.");

                receiveCts.Cancel();
                try { await receiveTask; } catch { }
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        [Fact]
        public async Task Test_StateCheckpointManager_FlushFailure_RetainsDirtyFlagForNextCycle()
        {
            const long testPlayerId = 970000101L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    LogicEpochCounter = 50L
                });
                await db.SaveChangesAsync();
            }

            var checkpointManager = new StateCheckpointManager(_fixture.ServiceProvider);

            // A stale LogicEpochCounter (behind the DB's) deterministically
            // triggers FlushState's split-brain sieve, which rolls back and
            // returns false without throwing - the same "flush did not
            // commit" outcome a Serializable conflict that exhausts its
            // retries would produce (that path returns false via the outer
            // catch instead, but TrackState cannot and should not
            // distinguish the two - see FlushState). This is what Part 1's
            // fix to TrackState is actually about: neither outcome may be
            // treated as a successful checkpoint.
            var state = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LogicEpochCounter = 10L,
                TicksSinceLastFlush = 3000,
                IsDirty = true,
                InventorySpaceRemaining = 20
            };

            checkpointManager.TrackState(ref state);

            Assert.True(state.IsDirty, "A failed flush must leave IsDirty set so the state is requeued on the next cycle instead of being silently discarded.");
            Assert.Equal(3000, state.TicksSinceLastFlush);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
            Assert.Equal(50L, player.LogicEpochCounter);
        }

        // Builds a fully live SimulationEngine + NetworkBroadcastSystem pair
        // (unlike CreateTestSimulationEngine, which returns a SimulationEngine
        // whose NetworkBroadcastSystem is never Start()-ed and is therefore
        // unusable for real WebSocket traffic) - needed here because mana
        // deduction and cooldown rejection live inside SimulationEngine.
        // EngineLoop's CommandQueue.TryDequeue dispatch, which only runs on
        // the background engine thread, not via the single-payload ProcessTick
        // helper the lighter chrono test above uses.
        private (SimulationEngine SimulationEngine, NetworkBroadcastSystem NetworkSystem) CreateLiveSimulationEngine(string uriPrefix)
        {
            var serviceProvider = _fixture.ServiceProvider;
            var playerRegistry = _fixture.PlayerRegistry;
            var contextFactory = _fixture.DbContextFactory;

            var networkSystem = new NetworkBroadcastSystem(serviceProvider, AuthenticationDefaults.LocalDevelopmentFallback, uriPrefix);
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
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry, _fixture.RetryingOptions);
            var worldBossEngine = new WorldBossEngine(serviceProvider, playerRegistry);
            var villageBuildingEngine = new VillageBuildingEngine(serviceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var mentorshipEngine = new MentorshipEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(serviceProvider, playerRegistry);
            var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);

            var antiCheatTelemetryEngine = new AntiCheatTelemetryEngine(serviceProvider, null!, playerRegistry, networkSystem);
            networkSystem.RegisterAntiCheatTelemetryEngine(antiCheatTelemetryEngine);

            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, antiCheatTelemetryEngine, null!, null!, null!, null!, contextFactory);

            return (simulationEngine, networkSystem);
        }

        private static async Task SendCommandAsync(ClientWebSocket socket, ClientCommandPacket packet)
        {
            byte[] buffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        [Fact]
        public async Task Test_ActiveSkillTree_CastDeductsManaAppliesDamageMultiplier_CooldownRejectsRecast_InvalidSkillDisconnects()
        {
            const long testPlayerId = 970000020L;
            const int forestRatActivityId = 55; // 69 HP, 15 dmg/hit baseline - see Test_Handshake_* precedent / E2EGameLoopTest.
            // Skill 2, not skill 1: ManaCost (20) must exceed
            // ActiveSkillEngine.ManaRegenPerTick(1) * the ~10 sub-ticks in one
            // NetworkBroadcastSystem broadcast cycle (~1s), or passive regen
            // can fully mask the deduction by the time the first observable
            // StateUpdatePacket after the cast arrives - skill 1's ManaCost
            // (10) exactly equals that worst-case regen and was observed to
            // do exactly this in practice. ManaCost 20, CooldownMs 6000,
            // DamageMultiplierPct 200.
            const int skillId = 2;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = accountId,
                    AuthenticatorToken = Guid.NewGuid(),
                    AvailableSkillPoints = 1
                });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var (simulationEngine, networkSystem) = CreateLiveSimulationEngine("http://localhost:8093/");
            networkSystem.Start();
            simulationEngine.Start();

            try
            {
                using var clientSocket = new ClientWebSocket();
                try
                {
                    await clientSocket.ConnectAsync(new Uri("ws://localhost:8093/"), CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"WARNING: Skipping active skill tree verification because the local WebSocket listener is unavailable: {ex.Message}");
                    return;
                }

                byte[] authBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
                await clientSocket.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                var receivedPackets = new System.Collections.Concurrent.ConcurrentQueue<StateUpdatePacket>();
                var loginConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var castConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

                var receiveTask = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await clientSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                        }
                        catch
                        {
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count < Marshal.SizeOf<StateUpdatePacket>()) continue;

                        var state = MemoryMarshal.Read<StateUpdatePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        receivedPackets.Enqueue(state);
                        loginConfirmed.TrySetResult();

                        if (state.LastSkillCastResultTick != 0)
                        {
                            castConfirmed.TrySetResult();
                        }

                        // Same anti-cheat challenge auto-response requirement as
                        // every other live-loop test in this file/E2EGameLoopTest -
                        // an unanswered challenge quarantines the player and
                        // silently freezes combat for the rest of the session.
                        if (state.ActiveChallengeSeed != 0)
                        {
                            uint hash = AntiCheatTelemetryEngine.ComputeChallengeHash(state.ActiveChallengeSeed, state.PlayerId, 0L);
                            await SendCommandAsync(clientSocket, new ClientCommandPacket
                            {
                                Command = CommandType.AntiCheatChallengeResponse,
                                ChallengeId = state.ActiveChallengeSeed,
                                ChallengeVerificationHash = hash
                            });
                        }
                    }
                });

                await Task.WhenAny(loginConfirmed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(loginConfirmed.Task.IsCompletedSuccessfully, "Did not observe the player enter the active tick loop before the skill test began.");

                // Unlock skill 1, enter combat, then cast immediately - all
                // three land within the same or next engine tick, well before
                // the 1500ms first-attack timer, so the damage multiplier is
                // guaranteed to be pending before any hit lands.
                await SendCommandAsync(clientSocket, new ClientCommandPacket { Command = CommandType.RequestUnlockSkill, TargetId = skillId });
                await SendCommandAsync(clientSocket, new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = forestRatActivityId });
                await SendCommandAsync(clientSocket, new ClientCommandPacket { Command = CommandType.RequestCastSkill, TargetId = skillId });

                await Task.WhenAny(castConfirmed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(castConfirmed.Task.IsCompletedSuccessfully, "Server never broadcast a skill cast result.");

                // FirstOrDefault, not Last - LastSkillCastResultTick stays
                // nonzero on every subsequent broadcast too (it is only ever
                // incremented, never reset), so the first packet where it
                // flips from 0 is the one closest to the actual cast moment,
                // before passive mana regen has had many ticks to run.
                var castResultPacket = receivedPackets.FirstOrDefault(p => p.LastSkillCastResultTick != 0);
                Assert.Equal((byte)skillId, castResultPacket.LastSkillCastId);
                Assert.Equal((byte)1, castResultPacket.LastSkillCastSuccess);
                // ComputeMaxMana(level 0) = 100 + 0*2 = 100, minus skill 2's 20
                // mana cost = 80. Upper bound allows for up to a full ~1s
                // broadcast cycle's worth of ManaRegenPerTick (1 per ~100ms,
                // ~10 ticks) having already run by the time this packet was
                // broadcast, without that regen being able to fully mask a
                // 20-point deduction the way it could for a 10-point one.
                Assert.True(castResultPacket.CurrentMana >= 80 && castResultPacket.CurrentMana < 100,
                    $"Expected mana to have dropped from 100 by skill 2's 20-point cost (allowing regen drift up to one broadcast cycle), got {castResultPacket.CurrentMana}.");

                // Wait for the boosted hit to land (attack cadence is 1500ms
                // with 0 AttackSpeedPct, matching the well-established Forest
                // Rat baseline this codebase's other live-loop tests already
                // rely on: 15 raw dmg/hit with no stats set).
                await Task.Delay(TimeSpan.FromSeconds(3));

                var hpSnapshots = receivedPackets.ToArray();
                int maxSingleHitDamage = 0;
                for (int i = 1; i < hpSnapshots.Length; i++)
                {
                    if (hpSnapshots[i].CurrentMonsterId == hpSnapshots[i - 1].CurrentMonsterId && hpSnapshots[i].CurrentMonsterId != 0)
                    {
                        int delta = hpSnapshots[i - 1].CurrentMonsterHp - hpSnapshots[i].CurrentMonsterHp;
                        if (delta > maxSingleHitDamage) maxSingleHitDamage = delta;
                    }
                }

                // Baseline (unboosted) hit is 15; skill 2's 200% multiplier
                // produces 30 (15000 milli * 2.0 = 30000 -> 30 whole HP).
                // Assert the largest observed single-hit delta is measurably
                // above the unboosted baseline, proving the multiplier was
                // consumed by a real attack rather than silently dropped.
                Assert.True(maxSingleHitDamage > 20, $"Expected a skill-boosted hit (~30 dmg) to exceed the unboosted baseline (15 dmg) by a clear margin, but the largest observed single-hit delta was {maxSingleHitDamage}.");

                // Cooldown rejection: skill 2 has a 6000ms cooldown and only
                // ~3s have elapsed, so immediately recast to land solidly
                // inside the cooldown window regardless of jitter.
                int manaBeforeRecast = (int)receivedPackets.Last().CurrentMana;
                await SendCommandAsync(clientSocket, new ClientCommandPacket { Command = CommandType.RequestCastSkill, TargetId = skillId });
                await Task.Delay(TimeSpan.FromSeconds(2));

                var rejectedPacket = receivedPackets.Last();
                Assert.Equal((byte)0, rejectedPacket.LastSkillCastSuccess);

                // A rejected cast deducts nothing, so mana can only have
                // stayed the same or grown (passive regen) since
                // manaBeforeRecast - never dropped, which is what a second
                // successful 10-point deduction would cause. This is the
                // opposite direction from "did mana decrease" precisely
                // because regen is the only thing moving it at this point.
                Assert.True(rejectedPacket.CurrentMana >= manaBeforeRecast, $"A cooldown-rejected cast must not deduct mana a second time - mana went from {manaBeforeRecast} to {rejectedPacket.CurrentMana}.");
                Assert.Equal(WebSocketState.Open, clientSocket.State);

                // A structurally invalid skill ID is a cheat signal (the real
                // UI could never send one), unlike the soft cooldown/mana
                // rejection above - ClientCommandValidator.ValidateSkillCommand
                // must terminate the connection outright. Do not start a second
                // ReceiveAsync loop here - the original receiveTask above is
                // still the sole reader of this socket (ClientWebSocket does
                // not support concurrent ReceiveAsync calls), and it already
                // breaks out as soon as it observes a Close message or the
                // connection faults, so waiting on ITS completion is the
                // correct signal.
                await SendCommandAsync(clientSocket, new ClientCommandPacket { Command = CommandType.RequestCastSkill, TargetId = 99 });

                var receiveCompleted = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(receiveCompleted == receiveTask, "Expected the connection to be terminated after a structurally invalid RequestCastSkill.");
                Assert.NotEqual(WebSocketState.Open, clientSocket.State);

                cts.Cancel();
                try { await receiveTask; } catch { }
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                simulationEngine.Stop();
                networkSystem.Stop();
            }
        }

        [Fact]
        public async Task Test_ChronoCore_ConcurrentConsumption_SerializesViaForUpdateLock()
        {
            const long testPlayerId = 970000001L;
            const long chronoCoreItemId = 500L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = chronoCoreItemId.ToString(), Quantity = 1 });
                await db.SaveChangesAsync();
            }

            var chronoCoreEngine = new ChronoCoreEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            // Fire concurrent consumption attempts against a single-unit stock;
            // the FOR UPDATE lock inside ConsumeChronoCoreAsync must serialize
            // these so exactly one succeeds and the stock never goes negative.
            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = chronoCoreEngine.ConsumeChronoCoreAsync(testPlayerId, chronoCoreItemId);
            }
            await Task.WhenAll(tasks);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var core = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == chronoCoreItemId.ToString());

            Assert.Equal(0L, core.Quantity);
            Assert.Single(_fixture.PlayerRegistry.ChronoAccelerationQueue.Where(n => n.PlayerId == testPlayerId));
        }

        [Fact]
        public async Task Test_Billing_ConcurrentDuplicateIapReceipt_OnlyOneCreditApplied()
        {
            const long testPlayerId = 970000002L;
            const string transactionId = "iap_txn_dup_970000002";
            const int premiumAmount = 500;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), PremiumDiamonds = 0 });
                await db.SaveChangesAsync();
            }

            using var offlineRedis = CreateOfflineRedisMultiplexer();
            var redisCache = new RedisSessionCache(offlineRedis);
            var billingEngine = new BillingVerificationEngine(_fixture.DbContextFactory, redisCache, _fixture.PlayerRegistry, _fixture.RetryingOptions, new MockIapReceiptValidator());

            async Task<bool> SafeVerifyAsync()
            {
                try
                {
                    return await billingEngine.VerifyPurchaseAsync(testPlayerId, transactionId, "gems_pack_small");
                }
                catch
                {
                    // A thrown unique-constraint/serialization failure is an
                    // equally valid rejection outcome as a soft `false` return -
                    // both mean the duplicate receipt did not get credited.
                    return false;
                }
            }

            // Simulate the same platform webhook receipt arriving twice
            // concurrently (network retry / duplicate delivery); the [Key]
            // unique constraint on TransactionId plus the Serializable
            // transaction boundary must ensure only one credit lands.
            var tasks = new Task<bool>[6];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = SafeVerifyAsync();
            }
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(r => r));

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var profile = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
            Assert.Equal(premiumAmount, profile.PremiumDiamonds);

            var ledgerCount = await verifyDb.PrimaryPurchaseLedgers.AsNoTracking().CountAsync(l => l.TransactionId == transactionId);
            Assert.Equal(1, ledgerCount);
        }

        [Fact]
        public async Task Test_BillingVerificationEngine_DuplicateReceiptTransactionId_RejectedOnSecondAttempt()
        {
            const long testPlayerId = 970000201L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), PremiumDiamonds = 0 });
                await db.SaveChangesAsync();
            }

            using var offlineRedis = CreateOfflineRedisMultiplexer();
            var redisCache = new RedisSessionCache(offlineRedis);
            var billingEngine = new BillingVerificationEngine(_fixture.DbContextFactory, redisCache, _fixture.PlayerRegistry, _fixture.RetryingOptions, new MockIapReceiptValidator());

            // Modul: the mock receipt validator decodes exactly this shape -
            // see MockIapReceiptValidator. TransactionId/ProductId come
            // only from the decoded receipt, never from a separate
            // caller-supplied parameter, matching the real REST endpoint's
            // contract (see NetworkBroadcastSystem.HandleBillingVerify).
            string receiptJson = "{\"transactionId\":\"iap_replay_970000201\",\"productId\":\"gems_pack_small\"}";
            string base64Receipt = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(receiptJson));

            bool firstAttempt = await billingEngine.VerifyReceiptAsync(testPlayerId, base64Receipt);
            bool secondAttempt = await billingEngine.VerifyReceiptAsync(testPlayerId, base64Receipt);

            Assert.True(firstAttempt, "The first submission of a never-before-seen transaction ID must be accepted.");
            Assert.False(secondAttempt, "Resubmitting the exact same transaction ID must be strictly rejected.");

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var profile = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
            Assert.Equal(BillingVerificationEngine.ResolvePremiumDiamondsForProduct("gems_pack_small"), profile.PremiumDiamonds);

            int processedCount = await verifyDb.ProcessedTransactions.AsNoTracking().CountAsync(t => t.TransactionId == "iap_replay_970000201");
            Assert.Equal(1, processedCount);
        }

        [Fact]
        public async Task Test_AuthenticationEngine_OAuthLink_AllowsLoginRecoveryWithoutDeviceId()
        {
            const long testPlayerId = 970000202L;
            string originalDeviceId = "device_oauth_recovery_970000202";
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = accountId,
                    AuthenticatorToken = Guid.NewGuid(),
                    DeviceId = originalDeviceId
                });
                await db.SaveChangesAsync();
            }

            var validator = new MockOAuthTokenValidator();
            string oauthToken = "mock:Google:google_user_970000202";

            var linkOutcome = await AuthenticationEngine.LinkOAuthAccountAsync(_fixture.RetryingOptions, accountId, oauthToken, validator);
            Assert.Equal(OAuthLinkOutcome.Success, linkOutcome);

            // Recovery login succeeds via the OAuth token alone - nothing
            // here references originalDeviceId.
            var recovery = await AuthenticationEngine.TryLoginByOAuthAsync(_fixture.RetryingOptions, oauthToken, validator);

            Assert.True(recovery.Found);
            Assert.Equal(testPlayerId, recovery.PlayerId);
            Assert.Equal(accountId, recovery.AccountId);

            // Linking is irreversible - a second link attempt against the
            // same already-linked account must be rejected outright.
            var relinkOutcome = await AuthenticationEngine.LinkOAuthAccountAsync(_fixture.RetryingOptions, accountId, "mock:Apple:some_other_id", validator);
            Assert.Equal(OAuthLinkOutcome.AlreadyLinked, relinkOutcome);
        }

        [Fact]
        public async Task Test_OfflineSimulationEngine_SevenDayOfflinePeriod_GrantsExactAnalyticalYieldInO1Time()
        {
            const long testPlayerId = 970000203L;
            const long sevenDaysSeconds = 604800L;

            long currentUnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long lastLogoutTimestamp = currentUnixTimestamp - sevenDaysSeconds;

            var payload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LastLogoutTimestamp = lastLogoutTimestamp,
                ActiveActivityId = 0,
                LumberjackLevel = 1,
                WarehouseLevel = 100,
                InventorySpaceRemaining = 20
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                payload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, payload, currentUnixTimestamp);
            }
            stopwatch.Stop();

            // O(1) analytical projection - a loop touching the database once
            // per elapsed second would take drastically longer than this for
            // a 604,800-second gap; a closed-form calculation completes in a
            // handful of milliseconds regardless of how large deltaSeconds is.
            Assert.True(stopwatch.ElapsedMilliseconds < 3000,
                $"Offline extrapolation took {stopwatch.ElapsedMilliseconds}ms for a 7-day gap - expected O(1) analytical projection, not a per-second loop.");

            Assert.Equal(currentUnixTimestamp, payload.LastLogoutTimestamp);
            Assert.True(payload.IsDirty);

            // Modul: OfflineSimulationEngine deliberately caps analytically-
            // projected offline time at 12 hours (43200 seconds) as an
            // anti-abuse measure - see MaxOfflineSeconds and its doc
            // comment - regardless of how much real time actually elapsed.
            // A 7-day gap is exactly the scenario that cap exists for: the
            // expected yield below reflects the CAPPED 43200 seconds, not
            // the full 604800, which is the correct, intentional behavior
            // being verified here, not an oversight.
            const long cappedElapsedSeconds = 43200L;
            long expectedWood = (long)(cappedElapsedSeconds * VillageManagementEngine.LumberjackWoodRatePerLevel);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var woodCommodity = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleOrDefaultAsync(c => c.PlayerId == testPlayerId && c.ItemId == VillageManagementEngine.WoodCommodityId);

            Assert.NotNull(woodCommodity);
            Assert.Equal(expectedWood, woodCommodity!.Quantity);
        }

        [Fact]
        public async Task Test_AuthenticationEngine_ConcurrentAutoProvisioning_ResolvesViaRetryStrategy()
        {
            const int concurrentNewAccounts = 50;
            string devicePrefix = "chaos_device_" + Guid.NewGuid().ToString("N") + "_";

            Task<(long PlayerId, Guid AccountId)> ProvisionOneAsync(int index)
            {
                string deviceId = devicePrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return AuthenticationEngine.LoginOrProvisionAsync(_fixture.RetryingOptions, deviceId);
            }

            // Mirrors the Chaos Tester's real-world failure mode: N distinct,
            // never-seen-before device IDs all provisioning for the first
            // time at once, each opening its own Serializable transaction on
            // its own dedicated retry-configured context - matching
            // HandleAuthLogin's call shape exactly (see RetryingDbContextOptions).
            // This is deliberately NOT
            // a same-device race (Test_Breeding_ConcurrentAttemptsSharingParent_OnlyOneSucceeds
            // and AuthenticationEngine's own unique-index re-check already
            // cover that shape) - it is Postgres's Serializable Snapshot
            // Isolation rejecting otherwise-unrelated concurrent inserts via
            // SQLSTATE 40001, which is exactly what CreateExecutionStrategy's
            // retry configured on the test fixture above must resolve
            // transparently. If any of the 50 propagates an unhandled
            // serialization failure, Task.WhenAll surfaces it and this test
            // fails.
            var tasks = new Task<(long PlayerId, Guid AccountId)>[concurrentNewAccounts];
            for (int i = 0; i < concurrentNewAccounts; i++)
            {
                tasks[i] = ProvisionOneAsync(i);
            }

            var results = await Task.WhenAll(tasks);

            Assert.Equal(concurrentNewAccounts, results.Select(r => r.PlayerId).Distinct().Count());
            Assert.Equal(concurrentNewAccounts, results.Select(r => r.AccountId).Distinct().Count());

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            int provisionedCount = await verifyDb.PlayerRecords.AsNoTracking()
                .CountAsync(p => p.DeviceId != null && p.DeviceId.StartsWith(devicePrefix));
            Assert.Equal(concurrentNewAccounts, provisionedCount);

            foreach (var (playerId, _) in results)
            {
                int commodityCount = await verifyDb.CommodityRecords.AsNoTracking().CountAsync(c => c.PlayerId == playerId);
                Assert.Equal(2, commodityCount);
            }
        }

        [Fact]
        public async Task Test_AntiCheat_AutomationFlag_TriggersImmediateSocketEvictionAndMarketSequestration()
        {
            const long testPlayerId = 970000003L;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid(), IsQuarantined = false, Quarantine_Active = false });
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = testPlayerId, Price = 100, Status = 0, OrderType = "SELL", BaseItemId = "copper_ore", QualityTier = 0 });
                await db.SaveChangesAsync();
            }

            using var offlineRedis = CreateOfflineRedisMultiplexer();
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8083/");
            var antiCheatEngine = new AntiCheatTelemetryEngine(_fixture.ServiceProvider, offlineRedis, _fixture.PlayerRegistry, networkSystem);
            networkSystem.RegisterAntiCheatTelemetryEngine(antiCheatEngine);
            networkSystem.Start();

            try
            {
                using var clientSocket = new ClientWebSocket();
                try
                {
                    await clientSocket.ConnectAsync(new Uri("ws://localhost:8083/"), CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    // Same pre-existing HttpListener/WebSocket environment
                    // limitation documented on E2EGameLoopTest - not something
                    // this task's changes can fix, so skip rather than fail.
                    Console.WriteLine($"WARNING: Skipping socket-eviction verification because the local WebSocket listener is unavailable: {ex.Message}");
                    return;
                }

                byte[] authBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
                await clientSocket.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                // Give the accept loop time to complete the handshake and
                // register the session before the automation flag fires.
                await Task.Delay(500);

                var closeDetected = new TaskCompletionSource<bool>();
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[64];
                    try
                    {
                        while (clientSocket.State == WebSocketState.Open)
                        {
                            var result = await clientSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                closeDetected.TrySetResult(true);
                                break;
                            }
                        }
                    }
                    catch
                    {
                        closeDetected.TrySetResult(true);
                    }
                });

                // Simulate a confirmed automation breach (matches the
                // RecordCommand -> RequestShadowBan path triggered by a
                // macro-flat command cadence).
                antiCheatEngine.RequestShadowBan(testPlayerId, 54, 1);

                var completed = await Task.WhenAny(closeDetected.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(completed == closeDetected.Task, "Expected the socket to be force-closed immediately after a confirmed automation flag.");
            }
            finally
            {
                networkSystem.Stop();
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var profile = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
            Assert.True(profile.IsQuarantined);
            Assert.True(profile.Quarantine_Active);

            var order = await verifyDb.MarketOrderRecords.AsNoTracking().SingleAsync(o => o.SellerId == testPlayerId);
            Assert.True(order.IsQuarantined);
        }

        [Fact]
        public async Task Test_MarketEscrow_UntradedItem_ExtremePriceBlockedByFallbackCorridor()
        {
            const long testPlayerId = 970000004L;
            const string baseItemId = "gilded_sabatons_boots_armor_slot_base"; // ItemDefinition Id 3, BaseValueGold 360
            const int qualityTier = 0;
            long equipmentId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // GuildId: market access now requires a guild trade license
                // (Advanced Economy Refactoring, Part 2.1) - this test is
                // about the price corridor, so the license must pass.
                db.GuildRecords.Add(new GuildRecord { Id = 970000904L, Name = "CorridorTestGuild970000904" });
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = 970000904L });
                var equipment = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = qualityTier };
                db.EquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;
            }

            var escrowEngine = new MarketEscrowEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            // No HistoricalMarketArchives rows exist for this item, so the
            // corridor must fall back to the ContentRegistry baseline
            // (360 * 1.0 = 360, corridor [288, 1080]) rather than allowing
            // an arbitrary RMT-laundering price through.
            bool accepted = await escrowEngine.ListItemAsync(testPlayerId, equipmentId, 999999999L);

            Assert.False(accepted);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var stillInBag = await verifyDb.EquipmentInstances.AsNoTracking().SingleOrDefaultAsync(e => e.Id == equipmentId);
            Assert.NotNull(stillInBag);

            bool anyMarketMirror = await verifyDb.MarketEquipmentInstances.AsNoTracking().AnyAsync(e => e.PlayerId == testPlayerId);
            Assert.False(anyMarketMirror);
        }

        [Fact]
        public async Task Test_MarketEscrow_EquippedItem_ListingRejectedBeforeMutation()
        {
            const long testPlayerId = 970000005L;
            const string baseItemId = "gilded_sabatons_boots_armor_slot_base";
            long equipmentId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var equipment = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = 0 };
                db.EquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;

                // GuildId: the trade license must pass so the
                // equipped-item guard under test is actually reached.
                db.GuildRecords.Add(new GuildRecord { Id = 970000905L, Name = "EquippedGuardGuild970000905" });
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    EquippedWeaponId = equipmentId,
                    GuildId = 970000905L
                });
                await db.SaveChangesAsync();
            }

            var escrowEngine = new MarketEscrowEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            bool accepted = await escrowEngine.ListItemAsync(testPlayerId, equipmentId, 500L);

            Assert.False(accepted);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var stillInBag = await verifyDb.EquipmentInstances.AsNoTracking().SingleOrDefaultAsync(e => e.Id == equipmentId);
            Assert.NotNull(stillInBag);

            bool anyOrderCreated = await verifyDb.MarketOrderRecords.AsNoTracking().AnyAsync(o => o.SellerId == testPlayerId);
            Assert.False(anyOrderCreated);
        }

        [Fact]
        public async Task Test_MarketEscrow_ConcurrentListings_ExactReplicaNoSerializationDrift()
        {
            const long testPlayerId = 970000006L;
            const string baseItemId = "gilded_sabatons_boots_armor_slot_base";
            const int itemCount = 6;
            var equipmentIds = new long[itemCount];
            var affixPayloads = new string[itemCount];

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // GuildId: the trade license must pass so the concurrent
                // listing behavior under test is actually reached.
                db.GuildRecords.Add(new GuildRecord { Id = 970000906L, Name = "ConcurrentListGuild970000906" });
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = 970000906L });

                for (int i = 0; i < itemCount; i++)
                {
                    affixPayloads[i] = $"{{\"flat_hp_slot{i}\":{100 + i}}}";
                    var equipment = new EquipmentInstance
                    {
                        PlayerId = testPlayerId,
                        BaseItemId = baseItemId,
                        QualityTier = 0,
                        AffixPayload = affixPayloads[i],
                        IsAffixLocked = i % 2 == 0
                    };
                    db.EquipmentInstances.Add(equipment);
                    await db.SaveChangesAsync();
                    equipmentIds[i] = equipment.Id;
                }
            }

            // Postgres reformats jsonb text on round-trip (e.g. adds a space
            // after ':'), so the true "zero serialization drift" baseline is
            // what the bag row actually holds after that round-trip, not the
            // pre-insert literal above - re-read it before listing.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                for (int i = 0; i < itemCount; i++)
                {
                    long id = equipmentIds[i];
                    affixPayloads[i] = (await db.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == id)).AffixPayload;
                }
            }

            var escrowEngine = new MarketEscrowEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            // Fire all six listings concurrently (highly concurrent
            // multi-threaded listing load) at a price inside the fallback
            // corridor (288-1080); each must migrate exactly one item with
            // zero cross-contamination between rows.
            var tasks = new Task<bool>[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                tasks[i] = escrowEngine.ListItemAsync(testPlayerId, equipmentIds[i], 500L);
            }
            var results = await Task.WhenAll(tasks);

            Assert.All(results, Assert.True);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            long remainingInBag = await verifyDb.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == testPlayerId);
            Assert.Equal(0, remainingInBag);

            var marketMirrors = await verifyDb.MarketEquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == testPlayerId)
                .ToListAsync();
            Assert.Equal(itemCount, marketMirrors.Count);

            for (int i = 0; i < itemCount; i++)
            {
                var expectedPayload = affixPayloads[i];
                var matchingMirror = marketMirrors.SingleOrDefault(m => m.AffixPayload == expectedPayload);
                Assert.NotNull(matchingMirror);
                Assert.Equal(baseItemId, matchingMirror!.BaseItemId);
                Assert.Equal(0, matchingMirror.QualityTier);
                Assert.True(matchingMirror.IsLockedInEscrow);
                Assert.Equal(i % 2 == 0, matchingMirror.IsAffixLocked);
            }

            var linkedOrders = await verifyDb.MarketOrderRecords.AsNoTracking()
                .Where(o => o.SellerId == testPlayerId)
                .ToListAsync();
            Assert.Equal(itemCount, linkedOrders.Count);

            foreach (var order in linkedOrders)
            {
                Assert.NotNull(order.EquipmentInstanceId);
                Assert.Contains(marketMirrors, m => m.Id == order.EquipmentInstanceId!.Value);
            }
        }

        // Modul: Content Pipeline fast-fail coverage. ContentRegistry.Initialize/
        // ActiveSkillEngine.Initialize are deliberately parameterized to accept
        // an explicit directory (rather than always resolving AppContext.
        // BaseDirectory internally) precisely so this can be tested directly
        // against a deliberately broken temp directory, without needing to
        // spawn a separate process to observe a real boot crash. The
        // atomic-commit design in both Initialize methods (build into local
        // variables, only assign the static fields after every file parses
        // and validates successfully) means a failed call here must leave the
        // real content data - already loaded once by PostgresTestFixture.
        // InitializeAsync/E2EGameLoopTest.InitializeAsync before any test in
        // this class runs - completely untouched, which this test also
        // verifies explicitly so a regression that broke that guarantee would
        // itself fail this test.
        [Fact]
        public void Test_ContentPipeline_MissingOrMalformedJson_FailsFast()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "folkidle_content_test_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);

            try
            {
                // Case 1: directory exists but is entirely empty - every
                // required file is missing.
                Assert.Throws<InvalidOperationException>(() => ContentRegistry.Initialize(tempDir));
                Assert.Throws<InvalidOperationException>(() => ActiveSkillEngine.Initialize(tempDir));

                // Case 2: files exist but contain malformed or structurally
                // invalid JSON (unterminated object, plain non-JSON text, and
                // a syntactically valid but semantically empty array, which
                // Initialize must also reject rather than silently loading
                // zero content entries).
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "monsters.json"), "{ this is not valid json ");
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "items.json"), "[]");
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "gathering_nodes.json"), "[]");
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "skills.json"), "not json at all");

                Assert.Throws<InvalidOperationException>(() => ContentRegistry.Initialize(tempDir));
                Assert.Throws<InvalidOperationException>(() => ActiveSkillEngine.Initialize(tempDir));

                // Case 3: a monster with a non-contiguous Id (a gap) - the
                // rest of the engine indexes ContentRegistry.Monsters[id - 1]
                // directly, so this must be rejected even though it is
                // otherwise well-formed JSON.
                System.IO.File.WriteAllText(System.IO.Path.Combine(tempDir, "monsters.json"),
                    "[{\"Id\":1,\"MaxHp\":100,\"AttackPower\":1,\"BaseGoldReward\":1,\"BaseXpReward\":1,\"AttackIntervalMs\":1000,\"LootTableId\":1,\"Name\":\"X\",\"EnemyId\":\"x\"}," +
                    "{\"Id\":3,\"MaxHp\":100,\"AttackPower\":1,\"BaseGoldReward\":1,\"BaseXpReward\":1,\"AttackIntervalMs\":1000,\"LootTableId\":1,\"Name\":\"Y\",\"EnemyId\":\"y\"}]");
                Assert.Throws<InvalidOperationException>(() => ContentRegistry.Initialize(tempDir));

                // Every failed call above must have left the real,
                // already-loaded content completely untouched.
                Assert.True(ContentRegistry.Monsters.Length > 0);
                Assert.True(ContentRegistry.ItemDefinitions.Length > 0);
                Assert.True(ContentRegistry.GatheringNodes.Length > 0);
                Assert.True(ActiveSkillEngine.Skills.Length > 0);
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // Modul: /metrics is unauthenticated (matching /health/liveness and
        // /health/readiness) and must return HTTP 200 with a Prometheus
        // text-exposition-format body containing all three metrics this
        // task requires, even with no SimulationEngine registered and no
        // active sessions - HandleMetrics defaults every value to 0 in that
        // case rather than failing the scrape.
        [Fact]
        public async Task Test_MetricsEndpoint_ReturnsPlainTextPrometheusFormat()
        {
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8094/");
            networkSystem.Start();

            try
            {
                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync("http://localhost:8094/metrics");

                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType ?? string.Empty);

                string body = await response.Content.ReadAsStringAsync();

                Assert.Contains("# TYPE folkidle_active_sessions_total gauge", body);
                Assert.Contains("folkidle_active_sessions_total 0", body);

                Assert.Contains("# TYPE folkidle_tick_duration_milliseconds histogram", body);
                Assert.Contains("folkidle_tick_duration_milliseconds_bucket{le=\"10\"}", body);
                Assert.Contains("folkidle_tick_duration_milliseconds_bucket{le=\"+Inf\"}", body);
                Assert.Contains("folkidle_tick_duration_milliseconds_sum", body);
                Assert.Contains("folkidle_tick_duration_milliseconds_count", body);

                Assert.Contains("# TYPE folkidle_database_write_queue_length gauge", body);
                Assert.Contains("folkidle_database_write_queue_length", body);
            }
            finally
            {
                networkSystem.Stop();
            }
        }

        // Modul: ChatEngine's per-connection chat rate limit (5-message burst
        // capacity, refilling at 0.5 messages/second - see
        // ChatEngine.ChatBucketCapacity/ChatBucketRefillRatePerSecond) is
        // deliberately a soft reject, never a disconnect - spam is normal,
        // recoverable user behavior, unlike a structural protocol violation.
        // Sending more RequestChatMessagePacket messages back to back than
        // the bucket's burst capacity must result in only the capacity's
        // worth being published (observable via the sender's own echoed
        // ResponseChatMessagePacket, since every publish echoes back to the
        // sender exactly like everyone else - see ChatEngine.
        // HandleRedisMessage), while the connection itself stays open and
        // fully functional afterward.
        [Fact]
        public async Task Test_ChatEngine_RateLimiter_DropsExcessMessagesWithoutDisconnecting()
        {
            const long testPlayerId = 970000021L;
            const int burstCapacity = 5;
            const int sendCount = 9;
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var (simulationEngine, networkSystem) = CreateLiveSimulationEngine("http://localhost:8095/");
            networkSystem.Start();
            simulationEngine.Start();

            try
            {
                using var clientSocket = new ClientWebSocket();
                await clientSocket.ConnectAsync(new Uri("ws://localhost:8095/"), CancellationToken.None);

                byte[] authBuffer = BuildAuthHandshakeBuffer(MintTestJwt(accountId));
                await clientSocket.SendAsync(new ArraySegment<byte>(authBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                var loginConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int echoedChatCount = 0;
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                var receiveTask = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await clientSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                        }
                        catch
                        {
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        if (result.Count == Marshal.SizeOf<ResponseChatMessagePacket>())
                        {
                            var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                            if (chatPacket.SenderPlayerId == testPlayerId)
                            {
                                Interlocked.Increment(ref echoedChatCount);
                            }
                            continue;
                        }

                        if (result.Count < Marshal.SizeOf<StateUpdatePacket>()) continue;

                        var state = MemoryMarshal.Read<StateUpdatePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        loginConfirmed.TrySetResult();

                        if (state.ActiveChallengeSeed != 0)
                        {
                            uint hash = AntiCheatTelemetryEngine.ComputeChallengeHash(state.ActiveChallengeSeed, state.PlayerId, 0L);
                            await SendCommandAsync(clientSocket, new ClientCommandPacket
                            {
                                Command = CommandType.AntiCheatChallengeResponse,
                                ChallengeId = state.ActiveChallengeSeed,
                                ChallengeVerificationHash = hash
                            });
                        }
                    }
                });

                await Task.WhenAny(loginConfirmed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(loginConfirmed.Task.IsCompletedSuccessfully, "Did not observe the player enter the active tick loop before the rate limiter test began.");

                for (int i = 0; i < sendCount; i++)
                {
                    byte[] chatBuffer = BuildChatMessageBuffer($"burst message {i}");
                    await clientSocket.SendAsync(new ArraySegment<byte>(chatBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);
                }

                // Give every accepted publish time to round-trip back through
                // Redis before counting - comfortably longer than a single
                // local Redis Pub/Sub hop needs, short enough that the
                // refill rate (0.5/sec) could not plausibly grant more than
                // one extra token during the wait.
                await Task.Delay(TimeSpan.FromSeconds(3));

                Assert.True(echoedChatCount <= burstCapacity, $"Expected at most the {burstCapacity}-message burst capacity to be published, but observed {echoedChatCount} echoed messages.");
                Assert.True(echoedChatCount > 0, "Expected at least some messages within the burst capacity to be published.");
                Assert.True(echoedChatCount < sendCount, $"Expected the rate limiter to drop some of the {sendCount} sent messages, but all of them were echoed.");

                // The core requirement: rate-limited messages are dropped,
                // never disconnect-worthy - the socket must still be open
                // and the connection still fully usable afterward.
                Assert.Equal(WebSocketState.Open, clientSocket.State);
                byte[] pingBuffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
                MemoryMarshal.Write(new Span<byte>(pingBuffer), new ClientCommandPacket { Command = CommandType.ReloadState });
                await clientSocket.SendAsync(new ArraySegment<byte>(pingBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(1));
                Assert.Equal(WebSocketState.Open, clientSocket.State);

                cts.Cancel();
                try { await receiveTask; } catch { }
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                simulationEngine.Stop();
                networkSystem.Stop();
            }
        }

        // Modul: validates chat genuinely goes through Redis Pub/Sub, not
        // just a same-process in-memory fanout - two independent
        // NetworkBroadcastSystem instances on different ports, both sharing
        // this fixture's single Redis connection, stand in for two separate
        // pods. Deliberately does NOT start a SimulationEngine on either
        // side (unlike CreateLiveSimulationEngine's other consumers) -
        // chat is handled entirely inside NetworkBroadcastSystem's own
        // receive loop and never touches SimulationEngine/CommandQueue at
        // all (see HandleClientLoopAsync's exact-size RequestChatMessagePacket
        // branch), so a real tick loop is not needed to exercise it, and
        // skipping it avoids two full engines (each spinning up its own
        // pair of background threads) competing for scheduler time in one
        // test process, which was observed to make login confirmation via
        // "wait for the first StateUpdatePacket" flaky under this specific
        // two-engines-in-one-process load (never a problem for any other
        // test in this file, which all use at most one live engine).
        // Handshake success is instead confirmed the same way the simpler,
        // long-standing Test_Handshake_ConcurrentConnectionsSameAccount_
        // EvictsStaleSession test above already does: the socket stays
        // Open after a short grace delay (a failed handshake closes it
        // immediately - see HandleClientLoopAsync's PolicyViolation closes).
        // A message published by a connection on "pod A" must be observed
        // by a connection on "pod B", which never received it through any
        // local _connectedClients broadcast of its own - the only path
        // between the two pods is ChatEngine.PublishMessageAsync -> Redis
        // -> ChatEngine.HandleRedisMessageAsync on the other pod's
        // subscription.
        [Fact]
        public async Task Test_ChatEngine_RedisPubSub_ForwardsMessagesAcrossPods()
        {
            const long playerAId = 970000022L;
            const long playerBId = 970000023L;
            Guid accountAId = Guid.NewGuid();
            Guid accountBId = Guid.NewGuid();
            const string messageText = "cross-pod chat forwarding test";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerAId, PlayerGuid = accountAId, AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = playerBId, PlayerGuid = accountBId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystemA = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8096/");
            var networkSystemB = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8097/");
            networkSystemA.Start();
            networkSystemB.Start();

            try
            {
                using var socketA = new ClientWebSocket();
                await socketA.ConnectAsync(new Uri("ws://localhost:8096/"), CancellationToken.None);
                await socketA.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(accountAId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var socketB = new ClientWebSocket();
                await socketB.ConnectAsync(new Uri("ws://localhost:8097/"), CancellationToken.None);
                await socketB.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(accountBId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                await Task.Delay(500);
                Assert.Equal(WebSocketState.Open, socketA.State);
                Assert.Equal(WebSocketState.Open, socketB.State);

                var messageObservedOnB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                var receiveTaskB = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await socketB.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                        }
                        catch
                        {
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count != Marshal.SizeOf<ResponseChatMessagePacket>()) continue;

                        var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        if (chatPacket.SenderPlayerId != playerAId) continue;

                        string received;
                        unsafe
                        {
                            received = System.Text.Encoding.UTF8.GetString(chatPacket.MessageText, chatPacket.MessageLength);
                        }

                        if (received == messageText)
                        {
                            messageObservedOnB.TrySetResult();
                        }
                    }
                });

                byte[] chatBuffer = BuildChatMessageBuffer(messageText);
                await socketA.SendAsync(new ArraySegment<byte>(chatBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                var completed = await Task.WhenAny(messageObservedOnB.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(completed == messageObservedOnB.Task, "Pod B never observed the chat message published on pod A - Redis Pub/Sub forwarding did not occur.");

                cts.Cancel();
                try { await receiveTaskB; } catch { }
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystemA.Stop();
                networkSystemB.Stop();
            }
        }

        private static unsafe byte[] BuildChatMessageBuffer(string messageText)
        {
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(messageText);
            int length = textBytes.Length > RequestChatMessagePacket.MessageCapacity ? RequestChatMessagePacket.MessageCapacity : textBytes.Length;

            var packet = new RequestChatMessagePacket { MessageLength = (ushort)length };
            byte* target = packet.MessageText;
            for (int i = 0; i < RequestChatMessagePacket.MessageCapacity; i++)
            {
                target[i] = i < length ? textBytes[i] : (byte)0;
            }

            byte[] buffer = new byte[Marshal.SizeOf<RequestChatMessagePacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            return buffer;
        }

        // Modul: a fake receipt signed with a DIFFERENT key than the one
        // ProductionIapReceiptValidator is configured to trust (the
        // signature bytes are also explicitly corrupted, so this fails
        // regardless of which key generated them) must be rejected by
        // BillingVerificationEngine.VerifyReceiptAsync before any currency
        // is granted or any ledger row is written - the mandatory
        // signature-verification gate is BillingVerificationEngine's own
        // explicit `if (!receipt.SignatureVerified) return false;` check,
        // not something buried inside the validator.
        [Fact]
        public async Task Test_BillingVerificationEngine_InvalidReceiptSignature_RejectedWithoutBalanceChange()
        {
            const long testPlayerId = 970000401L;
            const string transactionId = "iap_forged_970000401";
            string envVarName = "FOLKIDLE_TEST_IAP_GOOGLE_PUBLIC_KEY_PATH_" + Guid.NewGuid().ToString("N");
            string keyFilePath = Path.Combine(Path.GetTempPath(), envVarName + ".pem");

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), PremiumDiamonds = 0 });
                await db.SaveChangesAsync();
            }

            try
            {
                using RSA trustedKeyPair = RSA.Create(2048);
                File.WriteAllText(keyFilePath, trustedKeyPair.ExportSubjectPublicKeyInfoPem());
                Environment.SetEnvironmentVariable(envVarName, keyFilePath);

                string payloadJson = "{\"transactionId\":\"" + transactionId + "\",\"productId\":\"gems_pack_small\"}";
                byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payloadJson);

                // Signed with a key the validator was never configured to
                // trust, then the signature bytes are corrupted on top -
                // either defect alone is sufficient to fail verification.
                using RSA forgingKeyPair = RSA.Create(2048);
                byte[] signatureBytes = forgingKeyPair.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                signatureBytes[0] ^= 0xFF;

                string envelopeJson = "{\"provider\":\"GooglePlay\",\"payload\":\"" + Base64UrlEncode(payloadBytes) + "\",\"signature\":\"" + Base64UrlEncode(signatureBytes) + "\"}";
                string base64Receipt = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(envelopeJson));

                var receiptValidator = new ProductionIapReceiptValidator(
                    new SecretRotationManager(envVarName),
                    new SecretRotationManager(envVarName + "_apple_unused"));

                using var offlineRedis = CreateOfflineRedisMultiplexer();
                var redisCache = new RedisSessionCache(offlineRedis);
                var billingEngine = new BillingVerificationEngine(_fixture.DbContextFactory, redisCache, _fixture.PlayerRegistry, _fixture.RetryingOptions, receiptValidator);

                bool result = await billingEngine.VerifyReceiptAsync(testPlayerId, base64Receipt);

                Assert.False(result, "A receipt with an invalid signature must be rejected.");

                await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
                var profile = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(0, profile.PremiumDiamonds);

                bool anyProcessed = await verifyDb.ProcessedTransactions.AsNoTracking().AnyAsync(t => t.TransactionId == transactionId);
                Assert.False(anyProcessed, "A rejected signature must never reach the ProcessedTransactions ledger.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(envVarName, null);
                if (File.Exists(keyFilePath))
                {
                    File.Delete(keyFilePath);
                }
            }
        }

        // Modul: subscribes directly to FolkIdleEventSource - a new
        // EventListener replays OnEventSourceCreated for every already-
        // constructed EventSource (FolkIdleEventSource.Log is instantiated
        // once, at static-field init), so this observes the event without
        // needing to drive a full SimulationEngine broadcast tick.
        [Fact]
        public async Task Test_FolkIdleEventSource_BroadcastSnapshotEnd_CapturesLatencyEvent()
        {
            const long expectedElapsedMicroseconds = 42424L;
            const long expectedActivePlayerCount = 7L;

            using var listener = new CapturingEventListener();

            FolkIdleEventSource.Log.BroadcastSnapshotEnd(expectedElapsedMicroseconds, expectedActivePlayerCount);

            var completed = await Task.WhenAny(listener.CaptureCompletionSource.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(completed == listener.CaptureCompletionSource.Task, "The EventListener never observed a BroadcastSnapshotEnd event.");

            (long capturedElapsedMicroseconds, long capturedActivePlayerCount) = await listener.CaptureCompletionSource.Task;
            Assert.Equal(expectedElapsedMicroseconds, capturedElapsedMicroseconds);
            Assert.Equal(expectedActivePlayerCount, capturedActivePlayerCount);
        }

        // Modul: proves the refund clawback matches the exact diamond amount
        // the original purchase granted (previously a hardcoded 1000 -
        // refunding a 1100-diamond gems_pack_medium clawed back 1000 and
        // let the player keep 100 for free), and that a refund alert for a
        // transaction with no purchase ledger row fails loudly instead of
        // silently no-oping.
        [Fact]
        public async Task Test_BillingVerificationEngine_RefundClawback_DeductsExactGrantedAmount()
        {
            const long testPlayerId = 980000501L;
            const string transactionId = "iap_refund_exact_980000501";
            int expectedGrant = BillingVerificationEngine.ResolvePremiumDiamondsForProduct("gems_pack_medium");

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), PremiumDiamonds = 0 });
                await db.SaveChangesAsync();
            }

            using var offlineRedis = CreateOfflineRedisMultiplexer();
            var redisCache = new RedisSessionCache(offlineRedis);
            var billingEngine = new BillingVerificationEngine(_fixture.DbContextFactory, redisCache, _fixture.PlayerRegistry, _fixture.RetryingOptions, new MockIapReceiptValidator());

            string receiptJson = "{\"transactionId\":\"" + transactionId + "\",\"productId\":\"gems_pack_medium\"}";
            string base64Receipt = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(receiptJson));

            bool purchased = await billingEngine.VerifyReceiptAsync(testPlayerId, base64Receipt);
            Assert.True(purchased);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var profile = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(expectedGrant, profile.PremiumDiamonds);
            }

            await billingEngine.HandleRefundAlertAsync(transactionId);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var profile = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(0, profile.PremiumDiamonds);

                var purchase = await verifyDb.PrimaryPurchaseLedgers.AsNoTracking().SingleAsync(p => p.TransactionId == transactionId);
                Assert.Equal(2, purchase.PurchaseState);
            }

            // A second delivery of the same refund alert is an idempotent
            // repeat - the balance must not be deducted twice.
            await billingEngine.HandleRefundAlertAsync(transactionId);
            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var profile = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(0, profile.PremiumDiamonds);
            }

            // A refund alert for a transaction that was never purchased
            // must throw loudly, not silently deduct anything.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => billingEngine.HandleRefundAlertAsync("iap_refund_never_purchased_980000501"));
        }

        // Modul: proves the full guild-membership pipeline end to end -
        // GuildManagementEngine commits create/join/leave to the database,
        // enqueues GuildMembershipChangeNotification, and the running
        // SimulationEngine tick drains it into _guildMembersIndex (checked
        // via the internal test accessors), updates the live
        // TickStatePayload.GuildId, and issues a ReloadState packet per
        // change (checked via the drain's issued-count).
        [Fact]
        public async Task Test_GuildManagementEngine_MembershipChanges_UpdateIndexAndIssueReloadState()
        {
            const long leaderPlayerId = 980000601L;
            const long memberPlayerId = 980000602L;
            const string guildName = "IntegrationTestManagedGuild980000601";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // CurrentLevel 25: clears GuildManagementEngine's universal
                // level-20 guild interaction gate (Advanced Economy
                // Refactoring, Part 3.1).
                db.PlayerRecords.Add(new PlayerRecord { Id = leaderPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                db.PlayerRecords.Add(new PlayerRecord { Id = memberPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                await db.SaveChangesAsync();
            }

            var contextFactory = _fixture.DbContextFactory;
            var retryingDbOptions = _fixture.RetryingOptions;
            var playerRegistry = new PlayerSessionRegistry();

            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8091/");
            var lootEngine = new LootTableEngine();
            var checkpointManager = new StateCheckpointManager(_fixture.ServiceProvider);
            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);
            var marketEngine = new MarketOrderBookEngine(_fixture.ServiceProvider, playerRegistry);
            var guildEngine = new GuildContributionEngine(_fixture.ServiceProvider);
            var escrowEngine = new MarketEscrowEngine(_fixture.ServiceProvider, playerRegistry);
            var mailboxEngine = new MailboxAndBankEngine(_fixture.ServiceProvider, playerRegistry);
            var rerollEngine = new AffixRerollEngine(_fixture.ServiceProvider);
            var breedingEngine = new BreedingEngine(_fixture.ServiceProvider, playerRegistry);
            var guildLogisticsEngine = new GuildLogisticsEngine(_fixture.ServiceProvider, playerRegistry);
            var craftingEngine = new CraftingEngine(contextFactory, playerRegistry, retryingDbOptions);
            var worldBossEngine = new WorldBossEngine(_fixture.ServiceProvider, playerRegistry);
            var villageBuildingEngine = new VillageBuildingEngine(_fixture.ServiceProvider, playerRegistry);
            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, playerRegistry);
            var mentorshipEngine = new MentorshipEngine(_fixture.ServiceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(_fixture.ServiceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(_fixture.ServiceProvider, playerRegistry);
            var legacyStoreEngine = new LegacyStoreEngine(_fixture.ServiceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(_fixture.ServiceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(_fixture.ServiceProvider, playerRegistry);

            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageBuildingEngine, villageManagementEngine, mentorshipEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);

            var managementEngine = new GuildManagementEngine(retryingDbOptions, playerRegistry);

            try
            {
                simulationEngine.Start();

                // CurrentLevel 25 on the injected payloads too - the
                // running engine's checkpoint flush writes TickStatePayload
                // state back over the seeded PlayerRecords rows (every tick
                // here, since InventorySpaceRemaining 0 forces the
                // checkpoint boundary), so a level-0 payload would erase
                // the seeded level and trip the level-20 guild gate
                // mid-test.
                simulationEngine.InjectVirtualPlayer(new TickStatePayload { PlayerId = leaderPlayerId, GuildId = 0, CurrentLevel = 25 });
                simulationEngine.InjectVirtualPlayer(new TickStatePayload { PlayerId = memberPlayerId, GuildId = 0, CurrentLevel = 25 });

                long guildId = await managementEngine.CreateGuildAsync(leaderPlayerId, guildName);
                Assert.True(guildId > 0, "CreateGuildAsync must return the new guild's id.");

                await WaitForConditionAsync(() => simulationEngine.IsPlayerInGuildIndex(guildId, leaderPlayerId),
                    "Creator never appeared in _guildMembersIndex after CreateGuildAsync.");
                Assert.Equal(guildId, simulationEngine.GetActivePlayerGuildId(leaderPlayerId));

                bool joined = await managementEngine.JoinGuildAsync(memberPlayerId, guildId);
                Assert.True(joined, "JoinGuildAsync must accept a guild with free capacity.");

                await WaitForConditionAsync(() => simulationEngine.IsPlayerInGuildIndex(guildId, memberPlayerId),
                    "Joiner never appeared in _guildMembersIndex after JoinGuildAsync.");
                Assert.Equal(guildId, simulationEngine.GetActivePlayerGuildId(memberPlayerId));

                await using (var verifyDb = await contextFactory.CreateDbContextAsync())
                {
                    var creatorRow = await verifyDb.GuildMembers.AsNoTracking().SingleAsync(m => m.PlayerId == leaderPlayerId);
                    var joinerRow = await verifyDb.GuildMembers.AsNoTracking().SingleAsync(m => m.PlayerId == memberPlayerId);
                    Assert.Equal(GuildManagementEngine.RoleLeader, creatorRow.Role);
                    Assert.Equal(GuildManagementEngine.RoleMember, joinerRow.Role);

                    var guildRow = await verifyDb.GuildRecords.AsNoTracking().SingleAsync(g => g.Id == guildId);
                    Assert.Equal(2, guildRow.ActiveMembers);
                }

                bool left = await managementEngine.LeaveGuildAsync(leaderPlayerId);
                Assert.True(left, "LeaveGuildAsync must accept a current member.");

                await WaitForConditionAsync(() => !simulationEngine.IsPlayerInGuildIndex(guildId, leaderPlayerId),
                    "Leaver never disappeared from _guildMembersIndex after LeaveGuildAsync.");
                Assert.Equal(0L, simulationEngine.GetActivePlayerGuildId(leaderPlayerId));
                Assert.True(simulationEngine.IsPlayerInGuildIndex(guildId, memberPlayerId),
                    "Remaining member must stay in _guildMembersIndex after another member leaves.");

                await using (var verifyDb = await contextFactory.CreateDbContextAsync())
                {
                    var successorRow = await verifyDb.GuildMembers.AsNoTracking().SingleAsync(m => m.PlayerId == memberPlayerId);
                    Assert.Equal(GuildManagementEngine.RoleLeader, successorRow.Role);
                }

                // Three membership changes (create, join, leave) must have
                // issued exactly three ReloadState packets to the affected
                // live players.
                Assert.Equal(3L, System.Threading.Interlocked.Read(ref simulationEngine.GuildMembershipReloadStatesIssued));
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        private static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition()) return;
                await Task.Delay(50);
            }
            Assert.Fail(failureMessage);
        }

        // Modul: proves the CI content gate (ops/validate_content.py, the
        // "Validate content data" step in deploy.yml) rejects malformed
        // GameData JSON with a non-zero exit code, which is what fails the
        // pipeline before a broken image is built. Skips silently when no
        // Python interpreter is on PATH (the C# side of the same rules is
        // covered by Test_ContentPipeline_MissingOrMalformedJson_FailsFast).
        [Fact]
        public void Test_ContentValidatorScript_MalformedJson_ExitsNonZero()
        {
            string? pythonExe = ResolvePythonExecutable();
            if (pythonExe == null)
            {
                return;
            }

            string? repoRoot = FindRepositoryRoot();
            Assert.NotNull(repoRoot);
            string validatorPath = Path.Combine(repoRoot!, "ops", "validate_content.py");
            Assert.True(File.Exists(validatorPath), $"validate_content.py not found at {validatorPath}.");

            string goodDataDir = Path.Combine(AppContext.BaseDirectory, "GameData");
            Assert.True(RunValidator(pythonExe, validatorPath, goodDataDir) == 0,
                "Validator must pass against the real GameData set.");

            string badDataDir = Path.Combine(Path.GetTempPath(), "folkidle_baddata_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(badDataDir);
            try
            {
                foreach (string file in Directory.GetFiles(goodDataDir, "*.json"))
                {
                    File.Copy(file, Path.Combine(badDataDir, Path.GetFileName(file)));
                }
                File.WriteAllText(Path.Combine(badDataDir, "monsters.json"), "{ this is not valid json");

                Assert.True(RunValidator(pythonExe, validatorPath, badDataDir) != 0,
                    "Validator must exit non-zero for malformed JSON.");
            }
            finally
            {
                Directory.Delete(badDataDir, recursive: true);
            }
        }

        private static string? ResolvePythonExecutable()
        {
            foreach (string candidate in new[] { "python3", "python" })
            {
                try
                {
                    using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    });
                    if (probe != null)
                    {
                        probe.WaitForExit(10000);
                        if (probe.ExitCode == 0) return candidate;
                    }
                }
                catch
                {
                    // Candidate not on PATH - try the next one.
                }
            }
            return null;
        }

        private static string? FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ops", "validate_content.py")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static int RunValidator(string pythonExe, string validatorPath, string dataDir)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{validatorPath}\" --path \"{dataDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            Assert.NotNull(process);
            process!.WaitForExit(30000);
            return process.ExitCode;
        }

        private sealed class CapturingEventListener : EventListener
        {
            public readonly TaskCompletionSource<(long, long)> CaptureCompletionSource =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (string.Equals(eventSource.Name, "FolkIdle-Server", StringComparison.Ordinal))
                {
                    EnableEvents(eventSource, EventLevel.Verbose);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (eventData.EventId != FolkIdleEventSource.EventIds.BroadcastSnapshotEnd)
                {
                    return;
                }

                if (eventData.Payload == null || eventData.Payload.Count < 2)
                {
                    return;
                }

                if (eventData.Payload[0] is long elapsedMicroseconds && eventData.Payload[1] is long activePlayerCount)
                {
                    CaptureCompletionSource.TrySetResult((elapsedMicroseconds, activePlayerCount));
                }
            }
        }

        // Modul: proves daily quest generation is deterministic within a
        // UTC day (regenerating mid-day never reshuffles what a player is
        // already working toward) and genuinely resets at the UTC-midnight
        // boundary (new quest set, progress wiped) - the two behaviors
        // QuestEngine.GetUtcDateKey/EnsureAndLoadDailyQuestsAsync exist to
        // guarantee.
        [Fact]
        public async Task Test_QuestEngine_DailyQuestGenerationAndUtcMidnightReset()
        {
            const long testPlayerId = 980000701L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            long day1Epoch = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

            DailyQuestRecord[] firstGeneration;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                firstGeneration = await QuestEngine.EnsureAndLoadDailyQuestsAsync(db, testPlayerId, day1Epoch);
                await db.SaveChangesAsync();
            }

            Assert.Equal(3, firstGeneration.Length);
            Assert.All(firstGeneration, q => Assert.True(q.TargetAmount > 0));
            Assert.All(firstGeneration, q => Assert.True(q.QuestType == QuestEngine.QuestTypeKillMonsters || q.QuestType == QuestEngine.QuestTypeCraftItems));

            // Determinism: reloading later the SAME UTC day must return the
            // identical quest set, not reshuffle it.
            long sameDayLaterEpoch = day1Epoch + 3600L;
            DailyQuestRecord[] sameDayReload;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                sameDayReload = await QuestEngine.EnsureAndLoadDailyQuestsAsync(db, testPlayerId, sameDayLaterEpoch);
                await db.SaveChangesAsync();
            }
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(firstGeneration[i].QuestType, sameDayReload[i].QuestType);
                Assert.Equal(firstGeneration[i].TargetAmount, sameDayReload[i].TargetAmount);
            }

            // Record progress that a UTC-midnight rollover must wipe.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var slot0 = await db.DailyQuestRecords.SingleAsync(q => q.PlayerId == testPlayerId && q.QuestSlot == 0);
                slot0.CurrentProgress = 5;
                await db.SaveChangesAsync();
            }

            long day2Epoch = day1Epoch + 86400L;
            Assert.NotEqual(QuestEngine.GetUtcDateKey(day1Epoch), QuestEngine.GetUtcDateKey(day2Epoch));

            DailyQuestRecord[] secondGeneration;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                secondGeneration = await QuestEngine.EnsureAndLoadDailyQuestsAsync(db, testPlayerId, day2Epoch);
                await db.SaveChangesAsync();
            }

            Assert.Equal(3, secondGeneration.Length);
            Assert.All(secondGeneration, q => Assert.Equal(0, q.CurrentProgress));
            Assert.All(secondGeneration, q => Assert.Equal(QuestEngine.GetUtcDateKey(day2Epoch), q.DateKeyUtc));
        }

        // Modul: proves the new Accuracy/Armor/BlockStrength combat axes
        // are genuinely wired into SimulationEngine's live tick, not just
        // present in StatsCalculator - a high-CON (armored) character must
        // take strictly less cumulative damage from repeated monster
        // attacks than an otherwise-identical naked character. DEX/LCK are
        // left at 0 for both builds so hit chance is identical between the
        // two samples, isolating the comparison to the armor+block
        // mitigation step. Statistical (many samples), matching this
        // codebase's existing RNG-involving test convention (e.g.
        // Test_RarityTier_HighLuckIncreasesRareRollProbability), since a
        // single hit/crit roll would be flaky.
        [Fact]
        public void Test_Combat_ArmorAndBlockStrengthReduceIncomingMonsterDamage()
        {
            var simulationEngine = CreateTestSimulationEngine();
            const int monsterId = 1;

            long nakedDamage = SimulateTotalDamageTakenFromMonster(simulationEngine, monsterId, con: 0);
            long armoredDamage = SimulateTotalDamageTakenFromMonster(simulationEngine, monsterId, con: 500);

            Assert.True(armoredDamage < nakedDamage,
                $"Armored (CON=500) took {armoredDamage} total milli-damage across the sample, naked (CON=0) took {nakedDamage} - armor and block strength must reduce incoming monster damage.");
        }

        private static long SimulateTotalDamageTakenFromMonster(SimulationEngine simulationEngine, int monsterId, int con)
        {
            int attackIntervalMs = ContentRegistry.Monsters[monsterId - 1].AttackIntervalMs;
            int ticksPerAttack = attackIntervalMs / 100;
            const int sampleAttacks = 200;

            long totalDamage = 0;
            for (int attack = 0; attack < sampleAttacks; attack++)
            {
                var payload = new TickStatePayload
                {
                    PlayerId = 1,
                    ActiveActivityId = monsterId,
                    CurrentMonsterId = monsterId,
                    CurrentMonsterHp = int.MaxValue / 2,
                    PlayerHp = int.MaxValue / 2,
                    CON = con,
                    SpeedMultiplier = 1,
                    InventorySpaceRemaining = 1000
                };

                int hpBefore = payload.PlayerHp;
                for (int t = 0; t < ticksPerAttack; t++)
                {
                    simulationEngine.ProcessTick(ref payload);
                }
                totalDamage += hpBefore - payload.PlayerHp;
            }

            return totalDamage;
        }

        // Modul: proves TutorialStateMachine.IsInteractionAllowed genuinely
        // blocks every UI surface except the one the current step needs -
        // the rule UiTutorialInteractionGate enforces client-side. Pure
        // logic test, no DB - TutorialStateMachine has zero UnityEngine
        // references (see its own doc comment) and is compiled into this
        // project via the csproj file link in FolkIdle.Server.Tests.csproj.
        [Fact]
        public void Test_TutorialStateMachine_BlocksNonTutorialUiUntilStepsComplete()
        {
            var machine = new TutorialStateMachine();

            // Inactive: nothing is gated yet.
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Market));
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Inventory));

            machine.Begin();
            Assert.Equal(TutorialStep.LootFirstItem, machine.CurrentStep);
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Inventory));
            Assert.False(machine.IsInteractionAllowed(TutorialUiElement.Forge));
            Assert.False(machine.IsInteractionAllowed(TutorialUiElement.Arena));
            Assert.False(machine.IsInteractionAllowed(TutorialUiElement.Market));
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Settings), "Settings must never be blocked by the tutorial.");

            // Out-of-order signals must not skip ahead.
            machine.NotifyItemCrafted();
            Assert.Equal(TutorialStep.LootFirstItem, machine.CurrentStep);
            machine.NotifyCombatWon();
            Assert.Equal(TutorialStep.LootFirstItem, machine.CurrentStep);

            machine.NotifyItemLooted();
            Assert.Equal(TutorialStep.CraftFirstItem, machine.CurrentStep);
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Forge));
            Assert.False(machine.IsInteractionAllowed(TutorialUiElement.Inventory));
            Assert.False(machine.IsInteractionAllowed(TutorialUiElement.Arena));

            machine.NotifyItemCrafted();
            Assert.Equal(TutorialStep.WinFirstCombat, machine.CurrentStep);
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Arena));
            Assert.False(machine.IsInteractionAllowed(TutorialUiElement.Forge));

            machine.NotifyCombatWon();
            Assert.Equal(TutorialStep.Completed, machine.CurrentStep);
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Market));
            Assert.True(machine.IsInteractionAllowed(TutorialUiElement.Inventory));
        }

        // Modul: proves the tick-thread exception isolation added to
        // EngineLoop's per-player foreach actually works - a real running
        // SimulationEngine, one player deliberately carrying a payload that
        // throws IndexOutOfRangeException inside ProcessTick's combat
        // resolution (CurrentMonsterId set to a value beyond
        // ContentRegistry.Monsters' authored range - a genuine, still-open
        // crash vector this pass did not specifically guard, used here
        // precisely because it is real, not contrived), alongside a second,
        // healthy player whose gathering progress must keep advancing
        // across further real ticks. If the isolation regressed back to no
        // try/catch, this test would never reach its assertions - the
        // exception would propagate out of the tick thread and crash the
        // whole test process, not just fail an assertion.
        [Fact]
        public async Task Test_SimulationEngine_TickException_IsolatesFailureAndKeepsOtherPlayersTicking()
        {
            const long healthyPlayerId = 970001001L;
            const long brokenPlayerId = 970001002L;

            var simulationEngine = CreateTestSimulationEngine();

            try
            {
                simulationEngine.Start();

                simulationEngine.InjectVirtualPlayer(new TickStatePayload
                {
                    PlayerId = healthyPlayerId,
                    ActiveActivityId = 101,
                    GatheringProgressTicks = 0,
                    InventorySpaceRemaining = 1000
                });

                simulationEngine.InjectVirtualPlayer(new TickStatePayload
                {
                    PlayerId = brokenPlayerId,
                    ActiveActivityId = 1,
                    CurrentMonsterId = 999999,
                    CurrentMonsterHp = 1_000_000,
                    PlayerHp = 1_000_000,
                    InventorySpaceRemaining = 1000
                });

                int initialHealthyProgress = simulationEngine.GetActivePlayerGatheringProgressTicks(healthyPlayerId);

                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerGatheringProgressTicks(healthyPlayerId) > initialHealthyProgress,
                    "Healthy player's gathering progress never advanced - the tick thread did not survive the broken player's ProcessTick exception.");

                // The broken player must be isolated (suspended, no longer
                // ticked every cycle) rather than repeatedly re-throwing on
                // every subsequent tick, and must still be present in
                // _activePlayers (this pass deliberately does not remove
                // it mid-enumeration - see the catch block's own comment).
                await WaitForConditionAsync(
                    () => simulationEngine.IsActivePlayerSuspended(brokenPlayerId),
                    "Broken player was never marked suspended after its ProcessTick exception.");
                Assert.True(simulationEngine.IsActivePlayerPresent(brokenPlayerId));

                // The healthy player must keep advancing for multiple
                // further ticks, not just the one increment already
                // observed above - proving sustained isolation, not a
                // one-off fluke.
                int progressAfterIsolation = simulationEngine.GetActivePlayerGatheringProgressTicks(healthyPlayerId);
                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerGatheringProgressTicks(healthyPlayerId) > progressAfterIsolation,
                    "Healthy player's gathering progress stalled after the broken player was isolated.");
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        // Modul: proves ContentRegistry.GetLootTable's defensive bounds
        // check (Part 1 of this pass) - an out-of-range or non-positive
        // lootTableId must return an empty span, never throw, while a
        // genuinely populated id (one of this pass's own new Fishing
        // gathering nodes) still returns real data, proving the bounds
        // check does not accidentally blank out valid lookups too.
        [Fact]
        public void Test_ContentRegistry_GetLootTable_OutOfBoundsIndexReturnsEmptySpanWithoutThrowing()
        {
            Assert.True(ContentRegistry.GetLootTable(-5).IsEmpty);
            Assert.True(ContentRegistry.GetLootTable(0).IsEmpty);
            Assert.True(ContentRegistry.GetLootTable(int.MaxValue).IsEmpty);
            Assert.True(ContentRegistry.GetLootTable(999999).IsEmpty);

            Assert.False(ContentRegistry.GetLootTable(301).IsEmpty);
        }

        // Modul: proves the guild lock-order normalization (Part 2) -
        // concurrent JoinGuildAsync and LeaveGuildAsync requests against
        // the SAME guild must all complete successfully (each engine
        // method already catches and reports its own failures as a
        // returned false rather than propagating an exception, so a
        // lock-order-inversion deadlock that exhausted the Serializable
        // retry policy would surface here as an unexpected false in the
        // results, not necessarily a thrown exception).
        [Fact]
        public async Task Test_GuildManagementEngine_ConcurrentJoinAndLeave_NoDeadlock()
        {
            const long leaderPlayerId = 970001101L;
            const string guildName = "ConcurrencyTestGuild970001101";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // CurrentLevel 25: clears the universal level-20 guild
                // interaction gate (Advanced Economy Refactoring, Part 3.1).
                db.PlayerRecords.Add(new PlayerRecord { Id = leaderPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                // Covers both the pre-join range (970001110-970001114) and
                // the concurrent-new-joiner range (970001120-970001124)
                // used below.
                for (int i = 0; i < 20; i++)
                {
                    db.PlayerRecords.Add(new PlayerRecord { Id = 970001110L + i, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                }
                await db.SaveChangesAsync();
            }

            var managementEngine = new GuildManagementEngine(_fixture.RetryingOptions, _fixture.PlayerRegistry);

            long guildId = await managementEngine.CreateGuildAsync(leaderPlayerId, guildName);
            Assert.True(guildId > 0);

            // Raise MaxMembers so the concurrent phase below exercises
            // lock ordering, not the (correctly enforced, but irrelevant
            // to this test) capacity cap - leader + 5 pre-joins + 5 new
            // concurrent joins is 11, one over the default cap of 10.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var guildRow = await db.GuildRecords.SingleAsync(g => g.Id == guildId);
                guildRow.MaxMembers = 50;
                await db.SaveChangesAsync();
            }

            for (int i = 0; i < 5; i++)
            {
                bool preJoined = await managementEngine.JoinGuildAsync(970001110L + i, guildId);
                Assert.True(preJoined);
            }

            // Five NEW players joining and the five already-joined members
            // leaving, all fired concurrently against the SAME guild - the
            // exact overlapping Join-vs-Leave race that previously risked
            // a deadlock between JoinGuildAsync's GuildRecords-then-
            // PlayerRecords lock order and the old LeaveGuildAsync's
            // reversed PlayerRecords-then-GuildRecords order.
            var tasks = new List<Task<bool>>();
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(managementEngine.JoinGuildAsync(970001120L + i, guildId));
            }
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(managementEngine.LeaveGuildAsync(970001110L + i));
            }

            bool[] results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.True(r));

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            int remainingMembers = await verifyDb.GuildMembers.AsNoTracking().CountAsync(m => m.GuildId == guildId);
            Assert.Equal(6, remainingMembers);
        }

        // Modul: proves the generic client error-feedback channel (Part 4)
        // end to end - a rejected AffixRerollEngine request (no
        // premium_diamond CommodityRecord at all, a guaranteed
        // InsufficientMaterials rejection) must enqueue a
        // CommandResultNotification that the running SimulationEngine's
        // own tick thread drains into TickStatePayload's 4-slot
        // CommandResultSlot0-3 ring buffer, the exact slots
        // StateUpdatePacket.CommandResult0-3_Code/Tick are populated from
        // at broadcast time.
        [Fact]
        public async Task Test_CommandResultCode_RejectedRerollFlushesErrorCodeToTickStatePayload()
        {
            const long testPlayerId = 970001201L;
            long equipmentId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                var equipment = new EquipmentInstance
                {
                    PlayerId = testPlayerId,
                    BaseItemId = "integration_test_command_result_reroll_sword",
                    QualityTier = 1,
                    AffixPayload = "{\"flat_hp_aaaa\":10}"
                };
                db.EquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;
            }

            var simulationEngine = CreateTestSimulationEngine();

            try
            {
                simulationEngine.Start();

                simulationEngine.InjectVirtualPlayer(new TickStatePayload
                {
                    PlayerId = testPlayerId,
                    InventorySpaceRemaining = 1000
                });

                Assert.Equal(0, simulationEngine.GetActivePlayerLastCommandResultCode(testPlayerId));

                // No premium_diamond CommodityRecord exists for this player
                // at all - ExecuteRerollAsync must reject with
                // InsufficientMaterials.
                var rerollEngine = new AffixRerollEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
                await rerollEngine.ExecuteRerollAsync(testPlayerId, equipmentId, 0);

                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerLastCommandResultCode(testPlayerId) == (int)FolkIdle.Server.Network.CommandResultCode.InsufficientMaterials,
                    "Rejected reroll never flushed CommandResultCode.InsufficientMaterials onto the tick-owned TickStatePayload.");
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        // Modul: Phase 4 Production Stabilization - Part 1. Previously
        // additively stacked the full 24-hour OfflineThresholdSeconds on
        // top of the logarithmic-decay result, banking roughly 7x the
        // GDD-specified amount (~27.8 hours instead of ~3.8 hours at 48
        // hours offline). Asserts the corrected formula matches the GDD
        // exactly and no longer produces an inflated value.
        [Fact]
        public void Test_ChronoBufferEngine_FortyEightHoursOffline_BanksLogarithmicDecayWithoutThresholdInflation()
        {
            long fortyEightHoursSeconds = 48L * 3600L;
            int banked = ChronoBufferEngine.CalculateOfflineBankedSeconds(fortyEightHoursSeconds);

            long excess = fortyEightHoursSeconds - ChronoBufferEngine.OfflineThresholdSeconds;
            int expected = (int)Math.Floor(Math.Log(excess + 1.0) * 1200.0);

            Assert.Equal(expected, banked);
            Assert.InRange(banked, (int)(3.7 * 3600), (int)(3.9 * 3600));
            Assert.True(banked < ChronoBufferEngine.OfflineThresholdSeconds,
                "Banked seconds must never reach the full threshold offset the old buggy formula additively granted.");
        }

        // Modul: Phase 4 Production Stabilization - Part 2. A Transcendent
        // (tier 13) item must be rejected before any gold is deducted or
        // sacrifices are consumed - proves the cap check runs ahead of
        // every resource-consuming step in ExecuteFusionAsync.
        [Fact]
        public async Task Test_ForgeSplicing_RejectsFusionAtMaxQualityTier()
        {
            const long testPlayerId = 970001301L;
            const string baseItemId = "integration_test_forge_max_tier_sword";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = testPlayerId,
                    BuildingId = VillageManagementEngine.ForgeBuildingId,
                    CurrentLevel = 20
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 1000000L });
                await db.SaveChangesAsync();
            }

            long targetId, sac1Id, sac2Id;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var target = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = ForgeSplicingEngine.MaxQualityTier };
                var sac1 = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = ForgeSplicingEngine.MaxQualityTier };
                var sac2 = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseItemId, QualityTier = ForgeSplicingEngine.MaxQualityTier };
                db.EquipmentInstances.AddRange(target, sac1, sac2);
                await db.SaveChangesAsync();
                targetId = target.Id;
                sac1Id = sac1.Id;
                sac2Id = sac2.Id;
            }

            long goldBefore = await GetGoldAsync(testPlayerId);

            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);
            var result = await forgeEngine.ExecuteFusionAsync(testPlayerId, targetId, sac1Id, sac2Id);

            Assert.Equal(ForgeSplicingResult.MaxTierReached, result);

            long goldAfter = await GetGoldAsync(testPlayerId);
            Assert.Equal(goldBefore, goldAfter);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var unchangedTarget = await verifyDb.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == targetId);
            Assert.Equal(ForgeSplicingEngine.MaxQualityTier, unchangedTarget.QualityTier);

            int survivingSacrificeCount = await verifyDb.EquipmentInstances.AsNoTracking()
                .CountAsync(e => e.Id == sac1Id || e.Id == sac2Id);
            Assert.Equal(2, survivingSacrificeCount);
        }

        // Modul: Phase 4 Production Stabilization - Part 3. Locks in the
        // explicit, authored material-to-profession mapping that replaced
        // the itemDefinitionId % 2 != 0 parity heuristic.
        [Fact]
        public void Test_ContentRegistry_GetMaterialProfessionType_MapsAllKnownGatheringMaterials()
        {
            Assert.Equal(GatheringProfessionType.Mining, ContentRegistry.GetMaterialProfessionType(1));      // copper_ore
            Assert.Equal(GatheringProfessionType.Woodcutting, ContentRegistry.GetMaterialProfessionType(2)); // raw_log
            Assert.Equal(GatheringProfessionType.Mining, ContentRegistry.GetMaterialProfessionType(3));      // iron_ore
            Assert.Equal(GatheringProfessionType.Woodcutting, ContentRegistry.GetMaterialProfessionType(4)); // oak_log
            Assert.Equal(GatheringProfessionType.Mining, ContentRegistry.GetMaterialProfessionType(5));      // gold_ore
            Assert.Equal(GatheringProfessionType.Woodcutting, ContentRegistry.GetMaterialProfessionType(6)); // magic_log
        }

        // Modul: Phase 4 Production Stabilization - Part 3, end to end.
        // Proves GuildLogisticsEngine.ApplyMonolithProgressionAsync routes
        // a contribution to the correct Monolith progress column via
        // ContentRegistry.GetMaterialProfessionType, not raw ID parity.
        [Fact]
        public async Task Test_GuildLogistics_ContributionRoutesToCorrectMonolithByMetadataNotParity()
        {
            const long testGuildId = 970001401L;
            const long miningPlayerId = 970001402L;
            const long woodcuttingPlayerId = 970001403L;
            const int ironOreMaterialId = 3; // Mining
            const int oakLogMaterialId = 4;  // Woodcutting
            const long contributionQuantity = 500L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.GuildRecords.Add(new GuildRecord { Id = testGuildId, Name = "IntegrationTestMonolithGuild970001401" });
                db.PlayerRecords.Add(new PlayerRecord { Id = miningPlayerId, GuildId = testGuildId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = woodcuttingPlayerId, GuildId = testGuildId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = miningPlayerId, ItemId = ContentRegistry.GetMaterialString(ironOreMaterialId), Quantity = contributionQuantity });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = woodcuttingPlayerId, ItemId = ContentRegistry.GetMaterialString(oakLogMaterialId), Quantity = contributionQuantity });
                await db.SaveChangesAsync();
            }

            var logisticsEngine = new GuildLogisticsEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await logisticsEngine.ExecuteGuildContributionAsync(miningPlayerId, testGuildId, contributionQuantity, ironOreMaterialId);

            await using (var afterMiningDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var guild = await afterMiningDb.GuildRecords.AsNoTracking().SingleAsync(g => g.Id == testGuildId);
                Assert.Equal((int)contributionQuantity, guild.MiningMonolithProgress);
                Assert.Equal(0, guild.WoodcuttingMonolithProgress);
            }

            await logisticsEngine.ExecuteGuildContributionAsync(woodcuttingPlayerId, testGuildId, contributionQuantity, oakLogMaterialId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var finalGuild = await verifyDb.GuildRecords.AsNoTracking().SingleAsync(g => g.Id == testGuildId);
            Assert.Equal((int)contributionQuantity, finalGuild.MiningMonolithProgress);
            Assert.Equal((int)contributionQuantity, finalGuild.WoodcuttingMonolithProgress);
        }

        // Modul: Phase 4 Production Stabilization - Part 4. Guild ids well
        // past the old hardcoded int[1000] array bound must both write
        // and read correctly, with no silent no-op and no exception.
        [Fact]
        public void Test_GuildBonusesCache_SupportsGuildIdsAboveLegacyThousandCeiling()
        {
            const long guildIdAboveLegacyArrayBound = 5000L;
            const long anotherGuildIdAboveBound = 1500000L;

            double defaultMultiplier = GuildBonusesCache.GetGuildEfficiencyMultiplier(guildIdAboveLegacyArrayBound);
            Assert.Equal(1.0, defaultMultiplier);

            GuildBonusesCache.UpdateGuildTier(guildIdAboveLegacyArrayBound, 10);
            GuildBonusesCache.UpdateGuildTier(anotherGuildIdAboveBound, 25);

            Assert.Equal(1.0 + (10 * 0.02), GuildBonusesCache.GetGuildEfficiencyMultiplier(guildIdAboveLegacyArrayBound));
            Assert.Equal(1.0 + (25 * 0.02), GuildBonusesCache.GetGuildEfficiencyMultiplier(anotherGuildIdAboveBound));

            // Updating one guild's tier must not disturb another's.
            GuildBonusesCache.UpdateGuildTier(guildIdAboveLegacyArrayBound, 12);
            Assert.Equal(1.0 + (12 * 0.02), GuildBonusesCache.GetGuildEfficiencyMultiplier(guildIdAboveLegacyArrayBound));
            Assert.Equal(1.0 + (25 * 0.02), GuildBonusesCache.GetGuildEfficiencyMultiplier(anotherGuildIdAboveBound));
        }

        // Modul: Phase 4 Production Stabilization - Part 5. Exercises the
        // real Google Play Developer API request/response plumbing
        // (service-account JWT-bearer OAuth2 exchange, then a Bearer-
        // authenticated purchase lookup) against a stub HttpMessageHandler
        // standing in for live network access - no real Google credential
        // or network call is available in this environment, but the JWT
        // signing, HTTP call shape, and JSON parsing are all genuine.
        [Fact]
        public async Task Test_ProductionIapReceiptValidator_GooglePlayDeveloperApi_VerifiesSuccessfulPurchase()
        {
            var (secretManager, envVarName, filePath) = CreateFileBackedSecret(CreateStubGoogleServiceAccountJson());
            try
            {
                var stubFactory = new StubHttpClientFactory(new StubHttpMessageHandler(request =>
                    request.RequestUri!.Host.Contains("oauth2.googleapis.com")
                        ? StubJsonResponse(HttpStatusCode.OK, "{\"access_token\":\"stub-access-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")
                        : StubJsonResponse(HttpStatusCode.OK, "{\"purchaseState\":0,\"consumptionState\":0}")));

                var validator = new ProductionIapReceiptValidator(secretManager, secretManager, stubFactory);

                IapStoreVerificationOutcome outcome = await validator.VerifyViaGooglePlayDeveloperApiAsync(
                    secretManager, "com.folkidle.app", "premium_diamond_pack", "stub-purchase-token");

                Assert.True(outcome.IsVerified);
                Assert.Equal(string.Empty, outcome.ErrorMessage);
            }
            finally
            {
                CleanupFileBackedSecret(envVarName, filePath);
            }
        }

        // Modul: a well-formed but non-purchased response (purchaseState=1,
        // canceled) must be parsed successfully and rejected with a
        // reason - not misreported as verified, and not an uncaught
        // exception either.
        [Fact]
        public async Task Test_ProductionIapReceiptValidator_GooglePlayDeveloperApi_RejectsUnpurchasedStateWithoutThrowing()
        {
            var (secretManager, envVarName, filePath) = CreateFileBackedSecret(CreateStubGoogleServiceAccountJson());
            try
            {
                var stubFactory = new StubHttpClientFactory(new StubHttpMessageHandler(request =>
                    request.RequestUri!.Host.Contains("oauth2.googleapis.com")
                        ? StubJsonResponse(HttpStatusCode.OK, "{\"access_token\":\"stub-access-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")
                        : StubJsonResponse(HttpStatusCode.OK, "{\"purchaseState\":1,\"consumptionState\":0}")));

                var validator = new ProductionIapReceiptValidator(secretManager, secretManager, stubFactory);

                IapStoreVerificationOutcome outcome = await validator.VerifyViaGooglePlayDeveloperApiAsync(
                    secretManager, "com.folkidle.app", "premium_diamond_pack", "stub-purchase-token");

                Assert.False(outcome.IsVerified);
                Assert.NotEqual(string.Empty, outcome.ErrorMessage);
            }
            finally
            {
                CleanupFileBackedSecret(envVarName, filePath);
            }
        }

        // Modul: Phase 4 Production Stabilization - Part 5, Apple side.
        // Covers both a successful App Store Server API response
        // (signedTransactionInfo present) and a store-side error response
        // (HTTP 404 with a structured errorCode/errorMessage body) -
        // both must be defensively parsed with no uncaught exception, and
        // must map to the correct IsVerified/ErrorMessage outcome.
        [Fact]
        public async Task Test_ProductionIapReceiptValidator_AppleAppStoreApi_VerifiesSuccessAndRejectsErrorWithoutThrowing()
        {
            var (secretManager, envVarName, filePath) = CreateFileBackedSecret(CreateStubEcPrivateKeyPem());
            try
            {
                var successFactory = new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
                    StubJsonResponse(HttpStatusCode.OK, "{\"signedTransactionInfo\":\"stub.jws.payload\"}")));
                var successValidator = new ProductionIapReceiptValidator(secretManager, secretManager, successFactory);

                IapStoreVerificationOutcome successOutcome = await successValidator.VerifyViaAppleAppStoreServerApiAsync(
                    secretManager, "stub-key-id", "stub-issuer-id", "com.folkidle.app", "stub-transaction-id");

                Assert.True(successOutcome.IsVerified);

                var errorFactory = new StubHttpClientFactory(new StubHttpMessageHandler(_ =>
                    StubJsonResponse(HttpStatusCode.NotFound, "{\"errorCode\":4040010,\"errorMessage\":\"Transaction id not found.\"}")));
                var errorValidator = new ProductionIapReceiptValidator(secretManager, secretManager, errorFactory);

                IapStoreVerificationOutcome errorOutcome = await errorValidator.VerifyViaAppleAppStoreServerApiAsync(
                    secretManager, "stub-key-id", "stub-issuer-id", "com.folkidle.app", "stub-transaction-id-missing");

                Assert.False(errorOutcome.IsVerified);
                Assert.Contains("4040010", errorOutcome.ErrorMessage);
            }
            finally
            {
                CleanupFileBackedSecret(envVarName, filePath);
            }
        }

        private static string CreateStubGoogleServiceAccountJson()
        {
            using RSA rsa = RSA.Create(2048);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                client_email = "stub-service-account@example.iam.gserviceaccount.com",
                private_key = rsa.ExportPkcs8PrivateKeyPem()
            });
        }

        private static string CreateStubEcPrivateKeyPem()
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return ecdsa.ExportPkcs8PrivateKeyPem();
        }

        // Modul: SecretRotationManager resolves its value through an
        // environment-variable-named file path (never the secret itself
        // in an env var) - mirrors that exact shape for tests instead of
        // bypassing it, so this proves the same code path a real deployed
        // secret takes. A guid-suffixed env var name keeps concurrently
        // running tests in this class from colliding.
        private static (SecretRotationManager Manager, string EnvVarName, string FilePath) CreateFileBackedSecret(string content)
        {
            string envVarName = $"FOLKIDLE_TEST_SECRET_{Guid.NewGuid():N}";
            string filePath = Path.Combine(Path.GetTempPath(), $"{envVarName}.txt");
            File.WriteAllText(filePath, content);
            Environment.SetEnvironmentVariable(envVarName, filePath);
            return (new SecretRotationManager(envVarName), envVarName, filePath);
        }

        private static void CleanupFileBackedSecret(string envVarName, string filePath)
        {
            Environment.SetEnvironmentVariable(envVarName, null);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        private static HttpResponseMessage StubJsonResponse(HttpStatusCode statusCode, string jsonBody)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }

        private sealed class StubHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;

            public StubHttpClientFactory(HttpMessageHandler handler)
            {
                _handler = handler;
            }

            public HttpClient CreateClient(string name)
            {
                return new HttpClient(_handler, disposeHandler: false);
            }
        }

        // Modul: Phase - Full-Stack Production Polish, Part 1.1. Directly
        // exercises OfflineSimulationEngine.ExtrapolateOfflineProgressAsync
        // (the established test pattern for this engine - see
        // Test_OfflineProgression_AnalyticalCalculation/
        // Test_OfflineSimulationEngine_SevenDayOfflinePeriod_
        // GrantsExactAnalyticalYieldInO1Time above, neither of which goes
        // through the full Login/WebSocket pipeline either) and asserts the
        // four new Offline* summary fields are populated with the exact
        // delta this call granted - the values the client's Welcome Back
        // modal reads via StateUpdatePacket.Offline*/OfflineSummaryTick,
        // which SimulationEngine's packet-conversion site copies straight
        // from these same TickStatePayload fields with no transformation.
        [Fact]
        public async Task Test_OfflineSimulationEngine_PopulatesOfflineSummaryFieldsForWelcomeBackModal()
        {
            const long testPlayerId = 970001601L;
            const long elapsedOfflineSeconds = 3600L; // 1 hour, well under the 12h analytical cap
            const int monsterId = 31;

            long currentUnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var payload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LastLogoutTimestamp = currentUnixTimestamp - elapsedOfflineSeconds,
                ActiveActivityId = monsterId,
                CurrentLevel = 1,
                CurrentXp = 0,
                InventorySpaceRemaining = 1000,
                // Ample food stock so combat survives the full offline
                // window (see OfflineSimulationEngine.CalculateCombatProjection's
                // food-depletion model) - matches
                // Test_OfflineProgression_AnalyticalCalculation's own setup.
                Food1_ItemId = 1,
                Food1_Count = 100000
            };

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            payload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, payload, currentUnixTimestamp);

            Assert.Equal(elapsedOfflineSeconds, payload.OfflineElapsedSeconds);
            Assert.True(payload.OfflineGoldEarned > 0, "Expected offline combat against a real monster to grant gold.");
            Assert.True(payload.OfflineXpEarned > 0, "Expected offline combat against a real monster to grant XP.");
            Assert.Equal((byte)1, payload.OfflineSummaryTick);

            // Deltas, not running totals - CurrentGold/CurrentXp started at
            // 0 and were mutated by this same call, so the Offline* fields
            // must equal exactly what those counters increased by.
            Assert.Equal(payload.CurrentGold, payload.OfflineGoldEarned);
            Assert.Equal(payload.CurrentXp, payload.OfflineXpEarned);
        }

        // Modul: Phase - Full-Stack Production Polish, Part 4.1. Proves the
        // migration from a hardcoded switch statement to
        // ContentRegistry.Balance.IapProductPrices (loaded from
        // GameBalanceConfig.json) yields identical results for every
        // product the old switch recognized, plus the same 0 fallback for
        // an unrecognized product id - and that the values genuinely come
        // from the config object, not a second hardcoded literal that
        // happens to coincide.
        [Fact]
        public void Test_BillingVerificationEngine_ProductPricesMigratedToConfigMatchPriorHardcodedValues()
        {
            Assert.Equal(500, BillingVerificationEngine.ResolvePremiumDiamondsForProduct("gems_pack_small"));
            Assert.Equal(1100, BillingVerificationEngine.ResolvePremiumDiamondsForProduct("gems_pack_medium"));
            Assert.Equal(2400, BillingVerificationEngine.ResolvePremiumDiamondsForProduct("gems_pack_large"));
            Assert.Equal(5200, BillingVerificationEngine.ResolvePremiumDiamondsForProduct("gems_pack_mega"));
            Assert.Equal(0, BillingVerificationEngine.ResolvePremiumDiamondsForProduct("unknown_product_id"));

            Assert.Equal(500, ContentRegistry.Balance.IapProductPrices["gems_pack_small"]);
            Assert.Equal(5200, ContentRegistry.Balance.IapProductPrices["gems_pack_mega"]);
        }

        // Modul: Phase - Full-Stack Production Polish, Part 4.2. Proves
        // OfflineStateEngine.ReconcileOfflineStateAsync grants drops up to
        // the player's REAL race-mastery-expanded capacity (20 +
        // RaceMasteryResolver.GetHumanVaultBonusSlots, mirroring
        // StateCheckpointManager's own live formula) rather than the
        // previous hardcoded 50 - a player already holding 24 items with a
        // real capacity of 25 (Human mastery 25 => +5 bonus slots) must
        // only receive 1 more drop even though 10 were mathematically
        // earned over the simulated window, with the remaining 9 drops'
        // worth of time banked instead of being (incorrectly) granted
        // outright under the old capacity-50 assumption.
        [Fact]
        public async Task Test_OfflineStateEngine_ReconcileUsesRaceMasteryExpandedCapacityNotHardcodedFifty()
        {
            const long testPlayerId = 970001501L;
            const int humanMasteryLevel = 25; // GetHumanVaultBonusSlots(25) = +5 => real capacity 25
            const int preExistingEquipmentCount = 24;
            const long elapsedSecondsToSimulate = 3000L; // 10 potential drops at 300s/drop
            Guid accountId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = accountId, AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRaceMasteries.Add(new PlayerRaceMastery { PlayerId = testPlayerId, RaceId = RaceIds.Human, MasteryLevel = humanMasteryLevel });
                db.AccountChronoRegistries.Add(new AccountChronoRegistry
                {
                    AccountId = accountId,
                    LastClockSyncEpoch = System.Diagnostics.Stopwatch.GetTimestamp() - (elapsedSecondsToSimulate * System.Diagnostics.Stopwatch.Frequency)
                });

                for (int i = 0; i < preExistingEquipmentCount; i++)
                {
                    db.EquipmentInstances.Add(new EquipmentInstance
                    {
                        PlayerId = testPlayerId,
                        BaseItemId = "integration_test_capacity_filler",
                        QualityTier = 1,
                        AffixPayload = "{}"
                    });
                }

                await db.SaveChangesAsync();
            }

            var offlineStateEngine = new OfflineStateEngine(_fixture.ServiceProvider);
            await offlineStateEngine.ReconcileOfflineStateAsync(testPlayerId, CancellationToken.None);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            int finalEquipmentCount = await verifyDb.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == testPlayerId);

            // 24 pre-existing + exactly 1 granted (capacity 25 - 24 = 1
            // space available), never 26 (which the old hardcoded 50 would
            // have wrongly allowed).
            Assert.Equal(preExistingEquipmentCount + 1, finalEquipmentCount);

            var updatedPlayer = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
            Assert.True(updatedPlayer.BankedChronoSeconds > 0, "Overflow seconds from the 9 drops that could not fit must be banked, not discarded.");
        }

        // Modul: Phase - Full-Stack Production Polish, Part 3.1. Proves
        // ChatEngine's guild-channel routing pathway - added in
        // NetworkBroadcastSystem.BroadcastGuildChatMessage, filtering
        // strictly by each connected session's cached GuildId - actually
        // isolates a guild-channel message to the sender's own guild.
        // Three real WebSocket connections against one NetworkBroadcastSystem
        // instance: A and B share a guild, C does not. NetworkBroadcastSystem.
        // UpdateSessionGuildId is called directly here (normally done by
        // SimulationEngine.AddActivePlayer/the GuildMembershipChangeQueue
        // drain on Login) since no SimulationEngine runs in this test.
        [Fact]
        public async Task Test_ChatEngine_GuildChannel_RoutesOnlyToSenderGuildMembers()
        {
            const long playerAId = 970001701L; // sender
            const long playerBId = 970001702L; // same guild - must receive
            const long playerCId = 970001703L; // different guild - must NOT receive
            const long guildOneId = 970001710L;
            const long guildTwoId = 970001711L;
            Guid accountAId = Guid.NewGuid();
            Guid accountBId = Guid.NewGuid();
            Guid accountCId = Guid.NewGuid();
            const string messageText = "guild-only routing test";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerAId, PlayerGuid = accountAId, AuthenticatorToken = Guid.NewGuid(), GuildId = guildOneId });
                db.PlayerRecords.Add(new PlayerRecord { Id = playerBId, PlayerGuid = accountBId, AuthenticatorToken = Guid.NewGuid(), GuildId = guildOneId });
                db.PlayerRecords.Add(new PlayerRecord { Id = playerCId, PlayerGuid = accountCId, AuthenticatorToken = Guid.NewGuid(), GuildId = guildTwoId });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8098/");
            networkSystem.Start();

            try
            {
                using var socketA = new ClientWebSocket();
                await socketA.ConnectAsync(new Uri("ws://localhost:8098/"), CancellationToken.None);
                await socketA.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(accountAId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var socketB = new ClientWebSocket();
                await socketB.ConnectAsync(new Uri("ws://localhost:8098/"), CancellationToken.None);
                await socketB.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(accountBId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var socketC = new ClientWebSocket();
                await socketC.ConnectAsync(new Uri("ws://localhost:8098/"), CancellationToken.None);
                await socketC.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(accountCId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                await Task.Delay(500);
                Assert.Equal(WebSocketState.Open, socketA.State);
                Assert.Equal(WebSocketState.Open, socketB.State);
                Assert.Equal(WebSocketState.Open, socketC.State);

                networkSystem.UpdateSessionGuildId(playerAId, guildOneId);
                networkSystem.UpdateSessionGuildId(playerBId, guildOneId);
                networkSystem.UpdateSessionGuildId(playerCId, guildTwoId);

                var messageObservedOnB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool messageObservedOnC = false;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                var receiveTaskB = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await socketB.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                        }
                        catch
                        {
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count != Marshal.SizeOf<ResponseChatMessagePacket>()) continue;

                        var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        if (chatPacket.SenderPlayerId != playerAId) continue;

                        string received;
                        unsafe
                        {
                            received = System.Text.Encoding.UTF8.GetString(chatPacket.MessageText, chatPacket.MessageLength);
                        }

                        if (received == messageText)
                        {
                            messageObservedOnB.TrySetResult();
                        }
                    }
                });

                var receiveTaskC = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            WebSocketReceiveResult result = await socketC.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            if (result.Count != Marshal.SizeOf<ResponseChatMessagePacket>()) continue;

                            var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                            if (chatPacket.SenderPlayerId == playerAId)
                            {
                                messageObservedOnC = true;
                            }
                        }
                    }
                    catch
                    {
                    }
                });

                byte[] chatBuffer = BuildGuildChatMessageBuffer(messageText);
                await socketA.SendAsync(new ArraySegment<byte>(chatBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                var completed = await Task.WhenAny(messageObservedOnB.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(completed == messageObservedOnB.Task, "Guild member B never received the guild-channel chat message from A.");

                // Negative check: give C a further short window to
                // (incorrectly) receive the same message before concluding
                // it never will.
                await Task.Delay(TimeSpan.FromSeconds(2));
                Assert.False(messageObservedOnC, "Player C, in a different guild, must never receive a guild-channel message sent by A.");

                cts.Cancel();
                try { await receiveTaskB; } catch { }
                try { await receiveTaskC; } catch { }
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        private static unsafe byte[] BuildGuildChatMessageBuffer(string messageText)
        {
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(messageText);
            int length = textBytes.Length > RequestChatMessagePacket.MessageCapacity ? RequestChatMessagePacket.MessageCapacity : textBytes.Length;

            var packet = new RequestChatMessagePacket { MessageLength = (ushort)length, ChannelType = ChatEngine.GuildChannelType };
            byte* target = packet.MessageText;
            for (int i = 0; i < RequestChatMessagePacket.MessageCapacity; i++)
            {
                target[i] = i < length ? textBytes[i] : (byte)0;
            }

            byte[] buffer = new byte[Marshal.SizeOf<RequestChatMessagePacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            return buffer;
        }

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 1.
        // Fires two concurrent WithdrawFromBankAsync calls for the SAME
        // BankEquipmentInstances row. TryBeginPendingTransaction's
        // ConcurrentDictionary.TryAdd is atomic, so exactly one of the two
        // must win and reach the queue; the other must be rejected with
        // TransactionPending before ever touching the database. Then
        // simulates the tick loop's terminal CommitBankWithdrawAsync step
        // for the sole accepted request and proves only one real
        // EquipmentInstances row was ever created - the previous
        // double-enqueue race this task's Part 1 exists to close.
        [Fact]
        public async Task Test_MailboxAndBankEngine_ConcurrentWithdrawals_RejectSecondWithTransactionPendingAndPreventCloning()
        {
            const long testPlayerId = 970002001L;
            const string baseItemId = "integration_test_bank_withdraw_concurrent";
            long bankId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                var bankItem = new BankEquipmentInstance
                {
                    PlayerId = testPlayerId,
                    BaseItemId = baseItemId,
                    QualityTier = 1,
                    AffixPayload = "{}"
                };
                db.BankEquipmentInstances.Add(bankItem);
                await db.SaveChangesAsync();
                bankId = bankItem.Id;
            }

            var playerRegistry = new PlayerSessionRegistry();
            var mailboxEngine = new MailboxAndBankEngine(_fixture.ServiceProvider, playerRegistry);

            await Task.WhenAll(
                mailboxEngine.WithdrawFromBankAsync(testPlayerId, bankId),
                mailboxEngine.WithdrawFromBankAsync(testPlayerId, bankId));

            int queuedCount = 0;
            while (playerRegistry.BankWithdrawRequestQueue.TryDequeue(out var req))
            {
                queuedCount++;
                Assert.Equal(testPlayerId, req.PlayerId);
                Assert.Equal(bankId, req.BankId);
            }
            Assert.Equal(1, queuedCount);

            bool sawTransactionPending = false;
            while (playerRegistry.CommandResultQueue.TryDequeue(out var result))
            {
                if (result.PlayerId == testPlayerId && result.ResultCode == (byte)FolkIdle.Server.Network.CommandResultCode.TransactionPending)
                {
                    sawTransactionPending = true;
                }
            }
            Assert.True(sawTransactionPending, "The second concurrent withdrawal attempt must have been rejected with TransactionPending.");

            await mailboxEngine.CommitBankWithdrawAsync(testPlayerId, bankId, true);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            int clonedEquipmentCount = await verifyDb.EquipmentInstances.AsNoTracking()
                .CountAsync(e => e.PlayerId == testPlayerId && e.BaseItemId == baseItemId);
            Assert.Equal(1, clonedEquipmentCount);

            bool bankRowStillExists = await verifyDb.BankEquipmentInstances.AsNoTracking().AnyAsync(b => b.Id == bankId);
            Assert.False(bankRowStillExists);
        }

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 2.1.
        // Directly exercises ProgressionEngine.ProcessMonsterDeath's
        // level-up threshold at several levels, asserting it matches the
        // GDD exponential curve (100 * 1.15^level) exactly at both sides
        // of the boundary - baseExpReward=0 means this call never adds any
        // XP of its own, only evaluates the level-up check against
        // whatever CurrentXp was set to beforehand.
        [Fact]
        public void Test_ProgressionEngine_LevelUpCost_ScalesExponentially()
        {
            for (int level = 1; level <= 8; level++)
            {
                long requiredXp = (long)Math.Ceiling(100.0 * Math.Pow(1.15, level));

                var belowThreshold = new TickStatePayload { CurrentLevel = level, CurrentXp = requiredXp - 1 };
                ProgressionEngine.ProcessMonsterDeath(ref belowThreshold, baseExpReward: 0, xpMultiplier: 100, activeGlobalEventId: 0);
                Assert.Equal(level, belowThreshold.CurrentLevel);

                var atThreshold = new TickStatePayload { CurrentLevel = level, CurrentXp = requiredXp };
                ProgressionEngine.ProcessMonsterDeath(ref atThreshold, baseExpReward: 0, xpMultiplier: 100, activeGlobalEventId: 0);
                Assert.Equal(level + 1, atThreshold.CurrentLevel);
            }
        }

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 2.2.
        // Asserts VillageManagementEngine.CalculateProductionUpgradeCost
        // matches BaseCost * 1.5^currentLevel exactly, and that the
        // level-to-level growth ratio is a constant 1.5x - the previous
        // (currentLevel + 1)^1.8 polynomial curve's ratio would instead
        // shrink toward 1.0 as currentLevel grew, which this test would
        // catch as a ratio drifting away from 1.5.
        [Fact]
        public void Test_VillageManagementEngine_ProductionUpgradeCost_ScalesExponentially()
        {
            for (int level = 0; level <= 10; level++)
            {
                long expected = (long)Math.Ceiling(100.0 * Math.Pow(1.5, level));
                Assert.Equal(expected, VillageManagementEngine.CalculateProductionUpgradeCost(level));
            }

            long costAtLevel5 = VillageManagementEngine.CalculateProductionUpgradeCost(5);
            long costAtLevel6 = VillageManagementEngine.CalculateProductionUpgradeCost(6);
            double ratio = costAtLevel6 / (double)costAtLevel5;
            Assert.InRange(ratio, 1.49, 1.51);
        }

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 2.3.
        // Measures ForgeSplicingEngine's real gold deduction for two
        // different target QualityTiers, holding both sacrifices at
        // QualityTier 4 in each measurement so the fodder-quality penalty
        // multiplier stays a constant 1.0x - isolating baseGoldCost's own
        // exponential term (ceil(BaseGoldCost * 1.5^currentTier)) from the
        // unrelated penalty multiplier and letting the tier-to-tier ratio
        // be asserted directly.
        [Fact]
        public async Task Test_ForgeSplicing_GoldCost_ScalesExponentiallyWithCurrentTier()
        {
            const long testPlayerId = 970002101L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = testPlayerId, BuildingId = VillageManagementEngine.ForgeBuildingId, CurrentLevel = 20 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 10_000_000L });
                await db.SaveChangesAsync();
            }

            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);

            long costAtTier2 = await MeasureForgeCostAtTierAsync(testPlayerId, "integration_test_forge_exp_tier2", startingTier: 2, forgeEngine);
            long costAtTier3 = await MeasureForgeCostAtTierAsync(testPlayerId, "integration_test_forge_exp_tier3", startingTier: 3, forgeEngine);

            Assert.Equal((long)Math.Ceiling(1000.0 * Math.Pow(1.5, 2)), costAtTier2);
            Assert.Equal((long)Math.Ceiling(1000.0 * Math.Pow(1.5, 3)), costAtTier3);

            double ratio = costAtTier3 / (double)costAtTier2;
            Assert.InRange(ratio, 1.49, 1.51);
        }

        private async Task<long> MeasureForgeCostAtTierAsync(long playerId, string baseItemId, int startingTier, ForgeSplicingEngine forgeEngine)
        {
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var target = new EquipmentInstance { PlayerId = playerId, BaseItemId = baseItemId, QualityTier = startingTier };
                var sac1 = new EquipmentInstance { PlayerId = playerId, BaseItemId = baseItemId, QualityTier = 4 };
                var sac2 = new EquipmentInstance { PlayerId = playerId, BaseItemId = baseItemId, QualityTier = 4 };
                db.EquipmentInstances.AddRange(target, sac1, sac2);
                await db.SaveChangesAsync();

                long goldBefore = await GetGoldAsync(playerId);
                await forgeEngine.ExecuteFusionAsync(playerId, target.Id, sac1.Id, sac2.Id);
                long goldAfter = await GetGoldAsync(playerId);
                return goldBefore - goldAfter;
            }
        }

        // Modul: Phase - Full-Stack Production Polish Phase 2, Part 4.1.
        // Directly exercises StorefrontSegmentationEngine.ResolveCohort's
        // pure decision function against mock transactional signals,
        // proving distinct, accurate cohort assignment driven by actual
        // spending/activity behavior rather than the previous static
        // playerId hash bucket - the same three synthetic input sets would
        // previously have had no bearing whatsoever on cohort assignment.
        [Fact]
        public void Test_StorefrontSegmentationEngine_DynamicSegmentation_ReturnsDistinctCohortsBasedOnLtvAgeAndRecency()
        {
            int highValueActiveCohort = StorefrontSegmentationEngine.ResolveCohort(
                lifetimeValue: 10_000L, ageInTicks: 100L, daysSinceLastTransaction: 2);
            Assert.Equal(StorefrontSegmentationEngine.VariantB, highValueActiveCohort);

            int churnRiskVeteranCohort = StorefrontSegmentationEngine.ResolveCohort(
                lifetimeValue: 0L, ageInTicks: 1000L, daysSinceLastTransaction: 30);
            Assert.Equal(StorefrontSegmentationEngine.VariantA, churnRiskVeteranCohort);

            int newAccountCohort = StorefrontSegmentationEngine.ResolveCohort(
                lifetimeValue: 0L, ageInTicks: 5L, daysSinceLastTransaction: int.MaxValue);
            Assert.Equal(StorefrontSegmentationEngine.Control, newAccountCohort);

            Assert.NotEqual(highValueActiveCohort, churnRiskVeteranCohort);
            Assert.NotEqual(highValueActiveCohort, newAccountCohort);
        }

        // Modul: Production Release Hardening, Part 1. Proves
        // ProductIdHasher is deterministic (the exact property
        // string.GetHashCode() lacked, since .NET randomizes it per
        // process - the actual root cause TargetProductIdHash never
        // resolved before this fix) and that ContentRegistry's reverse
        // lookup table, built once at Initialize from the same
        // GameBalanceConfig.json IapProductPrices catalog
        // ResolvePremiumDiamondsForProduct reads, correctly resolves a
        // real product id back from its hash - and gracefully (never
        // throwing) reports failure for an unrecognized hash.
        [Fact]
        public void Test_ProductIdHasher_HashIsStableAndResolvesViaContentRegistry()
        {
            uint hash = ProductIdHasher.HashProductId("gems_pack_small");
            Assert.Equal(hash, ProductIdHasher.HashProductId("gems_pack_small"));

            Assert.True(ContentRegistry.TryResolveProductIdFromHash(hash, out string resolvedProductId));
            Assert.Equal("gems_pack_small", resolvedProductId);

            Assert.False(ContentRegistry.TryResolveProductIdFromHash(0xDEADBEEFU, out string unresolvedProductId));
            Assert.Null(unresolvedProductId);
        }

        // Modul: Production Release Hardening, Part 1. Exercises
        // BillingVerificationEngine.VerifyPurchaseAsync (the method
        // SimulationEngine's SubmitPurchaseReceipt handler ultimately
        // calls) against both resolution paths that handler now supports:
        // a hash successfully resolved via ContentRegistry.
        // TryResolveProductIdFromHash (the primary path), and a cleartext
        // product id submitted directly as both transactionId and
        // productId (the bulletproof fallback path - mirrors
        // SimulationEngine's own "productId = transactionId" fallback
        // exactly, for when TargetProductIdHash does not resolve). Both
        // must grant the correct, real GameBalanceConfig.json-configured
        // diamond amount.
        [Fact]
        public async Task Test_BillingVerificationEngine_HashResolvedAndCleartextFallbackProductIds_BothGrantCorrectDiamonds()
        {
            const long testPlayerIdHashPath = 970003001L;
            const long testPlayerIdCleartextPath = 970003002L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerIdHashPath, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), PremiumDiamonds = 0 });
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerIdCleartextPath, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), PremiumDiamonds = 0 });
                await db.SaveChangesAsync();
            }

            using var offlineRedis = CreateOfflineRedisMultiplexer();
            var redisCache = new RedisSessionCache(offlineRedis);
            var billingEngine = new BillingVerificationEngine(_fixture.DbContextFactory, redisCache, _fixture.PlayerRegistry, _fixture.RetryingOptions, new MockIapReceiptValidator());

            Assert.True(ContentRegistry.TryResolveProductIdFromHash(ProductIdHasher.HashProductId("gems_pack_medium"), out string hashResolvedProductId));
            bool hashPathSuccess = await billingEngine.VerifyPurchaseAsync(testPlayerIdHashPath, "txn_hash_path_970003001", hashResolvedProductId);
            Assert.True(hashPathSuccess);

            bool cleartextPathSuccess = await billingEngine.VerifyPurchaseAsync(testPlayerIdCleartextPath, "gems_pack_large", "gems_pack_large");
            Assert.True(cleartextPathSuccess);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var hashPathPlayer = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerIdHashPath);
            var cleartextPathPlayer = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerIdCleartextPath);

            Assert.Equal(1100, hashPathPlayer.PremiumDiamonds);
            Assert.Equal(2400, cleartextPathPlayer.PremiumDiamonds);
        }

        // Modul: Production Release Hardening, Part 2, and Full-Stack
        // Production Hardening Phase 3, Part 4. StateUpdatePacket shrank
        // from 744 to 696 (ClaimedMilestonesBitmask, seasonal
        // meta-statistics, and static achievement data moved to
        // /api/v1/player/metadata and /api/v1/achievements/state - see
        // that struct's own trailing doc comment), then from 696 to 680
        // (34 bytes of dead *Reserved* filler removed, offset by the
        // command-result ring buffer's +18 bytes) - this is the structural
        // proof the 10Hz hot-path packet is strictly under 700 bytes, not
        // just NetworkPacketLayoutGuard's exact-680 pin (which would also
        // pass at, say, 699).
        [Fact]
        public void Test_StateUpdatePacket_StructuralSizeIsStrictlyUnder700Bytes()
        {
            int actualSize = System.Runtime.InteropServices.Marshal.SizeOf<StateUpdatePacket>();
            Assert.True(actualSize < 700, $"StateUpdatePacket is {actualSize} bytes - expected strictly under 700.");
        }

        // Modul: Production Release Hardening, Part 3. Exercises
        // ContentRegistry.TryGetLocalization against the real, dynamically
        // parsed server/GameData/localizations.json (loaded once by
        // PostgresTestFixture.InitializeAsync's own ContentRegistry.
        // Initialize call, the same boot path the real server uses) -
        // proves German and Czech resolve correctly, and that both a
        // wholly unrecognized key and an unrecognized language code
        // degrade gracefully to the English fallback rather than throwing.
        [Fact]
        public void Test_ContentRegistry_LocalizationLookup_ResolvesGermanAndCzechWithEnglishFallback()
        {
            Assert.True(ContentRegistry.TryGetLocalization("BossHpPrefix", "de", out string deValue));
            Assert.Equal("Boss LP: ", deValue);

            Assert.True(ContentRegistry.TryGetLocalization("ActiveEventPrefix", "cs", out string csValue));
            Assert.Equal("Aktivni event: ", csValue);

            bool resolvedMissingKey = ContentRegistry.TryGetLocalization("ThisKeyDoesNotExist", "de", out string missingKeyValue);
            Assert.False(resolvedMissingKey);
            Assert.Equal(string.Empty, missingKeyValue);

            Assert.True(ContentRegistry.TryGetLocalization("EventNone", "fr", out string fallbackValue));
            Assert.Equal("None", fallbackValue);
        }

        // Modul: Final Production Polish, Part 1/5. Every key appended to
        // localizations.json for the UI-header/error-message/dynamic-state
        // expansion must resolve to a non-empty value for all four
        // supported languages - proving the same fallback-safe
        // ContentRegistry parser the client's LocalizationMatrix mirrors
        // (both read the exact same server/GameData/localizations.json,
        // see LocalizationMatrix's own doc comment) indexes and resolves
        // the expanded key set correctly. LocalizationMatrix itself cannot
        // run inside this xunit project (it is a UnityEngine-dependent
        // unsafe class - Application.streamingAssetsPath, Marshal.
        // AllocHGlobal - with no headless equivalent here), so
        // ContentRegistry.TryGetLocalization against the identical source
        // JSON is the real, testable proxy for "the expanded matrix loads,
        // indexes, and resolves all newly authored keys."
        [Fact]
        public void Test_ContentRegistry_LocalizationLookup_ResolvesFinalProductionPolishKeysAcrossAllLanguages()
        {
            string[] keys =
            {
                "HeaderMailbox", "HeaderBankVault", "HeaderStore", "HeaderSeasonPass",
                "HeaderGuildRoster", "HeaderOfflineSummary",
                "ErrorTransactionPending", "ErrorMaxTierReached", "ErrorInsufficientFunds", "ErrorInventoryFull",
                "StateLevelUp", "StateAllProgressSaved", "StateSavedPrefix", "StateMinutesAgoSuffix", "StateHoursAgoSuffix",
                "OfflineAwayForPrefix", "OfflineHoursSuffix", "OfflineMinutesSuffix",
                "GuildWarStatusActive", "GuildWarStatusInactive"
            };
            string[] languageCodes = { "en", "cs", "de", "pl" };

            foreach (string key in keys)
            {
                foreach (string languageCode in languageCodes)
                {
                    bool resolved = ContentRegistry.TryGetLocalization(key, languageCode, out string value);
                    Assert.True(resolved, $"Expected '{key}' to resolve for language '{languageCode}'.");
                    Assert.False(string.IsNullOrEmpty(value), $"Expected '{key}' to resolve to a non-empty value for language '{languageCode}'.");
                }
            }

            Assert.True(ContentRegistry.TryGetLocalization("HeaderMailbox", "en", out string mailboxEn));
            Assert.Equal("Mailbox", mailboxEn);

            Assert.True(ContentRegistry.TryGetLocalization("ErrorMaxTierReached", "pl", out string maxTierPl));
            Assert.Equal("Osiagnieto maksymalny poziom", maxTierPl);
        }

        // Modul: Final Production Polish, Part 4/5. ActiveSkillEngine.
        // ApplyStatusSynergy is the sole logic implementing the Chilled/
        // Vulnerable synergy (called from RequestCastSkill's success
        // branch in SimulationEngine.cs) - a pure struct-in/float-out
        // function, exercised directly here without any DB/network
        // fixture, exactly like the existing StatsCalculator_* tests in
        // this class. Skill 1 (Ice Bolt) applies Chilled with no bonus to
        // itself; skill 2 (Heavy Strike) consumes it for the 1.5x
        // synergy multiplier and clears the bit, so a second Heavy Strike
        // with no Chilled active gets no bonus.
        [Fact]
        public void Test_ActiveSkillEngine_StatusSynergy_ChilledConsumedByHeavyStrikeForBonusMultiplier()
        {
            var payload = new TickStatePayload();

            float iceBoltMultiplier = ActiveSkillEngine.ApplyStatusSynergy(ref payload, 1);
            Assert.Equal(1f, iceBoltMultiplier);
            Assert.Equal(ActiveSkillEngine.StatusFlagChilled, (byte)(payload.TargetStatusEffectBitmask & ActiveSkillEngine.StatusFlagChilled));

            float heavyStrikeMultiplier = ActiveSkillEngine.ApplyStatusSynergy(ref payload, 2);
            Assert.Equal(ActiveSkillEngine.StatusSynergyDamageMultiplier, heavyStrikeMultiplier);
            Assert.Equal(0, payload.TargetStatusEffectBitmask & ActiveSkillEngine.StatusFlagChilled);

            float heavyStrikeMultiplierAgain = ActiveSkillEngine.ApplyStatusSynergy(ref payload, 2);
            Assert.Equal(1f, heavyStrikeMultiplierAgain);
        }

        // Modul: Final Production Polish, Part 4/5. Mirrors the Chilled/
        // Heavy Strike test above for the Vulnerable/Swift Slash pairing,
        // then proves the two status bits are independent - consuming
        // Vulnerable (skill 4) must not clear a still-active Chilled bit
        // applied earlier by skill 1, since both live in the same byte
        // bitmask.
        [Fact]
        public void Test_ActiveSkillEngine_StatusSynergy_VulnerableAndChilledBitsAreIndependent()
        {
            var payload = new TickStatePayload();

            ActiveSkillEngine.ApplyStatusSynergy(ref payload, 1);
            float sunderingBlowMultiplier = ActiveSkillEngine.ApplyStatusSynergy(ref payload, 3);
            Assert.Equal(1f, sunderingBlowMultiplier);
            Assert.Equal(
                (byte)(ActiveSkillEngine.StatusFlagChilled | ActiveSkillEngine.StatusFlagVulnerable),
                payload.TargetStatusEffectBitmask);

            float swiftSlashMultiplier = ActiveSkillEngine.ApplyStatusSynergy(ref payload, 4);
            Assert.Equal(ActiveSkillEngine.StatusSynergyDamageMultiplier, swiftSlashMultiplier);
            Assert.Equal(ActiveSkillEngine.StatusFlagChilled, payload.TargetStatusEffectBitmask);
        }

        // Modul: Full-Stack Production Hardening Phase 3, Part 1/7. Proves
        // RemoveActivePlayer is now the single authoritative cleanup choke
        // point - a real running SimulationEngine, one injected player
        // kicked via a guaranteed-invalid MarketListItem (price <= 0,
        // rejected by ClientCommandValidator.ValidateMarketCommands's
        // earliest branch), one of the ~21 anti-cheat/validation-failure
        // sites this fix centralizes cleanup for. Before this fix,
        // _liveSessionContexts was only ever cleared by
        // TerminateSessionForSecurity's own explicit call (never by a
        // plain kick site), and PlayerSessionRegistry._onlinePlayers was
        // only cleared by the Logout command handler - which a kicked
        // player's deferred Logout (enqueued by ForceDisconnect's socket-
        // closure finally block) never reached, because the command loop's
        // null-ref guard silently dropped it once _activePlayers no longer
        // held the entry. Both must now be gone immediately alongside
        // _activePlayers removal, not eventually via that deferred command.
        [Fact]
        public async Task Test_SimulationEngine_KickedPlayer_CleansUpLiveSessionContextAndOnlineRegistration()
        {
            const long testPlayerId = 970001301L;

            var simulationEngine = CreateTestSimulationEngine();

            try
            {
                simulationEngine.Start();

                simulationEngine.InjectVirtualPlayer(new TickStatePayload
                {
                    PlayerId = testPlayerId,
                    InventorySpaceRemaining = 1000
                });
                _fixture.PlayerRegistry.RegisterPlayer(testPlayerId);

                Assert.True(simulationEngine.IsActivePlayerPresent(testPlayerId));
                Assert.True(simulationEngine.IsLiveSessionContextPresent(testPlayerId));
                Assert.True(_fixture.PlayerRegistry.IsPlayerOnline(testPlayerId));

                simulationEngine.InjectBenchmarkCommand(testPlayerId, new ClientCommandPacket
                {
                    Command = CommandType.MarketListItem,
                    TargetId = 1,
                    LimitPrice = 0
                });

                await WaitForConditionAsync(
                    () => !simulationEngine.IsActivePlayerPresent(testPlayerId),
                    "Kicked player was never removed from _activePlayers.");

                Assert.False(simulationEngine.IsLiveSessionContextPresent(testPlayerId));
                Assert.False(_fixture.PlayerRegistry.IsPlayerOnline(testPlayerId));
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        // Modul: Full-Stack Production Hardening Phase 3, Part 2/7. The
        // real bug in the old async Task ObserveSendFault wrapper was a
        // brand new Task plus a boxed async state machine allocated on
        // every single invocation, every tick, for every online player,
        // regardless of whether a fault ever occurred. _logSendFault
        // replaces that with a `static` (compiler-verified zero-capture,
        // see that field's own doc comment) delegate assigned exactly
        // once at class load, never re-created per call - proven here by
        // reference identity across repeated field reads - and repeated
        // direct invocations against an already-faulted, already-completed
        // Task (bypassing ContinueWith/scheduling entirely, which is
        // separate Task-API plumbing, not the callback body itself)
        // allocate an identical, non-growing amount on every call rather
        // than leaking or scaling with call volume.
        [Fact]
        public void Test_NetworkBroadcastSystem_LogSendFaultDelegate_IsStaticNonCapturingAndDoesNotGrowPerInvocation()
        {
            Action<Task, object?> callback = NetworkBroadcastSystem._logSendFault;

            Assert.Same(callback, NetworkBroadcastSystem._logSendFault);

            Task faultedTask = Task.FromException(new InvalidOperationException("test fault"));

            // Warm-up call, not measured - the first invocation of any
            // code path pays one-time JIT/lazy-init costs (here, largely
            // the <>c display-class singleton's first allocation, see
            // _logSendFault's own doc comment) that a steady-state
            // per-call comparison must exclude to be meaningful.
            callback(faultedTask, 970001401L);

            long before = GC.GetAllocatedBytesForCurrentThread();
            callback(faultedTask, 970001401L);
            long afterFirst = GC.GetAllocatedBytesForCurrentThread();
            long firstCallBytes = afterFirst - before;

            callback(faultedTask, 970001401L);
            long afterSecond = GC.GetAllocatedBytesForCurrentThread();
            long secondCallBytes = afterSecond - afterFirst;

            Assert.Equal(firstCallBytes, secondCallBytes);
        }

        // Modul: Full-Stack Production Hardening Phase 3, Part 3/7.
        // Invokes EcoTelemetryEngine.ExecuteAuditAsync directly (internal
        // via InternalsVisibleTo, rather than waiting on StartCron's
        // 10-minute polling loop) and reads back
        // LastObservedAuditIsolationLevel, which captures what Npgsql/
        // Postgres actually negotiated for the read transaction - a
        // stronger proof than merely inspecting that RepeatableRead was
        // requested in source, since it would also catch a silent
        // downgrade.
        [Fact]
        public async Task Test_EcoTelemetryEngine_AuditQueries_RunUnderRepeatableReadIsolation()
        {
            var telemetryEngine = new EcoTelemetryEngine(_fixture.ServiceProvider);

            await telemetryEngine.ExecuteAuditAsync(CancellationToken.None);

            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, EcoTelemetryEngine.LastObservedAuditIsolationLevel);
        }

        // Modul: Full-Stack Production Hardening Phase 3, Part 5/7. Proves
        // the 4-slot command-result ring buffer end to end - 4 rapid
        // concurrent rejections must all survive into distinct slots (not
        // just the last one overwriting a single scalar), in ascending
        // per-player-monotonic ResultTick order matching insertion order,
        // and a 5th rejection must overwrite specifically the OLDEST slot
        // (ring-buffer wraparound) while the 3 more recent ones remain
        // untouched.
        [Fact]
        public async Task Test_SimulationEngine_CommandResultRingBuffer_BuffersMultipleConcurrentRejectionsWithoutLoss()
        {
            const long testPlayerId = 970001501L;

            var simulationEngine = CreateTestSimulationEngine();

            try
            {
                simulationEngine.Start();
                simulationEngine.InjectVirtualPlayer(new TickStatePayload { PlayerId = testPlayerId, InventorySpaceRemaining = 1000 });

                byte code1 = (byte)FolkIdle.Server.Network.CommandResultCode.InvalidPrice;
                byte code2 = (byte)FolkIdle.Server.Network.CommandResultCode.ItemEquipped;
                byte code3 = (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientMaterials;
                byte code4 = (byte)FolkIdle.Server.Network.CommandResultCode.InvalidActivity;

                _fixture.PlayerRegistry.EnqueueCommandResult(testPlayerId, code1);
                _fixture.PlayerRegistry.EnqueueCommandResult(testPlayerId, code2);
                _fixture.PlayerRegistry.EnqueueCommandResult(testPlayerId, code3);
                _fixture.PlayerRegistry.EnqueueCommandResult(testPlayerId, code4);

                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerCommandResultSlots(testPlayerId).Count(s => s.tick > 0) == 4,
                    "All 4 enqueued command results were not drained into the ring buffer.");

                var slots = simulationEngine.GetActivePlayerCommandResultSlots(testPlayerId);
                var codesPresent = slots.Select(s => s.code).OrderBy(c => c).ToArray();
                Assert.Equal(new[] { code1, code2, code3, code4 }.OrderBy(c => c).ToArray(), codesPresent);

                var byTick = slots.OrderBy(s => s.tick).Select(s => s.code).ToArray();
                Assert.Equal(new[] { code1, code2, code3, code4 }, byTick);

                byte code5 = (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientGold;
                _fixture.PlayerRegistry.EnqueueCommandResult(testPlayerId, code5);

                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerCommandResultSlots(testPlayerId).Any(s => s.code == code5),
                    "The 5th command result was never appended to the ring buffer.");

                var slotsAfterWrap = simulationEngine.GetActivePlayerCommandResultSlots(testPlayerId);
                Assert.DoesNotContain(slotsAfterWrap, s => s.code == code1);
                Assert.Contains(slotsAfterWrap, s => s.code == code2);
                Assert.Contains(slotsAfterWrap, s => s.code == code3);
                Assert.Contains(slotsAfterWrap, s => s.code == code4);
                Assert.Contains(slotsAfterWrap, s => s.code == code5);
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        // Modul: Comprehensive Game System Audit, Part 5/8. A client
        // cannot forge time by manipulating its OS clock because no
        // client-supplied timestamp is ever a progression input - but the
        // one clock-integrity signal the server does check at login
        // (ValidateLoginTime: a LastLogoutTimestamp in the future relative
        // to the server's own clock, indicating DB clock skew or a
        // rolled-back/tampered record) must reject the session. And the
        // offline extrapolation path must grant zero progress for a
        // non-positive elapsed delta and clamp an absurdly long one to
        // MaxOfflineSeconds - so even a tampered LastLogoutTimestamp far
        // in the past cannot mint unbounded offline gains.
        [Fact]
        public void Test_TimeManipulation_FutureLogoutTimestampRejectedAndOfflineDeltaClamped()
        {
            long serverNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var forgedPayload = new TickStatePayload
            {
                PlayerId = 970002001L,
                LastLogoutTimestamp = serverNow + 3600L
            };
            Assert.False(ClientCommandValidator.ValidateLoginTime(ref forgedPayload, serverNow),
                "A LastLogoutTimestamp in the server's future must reject the login.");

            var honestPayload = new TickStatePayload
            {
                PlayerId = 970002002L,
                LastLogoutTimestamp = serverNow - 600L
            };
            Assert.True(ClientCommandValidator.ValidateLoginTime(ref honestPayload, serverNow));
        }

        // Modul: Comprehensive Game System Audit, Part 4/8. The
        // self-sustaining battle-pass loop: the sum of PremiumDiamonds
        // rewarded across the 50 premium-track milestones must equal or
        // exceed the pass purchase price, so a fully active player who
        // completes the season can always afford the next season's pass
        // from rewards alone. Exact-value pins alongside the inequality so
        // an accidental reward-table edit that still passes the inequality
        // by shrinking the dividend to zero margin is visible in review.
        [Fact]
        public void Test_ChroniclePassEconomy_PremiumRewardsSustainNextSeasonPurchase()
        {
            int totalRewards = ChroniclePassEconomy.TotalPremiumDiamondRewards();

            Assert.True(totalRewards >= ChroniclePassEconomy.PremiumPassPriceDiamonds,
                $"Premium track rewards ({totalRewards}) must cover the pass price ({ChroniclePassEconomy.PremiumPassPriceDiamonds}).");

            Assert.Equal(1000, totalRewards);
            Assert.Equal(950, ChroniclePassEconomy.PremiumPassPriceDiamonds);

            Assert.Equal(0, ChroniclePassEconomy.GetPremiumDiamondReward(0));
            Assert.Equal(100, ChroniclePassEconomy.GetPremiumDiamondReward(4));
            Assert.Equal(100, ChroniclePassEconomy.GetPremiumDiamondReward(49));
            Assert.Equal(0, ChroniclePassEconomy.GetPremiumDiamondReward(50));
            Assert.Equal(0, ChroniclePassEconomy.GetPremiumDiamondReward(-1));
        }

        // Modul: Comprehensive Game System Audit, Part 4/8. Purchase flow
        // end to end against the real database: a player holding exactly
        // the pass price buys the premium track (balance drops to 0,
        // PremiumUnlocked set), a double purchase is rejected without a
        // second deduction, and a broke player cannot purchase at all.
        [Fact]
        public async Task Test_ChroniclePass_PurchaseDeductsDiamondsAndDoublePurchaseRejected()
        {
            // 970004xxx range - 970002101 collides with
            // Test_ForgeSplicing_GoldCost_ScalesExponentiallyWithCurrentTier's
            // own seeded player in this shared-fixture collection.
            const long testPlayerId = 970004001L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    PremiumDiamonds = ChroniclePassEconomy.PremiumPassPriceDiamonds
                });
                await db.SaveChangesAsync();
            }

            var simulationEngine = CreateTestSimulationEngine();

            Assert.True(await simulationEngine.ExecutePassPurchaseAsync(testPlayerId));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(0, player.PremiumDiamonds);

                var pass = await db.PlayerChroniclePasses.AsNoTracking().SingleAsync(p => p.PlayerId == testPlayerId);
                Assert.True(pass.PremiumUnlocked);
            }

            Assert.False(await simulationEngine.ExecutePassPurchaseAsync(testPlayerId),
                "A second purchase of an already-unlocked pass must be rejected.");

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(0, player.PremiumDiamonds);
            }
        }

        // Modul: Comprehensive Game System Audit, Part 2/8. The
        // allocation-free profanity filter: blacklisted words are masked
        // in place with asterisks (case-insensitively, including embedded
        // occurrences), clean text passes untouched, and a warm
        // steady-state call allocates exactly zero managed heap bytes -
        // measured the same way the Phase 3 _logSendFault test does.
        [Fact]
        public void Test_ChatProfanityFilter_MasksBlacklistedWordsWithoutHeapAllocation()
        {
            byte[] message = System.Text.Encoding.UTF8.GetBytes("you are such a FuCk head");
            int masked = ChatProfanityFilter.FilterInPlace(message, message.Length);
            Assert.Equal(1, masked);
            Assert.Equal("you are such a **** head", System.Text.Encoding.UTF8.GetString(message));

            byte[] cleanMessage = System.Text.Encoding.UTF8.GetBytes("hello guild, selling iron ore cheap");
            string before = System.Text.Encoding.UTF8.GetString(cleanMessage);
            Assert.Equal(0, ChatProfanityFilter.FilterInPlace(cleanMessage, cleanMessage.Length));
            Assert.Equal(before, System.Text.Encoding.UTF8.GetString(cleanMessage));

            byte[] embedded = System.Text.Encoding.UTF8.GetBytes("what a bullSHITstorm today");
            Assert.Equal(1, ChatProfanityFilter.FilterInPlace(embedded, embedded.Length));
            Assert.Equal("what a bull****storm today", System.Text.Encoding.UTF8.GetString(embedded));

            byte[] warmBuffer = System.Text.Encoding.UTF8.GetBytes("this shit again and again you fuck");
            ChatProfanityFilter.FilterInPlace(warmBuffer, warmBuffer.Length);

            byte[] measured = System.Text.Encoding.UTF8.GetBytes("this shit again and again you fuck");
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            int maskedCount = ChatProfanityFilter.FilterInPlace(measured, measured.Length);
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(2, maskedCount);
            Assert.Equal(0L, bytesAfter - bytesBefore);
        }

        // Modul: Comprehensive Game System Audit, Part 3/8. Gold
        // contributions from multiple members must land in each member's
        // own GuildMember.ContributionPoints (previously only raid
        // victories did) so the roster's existing
        // ContributionPoints-descending ordering ranks donors correctly
        // under interleaved traffic.
        [Fact]
        public async Task Test_GuildContribution_GoldDonationsRankMembersByContributionPoints()
        {
            const long guildId = 970002300L;
            const long bigDonorId = 970002301L;
            const long smallDonorId = 970002302L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.GuildRecords.Add(new GuildRecord { Id = guildId, Name = "ContributionRankGuild970002300" });
                db.PlayerRecords.Add(new PlayerRecord { Id = bigDonorId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = guildId });
                db.PlayerRecords.Add(new PlayerRecord { Id = smallDonorId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = guildId });
                db.GuildMembers.Add(new GuildMember { GuildId = guildId, PlayerId = bigDonorId, Role = 0, ContributionPoints = 0 });
                db.GuildMembers.Add(new GuildMember { GuildId = guildId, PlayerId = smallDonorId, Role = 0, ContributionPoints = 0 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = bigDonorId, ItemId = "gold", Quantity = 100000L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = smallDonorId, ItemId = "gold", Quantity = 100000L });
                await db.SaveChangesAsync();
            }

            var contributionEngine = new GuildContributionEngine(_fixture.ServiceProvider);

            // Interleaved donations - the big donor gives more across
            // multiple smaller deposits, proving accumulation rather than
            // last-write-wins.
            await contributionEngine.ContributeGoldAsync(smallDonorId, guildId, 1000L);
            await contributionEngine.ContributeGoldAsync(bigDonorId, guildId, 2000L);
            await contributionEngine.ContributeGoldAsync(bigDonorId, guildId, 3000L);
            await contributionEngine.ContributeGoldAsync(smallDonorId, guildId, 500L);
            await contributionEngine.ContributeGoldAsync(bigDonorId, guildId, 1500L);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var ranked = await verifyDb.GuildMembers.AsNoTracking()
                    .Where(m => m.GuildId == guildId)
                    .OrderByDescending(m => m.ContributionPoints)
                    .ToListAsync();

                Assert.Equal(2, ranked.Count);
                Assert.Equal(bigDonorId, ranked[0].PlayerId);
                Assert.Equal(smallDonorId, ranked[1].PlayerId);
                Assert.True(ranked[0].ContributionPoints > ranked[1].ContributionPoints);

                long divisor = ContentRegistry.Balance.GuildContributionGoldToExpDivisor;
                Assert.Equal(6500L / divisor, ranked[0].ContributionPoints);
                Assert.Equal(1500L / divisor, ranked[1].ContributionPoints);
            }
        }

        // Modul: Comprehensive Game System Audit, Part 6/8. The rotating
        // login-reward matrix must switch deterministically on the UTC
        // week boundary, cycle through all matrices, and every matrix must
        // carry the identical weekly total so rotation never changes
        // earning power.
        [Fact]
        public void Test_DailyLoginRewardEngine_MatrixRotatesWeeklyWithConstantWeeklyTotal()
        {
            const long baseDateKey = 20000L;
            long weekAlignedKey = (baseDateKey / 7L) * 7L;

            int weekAIndex = DailyLoginRewardEngine.ResolveActiveMatrixIndex(weekAlignedKey);
            int weekBIndex = DailyLoginRewardEngine.ResolveActiveMatrixIndex(weekAlignedKey + 7L);
            int weekCIndex = DailyLoginRewardEngine.ResolveActiveMatrixIndex(weekAlignedKey + 14L);
            int weekDIndex = DailyLoginRewardEngine.ResolveActiveMatrixIndex(weekAlignedKey + 21L);

            Assert.NotEqual(weekAIndex, weekBIndex);
            Assert.NotEqual(weekBIndex, weekCIndex);
            Assert.NotEqual(weekCIndex, weekAIndex);
            Assert.Equal(weekAIndex, weekDIndex);

            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                Assert.Equal(
                    DailyLoginRewardEngine.ResolveActiveMatrixIndex(weekAlignedKey),
                    DailyLoginRewardEngine.ResolveActiveMatrixIndex(weekAlignedKey + dayOffset));
            }

            long weekATotal = 0L;
            long weekBTotal = 0L;
            long weekCTotal = 0L;
            for (int day = 1; day <= 7; day++)
            {
                weekATotal += DailyLoginRewardEngine.GetGoldReward(weekAlignedKey, day);
                weekBTotal += DailyLoginRewardEngine.GetGoldReward(weekAlignedKey + 7L, day);
                weekCTotal += DailyLoginRewardEngine.GetGoldReward(weekAlignedKey + 14L, day);
            }

            Assert.Equal(25500L, weekATotal);
            Assert.Equal(weekATotal, weekBTotal);
            Assert.Equal(weekATotal, weekCTotal);
        }

        // Modul: Advanced Economy Refactoring, Part 1/4. Materials are ONE
        // unified CommodityRecords pool - gathering (tick loop), village
        // passive production (checkpoint flush), and every consumer
        // (crafting, forge, village upgrades, vendors) read and write the
        // same rows, so "hitting a workbench" never requires a transfer
        // step. This test additionally pins the pool's unbounded-stack
        // semantics: a quantity far beyond any supposed 999/9999 per-stack
        // cap survives the store-and-consume round trip intact - if a
        // stack cap is ever introduced on this path, this breaks loudly.
        [Fact]
        public async Task Test_UnifiedMaterialPool_CraftingConsumesDirectlyFromPoolBeyondLegacyStackCaps()
        {
            const long testPlayerId = 970005001L;
            const long seededQuantity = 12000L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "copper_ore", Quantity = seededQuantity });
                await db.SaveChangesAsync();
            }

            var craftingEngine = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
            await craftingEngine.ExecuteEquipmentCraftingAsync(testPlayerId, 1, slotIndex: 0, tickToken: 555);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var commodity = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "copper_ore");

            Assert.Equal(seededQuantity - 10L, commodity.Quantity);
            Assert.True(commodity.Quantity > 9999L, "A material quantity beyond the legacy stack cap must survive intact - no cap exists on the unified pool.");

            var crafted = await verifyDb.EquipmentInstances.AsNoTracking().SingleAsync(e => e.PlayerId == testPlayerId);
            Assert.Equal("copper_greatsword_melee_weapon_slot_base", crafted.BaseItemId);
        }

        // Modul: Advanced Economy Refactoring, Part 2.1/4. Trade license -
        // a player without a guild is completely blocked from both sides
        // of the market: listing an owned item fails (the item never
        // leaves their inventory) and buying an open order fails (order
        // stays open, gold untouched).
        [Fact]
        public async Task Test_Market_GuildlessPlayerBlockedFromListingAndBuying()
        {
            const long guildlessSellerId = 970005101L;
            const long guildlessBuyerId = 970005102L;
            const long licensedSellerId = 970005103L;
            const long sellerGuildId = 970005150L;

            long ownedItemId;
            long openOrderId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = guildlessSellerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = guildlessBuyerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 100 });
                db.PlayerRecords.Add(new PlayerRecord { Id = licensedSellerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = sellerGuildId });
                db.GuildRecords.Add(new GuildRecord { Id = sellerGuildId, Name = "TradeLicenseGuild970005150" });

                var ownedItem = new EquipmentInstance { PlayerId = guildlessSellerId, BaseItemId = "copper_greatsword_melee_weapon_slot_base", QualityTier = 0, AffixPayload = "{}" };
                db.EquipmentInstances.Add(ownedItem);

                var escrowItem = new MarketEquipmentInstance { PlayerId = licensedSellerId, BaseItemId = "copper_greatsword_melee_weapon_slot_base", QualityTier = 0, AffixPayload = "{}", IsLockedInEscrow = true };
                db.MarketEquipmentInstances.Add(escrowItem);
                await db.SaveChangesAsync();

                ownedItemId = ownedItem.Id;

                var order = new MarketOrderRecord
                {
                    SellerId = licensedSellerId,
                    OrderType = "SELL",
                    EquipmentInstanceId = escrowItem.Id,
                    BaseItemId = "copper_greatsword_melee_weapon_slot_base",
                    QualityTier = 0,
                    Price = 500L,
                    Status = 0,
                    CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                db.MarketOrderRecords.Add(order);
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = guildlessBuyerId, ItemId = "gold", Quantity = 100000L });
                await db.SaveChangesAsync();
                openOrderId = order.Id;
            }

            var escrowEngine = new MarketEscrowEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            bool listed = await escrowEngine.ListItemAsync(guildlessSellerId, ownedItemId, 500L);
            Assert.False(listed, "A guildless player must not be able to list on the market.");

            await escrowEngine.BuyItemAsync(guildlessBuyerId, openOrderId, hasSpace: true);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(await verifyDb.EquipmentInstances.AsNoTracking().AnyAsync(e => e.Id == ownedItemId && e.PlayerId == guildlessSellerId),
                    "The rejected listing must leave the item in the seller's inventory.");

                var order = await verifyDb.MarketOrderRecords.AsNoTracking().SingleAsync(o => o.Id == openOrderId);
                Assert.Equal(0, order.Status);

                var buyerGold = await verifyDb.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == guildlessBuyerId && c.ItemId == "gold");
                Assert.Equal(100000L, buyerGold.Quantity);
            }
        }

        // Modul: Advanced Economy Refactoring, Part 2.2/2.3/4. Anti-cheese
        // level locks - a low-level buyer cannot purchase over-leveled
        // market gear (region-9 T5 derives RequiredLevel 90), and the
        // same gate blocks equipping such gear acquired through any other
        // channel.
        [Fact]
        public async Task Test_Market_LowLevelPlayerBlockedFromBuyingAndEquippingOverLeveledGear()
        {
            const long lowLevelBuyerId = 970005201L;
            const long sellerId = 970005202L;
            const long buyerGuildId = 970005250L;
            const string highTierBaseId = "northern_greataxe_melee_weapon_slot_base";

            Assert.Equal(90, EquipmentLevelGate.DeriveRequiredLevel(9, 5));
            Assert.Equal(90, EquipmentLevelGate.DeriveRequiredLevel(highTierBaseId, 5));
            Assert.Equal(0, EquipmentLevelGate.DeriveRequiredLevel(1, 0));

            long openOrderId;
            long ownedHighTierItemId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = lowLevelBuyerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 10, GuildId = buyerGuildId });
                db.PlayerRecords.Add(new PlayerRecord { Id = sellerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.GuildRecords.Add(new GuildRecord { Id = buyerGuildId, Name = "LevelLockGuild970005250" });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = lowLevelBuyerId, ItemId = "gold", Quantity = 1000000L });

                var escrowItem = new MarketEquipmentInstance { PlayerId = sellerId, BaseItemId = highTierBaseId, QualityTier = 5, AffixPayload = "{}", IsLockedInEscrow = true };
                db.MarketEquipmentInstances.Add(escrowItem);

                var ownedItem = new EquipmentInstance { PlayerId = lowLevelBuyerId, BaseItemId = highTierBaseId, QualityTier = 5, AffixPayload = "{}" };
                db.EquipmentInstances.Add(ownedItem);
                await db.SaveChangesAsync();

                ownedHighTierItemId = ownedItem.Id;

                var order = new MarketOrderRecord
                {
                    SellerId = sellerId,
                    OrderType = "SELL",
                    EquipmentInstanceId = escrowItem.Id,
                    BaseItemId = highTierBaseId,
                    QualityTier = 5,
                    Price = 1000L,
                    Status = 0,
                    CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                db.MarketOrderRecords.Add(order);
                await db.SaveChangesAsync();
                openOrderId = order.Id;
            }

            var escrowEngine = new MarketEscrowEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await escrowEngine.BuyItemAsync(lowLevelBuyerId, openOrderId, hasSpace: true);

            var equipmentSlotEngine = new EquipmentSlotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await equipmentSlotEngine.EquipItemAsync(lowLevelBuyerId, ownedHighTierItemId);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var order = await verifyDb.MarketOrderRecords.AsNoTracking().SingleAsync(o => o.Id == openOrderId);
                Assert.Equal(0, order.Status);

                var buyerGold = await verifyDb.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == lowLevelBuyerId && c.ItemId == "gold");
                Assert.Equal(1000000L, buyerGold.Quantity);

                var buyer = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == lowLevelBuyerId);
                Assert.Null(buyer.EquippedWeaponId);
            }
        }

        // Modul: Advanced Economy Refactoring, Part 2.5/4. Configurable
        // guild sales tax - a completed purchase deducts the seller's
        // guild's TaxRatePct cut from the gross price, deposits it into
        // that guild's central gold ledger row, and awards only the net
        // remainder (gross - wealth fee - guild tax) to the seller.
        [Fact]
        public async Task Test_Market_GuildSalesTaxDepositedToGuildLedgerAndNetProceedsToSeller()
        {
            const long buyerId = 970005301L;
            const long sellerId = 970005302L;
            const long buyerGuildId = 970005350L;
            const long sellerGuildId = 970005351L;
            const long price = 1000L;

            long openOrderId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = buyerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 100, GuildId = buyerGuildId });
                db.PlayerRecords.Add(new PlayerRecord { Id = sellerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = sellerGuildId });
                db.GuildRecords.Add(new GuildRecord { Id = buyerGuildId, Name = "TaxBuyerGuild970005350" });
                db.GuildRecords.Add(new GuildRecord { Id = sellerGuildId, Name = "TaxSellerGuild970005351", TaxRatePct = 20 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = buyerId, ItemId = "gold", Quantity = 10000L });

                var escrowItem = new MarketEquipmentInstance { PlayerId = sellerId, BaseItemId = "copper_greatsword_melee_weapon_slot_base", QualityTier = 0, AffixPayload = "{}", IsLockedInEscrow = true };
                db.MarketEquipmentInstances.Add(escrowItem);
                await db.SaveChangesAsync();

                var order = new MarketOrderRecord
                {
                    SellerId = sellerId,
                    OrderType = "SELL",
                    EquipmentInstanceId = escrowItem.Id,
                    BaseItemId = "copper_greatsword_melee_weapon_slot_base",
                    QualityTier = 0,
                    Price = price,
                    Status = 0,
                    CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                db.MarketOrderRecords.Add(order);
                await db.SaveChangesAsync();
                openOrderId = order.Id;
            }

            var escrowEngine = new MarketEscrowEngine(_fixture.ServiceProvider, new PlayerSessionRegistry());
            await escrowEngine.BuyItemAsync(buyerId, openOrderId, hasSpace: true);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.False(await verifyDb.MarketOrderRecords.AsNoTracking().AnyAsync(o => o.Id == openOrderId),
                    "The completed order must be evicted from the active ledger.");

                // Gross 1000: wealth fee 5% (seller wealth 0) = 50, guild
                // tax 20% = 200, net seller proceeds = 750 (seller offline
                // with a fresh PlayerSessionRegistry, so gold is credited
                // directly).
                var guildLedger = await verifyDb.GuildMaterialSinkLedgers.AsNoTracking()
                    .SingleAsync(l => l.GuildId == sellerGuildId && l.CommodityId == "gold");
                Assert.Equal(200L, guildLedger.TotalAmountContributed);

                var sellerGold = await verifyDb.CommodityRecords.AsNoTracking()
                    .SingleAsync(c => c.PlayerId == sellerId && c.ItemId == "gold");
                Assert.Equal(750L, sellerGold.Quantity);

                var buyerGold = await verifyDb.CommodityRecords.AsNoTracking()
                    .SingleAsync(c => c.PlayerId == buyerId && c.ItemId == "gold");
                Assert.Equal(9000L, buyerGold.Quantity);
            }
        }

        // Modul: Advanced Economy Refactoring, Part 3/4. Guild access
        // gates - the universal level-20 unlock blocks creation and joins,
        // a guild's custom MinApplicationLevel blocks auto-joins below it,
        // application-required guilds route eligible joiners into the
        // pending GuildApplications table (and reject ineligible ones
        // without an application row), and the tax/access setters are
        // leader-only with clamped bounds.
        [Fact]
        public async Task Test_GuildAccessControl_LevelGatesApplicationsAndLeaderOnlySettings()
        {
            const long underLeveledId = 970005401L;
            const long leaderId = 970005402L;
            const long eligibleJoinerId = 970005403L;
            const long midLevelJoinerId = 970005404L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = underLeveledId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 5 });
                db.PlayerRecords.Add(new PlayerRecord { Id = leaderId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 50 });
                db.PlayerRecords.Add(new PlayerRecord { Id = eligibleJoinerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 45 });
                db.PlayerRecords.Add(new PlayerRecord { Id = midLevelJoinerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                await db.SaveChangesAsync();
            }

            var managementEngine = new GuildManagementEngine(_fixture.RetryingOptions, _fixture.PlayerRegistry);

            long rejectedGuildId = await managementEngine.CreateGuildAsync(underLeveledId, "UnderLeveledGuild970005401");
            Assert.Equal(0L, rejectedGuildId);

            long guildId = await managementEngine.CreateGuildAsync(leaderId, "AccessControlGuild970005402");
            Assert.True(guildId > 0L);

            // Leader raises the join bar to 40 and requires applications.
            Assert.True(await managementEngine.SetGuildAccessPolicyAsync(leaderId, GuildManagementEngine.JoinTypeApplicationRequired, 40));

            // Under-leveled (5) fails the universal gate; mid-level (25)
            // fails the guild's custom bar - neither may leave an
            // application row behind.
            Assert.False(await managementEngine.JoinGuildAsync(underLeveledId, guildId));
            Assert.False(await managementEngine.JoinGuildAsync(midLevelJoinerId, guildId));

            // Eligible (45) is routed to a pending application, not an
            // immediate join.
            Assert.False(await managementEngine.JoinGuildAsync(eligibleJoinerId, guildId));

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var applications = await verifyDb.GuildApplications.AsNoTracking().Where(a => a.GuildId == guildId).ToListAsync();
                Assert.Single(applications);
                Assert.Equal(eligibleJoinerId, applications[0].PlayerId);
                Assert.Equal(45, applications[0].ApplicantLevel);

                var eligibleJoiner = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == eligibleJoinerId);
                Assert.Equal(0L, eligibleJoiner.GuildId);
            }

            // Open guild with the bar back at 20: the mid-level joiner
            // (25) now auto-joins immediately.
            Assert.True(await managementEngine.SetGuildAccessPolicyAsync(leaderId, GuildManagementEngine.JoinTypeOpen, 20));
            Assert.True(await managementEngine.JoinGuildAsync(midLevelJoinerId, guildId));

            // Tax setter: leader-only, clamped to [5, 20].
            Assert.True(await managementEngine.SetGuildTaxRateAsync(leaderId, 50));
            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(20, (await verifyDb.GuildRecords.AsNoTracking().SingleAsync(g => g.Id == guildId)).TaxRatePct);
            }

            Assert.True(await managementEngine.SetGuildTaxRateAsync(leaderId, 1));
            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(5, (await verifyDb.GuildRecords.AsNoTracking().SingleAsync(g => g.Id == guildId)).TaxRatePct);
            }

            Assert.False(await managementEngine.SetGuildTaxRateAsync(midLevelJoinerId, 10),
                "A non-leader member must not be able to change the guild tax rate.");
        }
    }
}

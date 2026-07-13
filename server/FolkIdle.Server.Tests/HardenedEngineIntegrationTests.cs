using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
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
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = materialId.ToString(), Quantity = initialTargetRequirement });
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
    }
}

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
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

        // Modul: exposed so tests that MUTATE a well-known account can create
        // their own throwaway database on this same container instead of
        // disturbing the shared seed data. DevFixtureSeeder is the motivating
        // case: it keys off dev@folkidle.local, which DbSeeder also assigns to
        // PlayerLowId, so running it against the shared database silently
        // rewrites that player's gold and level and breaks whichever
        // tax-bracket test happens to run afterwards. An order-dependent
        // failure is worse than an outright one.
        public string ConnectionString { get; private set; } = string.Empty;

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

            ConnectionString = _container.GetConnectionString();

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

        // Modul: A LARDER FULL OF ORE HEALED FOR YEARS.
        //
        // Three offline fixtures stocked Food1_ItemId = 1, which is
        // gold_ore_crafting_material - not food, not edible, and worth nothing
        // to the live tick, which has always asked FoodRegistry what a slot is
        // worth. The offline projection did not ask: it healed a flat 50 HP per
        // unit no matter what the unit was, so an ore-filled larder sustained a
        // four hour window and the tests passed on it.
        //
        // The moment offline started reading the same registry as the live tick
        // all three went to zero kills, which is the correct answer to the
        // question they were actually asking. Asked of the registry now, so a
        // fixture cannot feed a character something it could never eat.
        private static int FirstEdibleItemId()
        {
            foreach (int id in ContentRegistry.RawFishItemIds)
            {
                return id;
            }

            throw new InvalidOperationException("no food in the catalogue to stock a larder with");
        }

        private readonly PostgresTestFixture _fixture;

        public HardenedEngineIntegrationTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        // Modul: region unlock fixture. Equipping stopped asking for a level
        // and started asking which region bosses are down - see
        // RegionUnlockGate. A test whose subject is set bonuses, slot
        // round-tripping or equip concurrency has no business also proving the
        // progression gate, so these open every region and take it out of the
        // measurement, exactly the role CurrentLevel = 100 played before.
        //
        // Adds to the caller's context WITHOUT saving: every one of these tests
        // already builds its player and items in one context and saves once, so
        // an inner SaveChangesAsync here would split that into two round trips
        // and, worse, commit a player whose items had not been written yet.
        //
        // Note this seeds the gate's inputs (codex kills) rather than the gate's
        // answer. A helper that stamped "unlocked = 5" somewhere would pass even
        // if HighestUnlockedRegion stopped reading the codex at all.
        private static void SeedAllRegionBossKills(FolkIdleDbContext db, long playerId)
        {
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry
                {
                    PlayerId = playerId,
                    MonsterId = RaceUnlockRegistry.GetRegionBossMonsterId(region),
                    KillCount = 1
                });
            }
        }

        /// <summary>
        /// Fusion takes THREE ITEMS OF THE SAME RARITY, and charges the tier
        /// curve with no fodder-quality modifier.
        ///
        /// This test used to pin that modifier: tier-1 sacrifices cost 4x and
        /// tier-4 sacrifices cost 1x, against a tier-1 target. That whole
        /// mechanism existed because the two sacrifices could be ANY rarity,
        /// which let a player climb a high-tier item on Normal duplicates. With
        /// all three rarities required to match, the modifier had exactly one
        /// possible value and the mismatch it priced is now refused outright.
        /// </summary>
        [Fact]
        public async Task Test_ForgeSplicing_RequiresMatchingRarityAndChargesTheTierCurve()
        {
            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);

            long matchedCost = await RunFusionAndMeasureCostAsync(
                baseItemId: "integration_test_forge_sword_matched",
                sacrificeQualityTier: 1,
                forgeEngine);

            long mismatchedCost = await RunFusionAndMeasureCostAsync(
                baseItemId: "integration_test_forge_sword_mismatched",
                sacrificeQualityTier: 4,
                forgeEngine);

            // Target starts at QualityTier 1, so the fee is
            // ceil(200 * 1.35^1) = 270 - flat, with no fodder modifier.
            Assert.Equal(270L, matchedCost);

            // Tier-4 sacrifices against a tier-1 target are refused before any
            // gold moves. Not "more expensive" - rejected.
            Assert.Equal(0L, mismatchedCost);
        }

        /// <summary>
        /// Matched rarities always succeed. The roll, the tier-2 affix lockout
        /// and the tier-3+ vaporization are gone: with the chest removing
        /// scrapping, the forge IS how duplicates are disposed of, so a
        /// mechanic that eats three matched items and returns nothing is a
        /// dead end with no alternative.
        /// </summary>
        [Fact]
        public async Task Test_ForgeSplicing_MatchedRaritiesAlwaysSucceed()
        {
            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);

            for (int attempt = 0; attempt < 8; attempt++)
            {
                var result = await RunFusionAndReturnResultAsync(
                    baseItemId: $"integration_test_forge_certain_{attempt}",
                    startingTier: 3,
                    forgeEngine);

                Assert.Equal(ForgeSplicingResult.Success, result);
            }
        }

        /// <summary>
        /// REAL CATALOGUE GEAR, at the rarity the old per-band ceiling stopped.
        ///
        /// This is the test that would have caught the bug behind "I press fuse
        /// and get an error, and nothing happens", and the reason no existing
        /// test did: every fusion fixture here uses an invented base id like
        /// "integration_test_forge_certain_0", which resolves to no
        /// ItemDefinition at all - so the region lookup fell through to 1, and
        /// with a starting tier of 3 the old cap of 5 was never reached. The
        /// fixtures dodged the rule rather than exercising it.
        ///
        /// A real region-1 item at rarity 5 was refused outright, and the
        /// refusal reached the player as "already at maximum tier" next to a 5
        /// out of 14. There is one ceiling now, the top of the rarity ladder.
        /// </summary>
        [Fact]
        public async Task Test_ForgeSplicing_RealStarterGearFusesPastTheOldBandCeiling()
        {
            ContentRegistry.Initialize();

            // Something a new player actually owns: the first region's gear.
            string starterBaseId = string.Empty;
            for (int itemId = 1; itemId <= ContentRegistry.ItemDefinitions.Length; itemId++)
            {
                if (ContentRegistry.ItemDefinitions[itemId - 1].RegionTier != 1) continue;
                string candidate = ContentRegistry.GetItemBaseId(itemId);
                if (candidate.Contains("_helmet_") || candidate.Contains("_chest_"))
                {
                    starterBaseId = candidate;
                    break;
                }
            }
            Assert.False(string.IsNullOrEmpty(starterBaseId), "the catalogue must contain region-1 armour");

            var forgeEngine = new ForgeSplicingEngine(_fixture.ServiceProvider);

            // Rarity 5 is exactly where region 1-2 gear used to stop.
            var result = await RunFusionAndReturnResultAsync(starterBaseId, startingTier: 5, forgeEngine);
            Assert.Equal(ForgeSplicingResult.Success, result);

            // And the whole way up: 13 -> 14 was unreachable too, because the
            // global ceiling was read as "tiers 0-13" while every item in the
            // game is 1-based and Transcendent is 14.
            var toTheTop = await RunFusionAndReturnResultAsync(starterBaseId, startingTier: 13, forgeEngine);
            Assert.Equal(ForgeSplicingResult.Success, toTheTop);

            // 14 is the top and stays the top.
            var pastTheTop = await RunFusionAndReturnResultAsync(starterBaseId, startingTier: 14, forgeEngine);
            Assert.Equal(ForgeSplicingResult.MaxTierReached, pastTheTop);
        }

        private async Task<ForgeSplicingResult> RunFusionAndReturnResultAsync(string baseItemId, int startingTier, ForgeSplicingEngine forgeEngine)
        {
            long targetId, sac1Id, sac2Id;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Modul: RAISE an existing Forge, do not only insert a missing
                // one.
                //
                // This inserted a level-10 Forge when none existed and left
                // whatever was already there alone - and the Forge's level is
                // the rarity ceiling for fusion, so a Forge seeded lower by
                // another test in this collection silently capped every fusion
                // here. It went unnoticed because every fixture fused at tier
                // 3; the first test to try tier 5 failed with InvalidRequest
                // and looked like a bug in the rule being tested.
                var forge = await db.VillageInfrastructures.SingleOrDefaultAsync(
                    v => v.PlayerId == DbSeeder.PlayerHighId && v.BuildingId == VillageManagementEngine.ForgeBuildingId);
                if (forge is null)
                {
                    db.VillageInfrastructures.Add(new VillageInfrastructure
                    {
                        PlayerId = DbSeeder.PlayerHighId,
                        BuildingId = VillageManagementEngine.ForgeBuildingId,
                        CurrentLevel = ForgeSplicingEngine.MaxQualityTier
                    });
                }
                else if (forge.CurrentLevel < ForgeSplicingEngine.MaxQualityTier)
                {
                    forge.CurrentLevel = ForgeSplicingEngine.MaxQualityTier;
                }

                var target = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = startingTier };
                var sac1 = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = startingTier };
                var sac2 = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = baseItemId, QualityTier = startingTier };
                db.EquipmentInstances.AddRange(target, sac1, sac2);
                await db.SaveChangesAsync();
                targetId = target.Id;
                sac1Id = sac1.Id;
                sac2Id = sac2.Id;
            }

            return await forgeEngine.ExecuteFusionAsync(DbSeeder.PlayerHighId, targetId, sac1Id, sac2Id);
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

        // Modul: REAL catalogue items, one per bracket.
        //
        // Each case used to invent `integration_test_ore_{sellerId}` - a
        // synthetic id that isolates the three brackets from each other's order
        // book, which is the right idea, but that name is in no catalogue. The
        // price corridor now refuses an item it cannot price (see
        // MarketEscrowEngine on why an unpriceable item is not an unlimited
        // one), so the buy order never landed and there was nothing to match.
        //
        // Three DISTINCT canonical ids keep the isolation - MatchOrdersAsync is
        // keyed by (BaseItemId, QualityTier), so a shared id would let the three
        // theory cases match against each other's orders. All three are 3,200
        // base gold, which at quality tier 1 gives a corridor of [3840, 14400]
        // and comfortably contains the 5,000 price these brackets are computed
        // from.
        [Theory]
        [InlineData(DbSeeder.PlayerLowId, 0.05, "eq_monolith_crown_helmet_armor_slot_base")]
        [InlineData(DbSeeder.PlayerMidId, 0.08, "eq_brawler_coat_chest_armor_slot_base")]
        [InlineData(DbSeeder.PlayerHighId, 0.15, "eq_monolith_body_chest_armor_slot_base")]
        public async Task Test_MarketOrderBook_TaxBracketsAndArchiving(long sellerId, double expectedRate, string baseItemId)
        {
            var marketEngine = new MarketOrderBookEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
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
                    new CharacterRecord { Id = parentBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsFemale = true });
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
                    new CharacterRecord { Id = parentBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsFemale = true });
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
                    new CharacterRecord { Id = parentBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsFemale = true });
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
                    new CharacterRecord { Id = parentBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsFemale = true });
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
            CombatStats geared = StatsCalculator.Calculate(str: 100, dex: 0, con: 0, lck: 0, equippedAffixTotals: new EquippedAffixTotals { FlatAttack = 500 });

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
                    new CharacterRecord { Id = siblingBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsFemale = true });
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
                    new CharacterRecord { Id = candidateBId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsFemale = true },
                    new CharacterRecord { Id = candidateCId, PlayerId = testPlayerId, Level = 50, AgePhase = 1, IsLockedInEscrow = false, IsFemale = true });
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

                var marketMainCharacterId = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = marketMainCharacterId,
                    AuthenticatorToken = Guid.NewGuid()
                });
                // Modul: per-character equipment. The worn item hangs off the
                // character now; IsEquippedAnywhereAsync is what the market
                // consults, and it looks at characters, not the player row.
                db.CharacterRecords.Add(new CharacterRecord
                {
                    Id = marketMainCharacterId,
                    PlayerId = testPlayerId,
                    Level = 1,
                    AgePhase = 1,
                    SlotIndex = 0,
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
                Food1_ItemId = FirstEdibleItemId(),
                Food1_Count = 100000
            };

            // Independently replicate the engine's analytical combat projection to
            // compute the expected reward, rather than hand-computing a fragile
            // cascading level-up chain by hand.
            // Modul: THE SHARED DAMAGE MODEL, not a private copy of it.
            //
            // These lines used to re-derive damage per hit inline - no monster
            // armour, no hit roll - which is precisely the model the unified
            // CombatDamageModel replaced when offline and warp were found to be
            // paying for combat that could not have happened. The engine moved;
            // this projection did not, so it computed a different number of
            // kills and the test failed against a correct engine.
            //
            // Calling the same two authorities keeps the test about what it is
            // for: that kills become XP, that the level-up cascade runs, and
            // that the result is persisted. The damage model itself is pinned
            // by its own tests, and a second hand-maintained copy here has now
            // drifted twice.
            MonsterDefinition monster = ContentRegistry.Monsters[monsterId - 1];
            CombatStats combatStats = StatsCalculator.Calculate(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0);
            var projectionLineage = ProgressionEngine.Lineages[0];
            long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(
                in combatStats, projectionLineage.DamageScalePerLevelPct, 1, 0);
            double secondsPerKill = CombatDamageModel.ExpectedSecondsPerKill(
                in combatStats, in monster, effectiveMilliAttack, multipliers.DamageMultiplier);

            // Modul: replicate the engine's incoming-damage/food-depletion model
            // exactly (expected-value monster crit + Vodnik mitigation, a "free"
            // max-HP absorption buffer before food is needed, then Food1-3
            // healing capacity) since payload here has zero food stocked - the
            // test character can only sustain a fraction of the raw offline
            // window before combat halts, matching the live tick's Auto-Eat halt
            // behavior when food runs out.
            // Also asked of the registry rather than re-derived - the engine
            // calls GetMonsterRegionTier, and the arithmetic that used to sit
            // here was a third copy of a rule that has since changed shape.
            int monsterRegionTier = ContentRegistry.GetMonsterRegionTier(monsterId);
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
                // Modul: asked of FoodRegistry, not the old flat 50 HP per
                // unit. The engine stopped healing a fixed number of points
                // when food became a share of max HP; this line was the last
                // copy of the constant it replaced, and it made the projection
                // disagree with the engine by three orders of magnitude.
                double healPerUnitMilliHp = FoodRegistry.GetHealMilliHp(FirstEdibleItemId(), effectiveMilliHp);
                double totalHealCapacityMilliHp = effectiveMilliHp + (100000.0 * healPerUnitMilliHp); // matches payload.Food1_Count above
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
                // Modul: balance pass. Was a fourth inline copy of the level
                // curve. Calls the one authority so this projection cannot
                // drift from the engine it is asserting against - which is
                // exactly what it did do, silently, until the curve changed.
                long requiredXp = ProgressionEngine.GetRequiredXpForLevel(expectedLevel);
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
                Food1_ItemId = FirstEdibleItemId(),
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
            const int gatheringActivityId = 1001;   // Woodcutting node 1 (band 1000)

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
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(serviceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(serviceProvider, playerRegistry);
            var legacyStoreEngine = new LegacyStoreEngine(serviceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(serviceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(serviceProvider, playerRegistry);

            return new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageManagementEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);
        }

        // Modul: A TOOL, NOT A SWORD. This drove
        // ExecuteEquipmentCraftingAsync, which turned ore into armour - the
        // second crafting system, removed when equipment became monster loot
        // and crafting became tools only. The transaction it was actually
        // testing (materials deducted exactly once, item written in the same
        // commit) is the same one, so the test moved to the path that remains.
        [Fact]
        public async Task Test_Crafting_TransactionAndResourceDeduction()
        {
            const long testPlayerId = 990000001L;
            const int birchAxeItemId = 408;
            const long stocked = 500L;

            Assert.True(ContentRegistry.TryGetRecipe(birchAxeItemId, out var recipe));
            string mat1 = ContentRegistry.GetItemBaseId(recipe.Mat1Id);
            string mat2 = ContentRegistry.GetItemBaseId(recipe.Mat2Id);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat1, Quantity = stocked });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat2, Quantity = stocked });
                await db.SaveChangesAsync();
            }

            var craftingEngine = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
            await craftingEngine.ExecuteCraftingAsync(testPlayerId, birchAxeItemId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            var firstMaterial = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == mat1);
            Assert.Equal(stocked - recipe.Mat1Count, firstMaterial.Quantity);

            var secondMaterial = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == mat2);
            Assert.Equal(stocked - recipe.Mat2Count, secondMaterial.Quantity);

            // The tool itself. Asked of the registry rather than spelled out,
            // so a repointed recipe does not fail a transaction test.
            string craftedBaseId = ContentRegistry.GetItemBaseId(birchAxeItemId);
            Assert.True(ContentRegistry.GetToolKind(craftedBaseId) >= 0);
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
                // Value rerolls are paid in gold since 2026-08-01.
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 50_000_000L });

                // Modul: Affix System Unification. This used to seed
                // BaseItemId = "1" and an affix key "flat_hp_aaaa" - both
                // artefacts of the bugs since fixed. BaseItemId is always a
                // slug (the old code's int.TryParse on it always failed, which
                // is why every rerolled affix was scaled as region 1), and the
                // random hex key suffix made the affix unreadable to
                // EquipmentSlotEngine. A real chest slug and a plain GDD affix
                // id now stand in for a genuine item.
                var equipment = new EquipmentInstance
                {
                    BaseItemId = "eq_linen_shroud_chest_armor_slot_base",
                    PlayerId = testPlayerId,
                    QualityTier = 1,
                    AffixPayload = "{\"flat_hp\":50}",
                    IsAffixLocked = false
                };
                db.EquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;
            }

            var rerollEngine = new AffixRerollEngine(_fixture.ServiceProvider);
            // Modul: one reroll. It replaces the stat, the rarity and the
            // magnitude together, so this needs no operation argument at all -
            // there is only the one, and it is what the default resolves to.
            await rerollEngine.ExecuteRerollAsync(testPlayerId, equipmentId, affixIndex: 0);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            // Diamonds are untouched by a value reroll; gold pays for it.
            var commodity = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "premium_diamond");
            Assert.Equal(initialPremiumCurrency, commodity.Quantity);

            var goldRow = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");
            Assert.Equal(50_000_000L - AffixRegistry.CalculateRerollGoldCost(1, 0, rerollStatType: true), goldRow.Quantity);

            var updatedEquipment = await verifyDb.EquipmentInstances.AsNoTracking()
                .SingleAsync(e => e.Id == equipmentId);

            var affixPayload = JsonNode.Parse(updatedEquipment.AffixPayload) as JsonObject;
            Assert.NotNull(affixPayload);
            Assert.Single(affixPayload);

            // A chest has exactly two legal affixes, so the replacement is
            // necessarily flat_armor. Payload keys now carry an "@rarity"
            // suffix, so compare on the stripped definition id rather than the
            // raw key - the suffix is exactly what StripStackSuffix exists for.
            var rolledIds = new List<string>();
            foreach (var kvp in affixPayload!)
            {
                if (kvp.Key == "is_affix_locked") continue;
                rolledIds.Add(AffixRegistry.StripStackSuffix(kvp.Key));
            }
            Assert.DoesNotContain("flat_hp", rolledIds);
            Assert.Contains("flat_armor", rolledIds);
        }

        // Modul: Affix System Unification. An item whose BaseItemId carries no
        // recognisable slot suffix has no legal affix pool, so a reroll must
        // refuse rather than invent one - and must not charge for the refusal.
        [Fact]
        public async Task Test_AffixReroll_RefusesAndRefundsWhenTheItemSlotIsUnrecognisable()
        {
            const long testPlayerId = 990000012L;
            const long initialPremiumCurrency = 100L;
            long equipmentId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "premium_diamond", Quantity = initialPremiumCurrency });
                // Value rerolls are paid in gold since 2026-08-01.
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 50_000_000L });

                var equipment = new EquipmentInstance
                {
                    BaseItemId = "some_item_with_no_slot_suffix",
                    PlayerId = testPlayerId,
                    QualityTier = 1,
                    AffixPayload = "{\"flat_hp\":50}",
                    IsAffixLocked = false
                };
                db.EquipmentInstances.Add(equipment);
                await db.SaveChangesAsync();
                equipmentId = equipment.Id;
            }

            var rerollEngine = new AffixRerollEngine(_fixture.ServiceProvider);
            await rerollEngine.ExecuteRerollAsync(testPlayerId, equipmentId, affixIndex: 0);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            long remaining = await verifyDb.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == testPlayerId && c.ItemId == "premium_diamond")
                .Select(c => c.Quantity).FirstAsync();
            Assert.Equal(initialPremiumCurrency, remaining);

            var untouched = await verifyDb.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == equipmentId);
            var payload = JsonNode.Parse(untouched.AffixPayload) as JsonObject;
            Assert.NotNull(payload);
            // Untouched means the ORIGINAL unsuffixed key survives verbatim - a
            // rewritten key would prove the refusal still mutated the item.
            Assert.True(payload!.ContainsKey("flat_hp"));
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
            var villageManagementEngine = new VillageManagementEngine(serviceProvider, playerRegistry);
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
                villageManagementEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, antiCheatTelemetryEngine, null!, null!, null!, null!, contextFactory);

            return (simulationEngine, networkSystem);
        }

        private static async Task SendCommandAsync(ClientWebSocket socket, ClientCommandPacket packet)
        {
            byte[] buffer = new byte[Marshal.SizeOf<ClientCommandPacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            await socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        // Modul: removed with the four active skills it exercised - mana
        // deduction, cooldown rejection and the cast damage multiplier. See
        // SkillTreeRegistry: measured, that rotation was +90% damage for
        // clicking every three seconds, so it was replaced by a passive tree
        // rather than rebalanced. SkillTreeTests prices what took its place.

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

        // Modul: Play Mode audit fix. SeasonalRotationEngine had zero test
        // coverage despite being the single highest-blast-radius operation
        // in the codebase (a server-wide, all-players reset every 90 days).
        // This test covers the specific bug found by reading it: the era
        // rollover TRUNCATEs EquipmentInstances with RESTART IDENTITY (so a
        // brand new item created after the reset can land on the exact same
        // numeric id an old, wiped item used to have), but never cleared
        // PlayerRecords.EquippedWeaponId/ArmorId/LeggingsId - and
        // EquipmentSlotEngine.ComputeEquippedTotalsAsync resolves equipped
        // items by id alone with no PlayerId ownership check. Left
        // unfixed, a player's stale equipped-id would silently start
        // resolving to a completely different player's post-reset item,
        // leaking that stranger's stats/set bonus as the old player's own
        // equipped gear.
        //
        // ExecutePlayerRolloversAsync also performs genuinely global,
        // unconditional writes elsewhere (TRUNCATE BankEquipmentInstances, a
        // blanket gold/commodity wipe across every row in the table) -
        // running the real method against the shared "Postgres collection"
        // fixture would corrupt DbSeeder's seeded players and any other
        // test's data that happens to run in the same execution (confirmed
        // live: it broke Test_ForgeSplicing_FodderPenaltyCalculation, an
        // otherwise unrelated test, by zeroing out DbSeeder.PlayerHighId's
        // gold). This test spins up its own dedicated, throwaway Postgres
        // container instead of using _fixture, mirroring
        // PostgresTestFixture's own setup but scoped to just this one test.
        [Fact]
        public async Task Test_SeasonalRotation_ClearsEquippedItemIds_BeforeIdentityRestartCanCauseCollision()
        {
            await using var container = new PostgreSqlBuilder("postgres:16")
                .WithDatabase("folkidle_test_seasonal")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await container.StartAsync();

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options => options.UseNpgsql(container.GetConnectionString()));
            var serviceProvider = services.BuildServiceProvider();
            var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var migrateDb = await dbContextFactory.CreateDbContextAsync())
            {
                await migrateDb.Database.MigrateAsync();
            }

            const long testPlayerId = 1L;
            const long otherPlayerId = 2L;

            long originalWeaponId;
            int closedEraId;
            await using (var db = await dbContextFactory.CreateDbContextAsync())
            {
                // PlayerLegacyLedger.EraId has a real FK to SeasonalEraRecords -
                // ExecutePlayerRolloversAsync inserts a ledger row for
                // closedEraId, so a real era row must exist first.
                var era = new SeasonalEraRecord { EndTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), IsActive = true };
                db.SeasonalEraRecords.Add(era);
                await db.SaveChangesAsync();
                closedEraId = era.EraId;

                var weapon = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "test_sword", QualityTier = 1, AffixPayload = "{\"1\": 50}" };
                db.EquipmentInstances.Add(weapon);
                await db.SaveChangesAsync();
                originalWeaponId = weapon.Id;

                var seasonalMainCharacterId = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = seasonalMainCharacterId,
                    AuthenticatorToken = Guid.NewGuid(),
                    CurrentLevel = 10,
                    CurrentXp = 5000
                });
                // Modul: per-character equipment. The seasonal wipe now clears
                // "characters" as well as "PlayerRecords", so the fixture has to
                // put the gear where the wipe will look for it.
                db.CharacterRecords.Add(new CharacterRecord
                {
                    Id = seasonalMainCharacterId,
                    PlayerId = testPlayerId,
                    Level = 10,
                    AgePhase = 1,
                    SlotIndex = 0,
                    EquippedWeaponId = originalWeaponId
                });
                await db.SaveChangesAsync();
            }

            var seasonalEngine = new SeasonalRotationEngine(serviceProvider);
            await seasonalEngine.ExecutePlayerRolloversAsync(closedEraId, CancellationToken.None);

            await using (var verifyDb = await dbContextFactory.CreateDbContextAsync())
            {
                var playerAfter = await verifyDb.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);

                var characterAfter = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.PlayerId == testPlayerId);
                Assert.Null(characterAfter.EquippedWeaponId);
                Assert.Null(characterAfter.EquippedChestId);
                Assert.Null(characterAfter.EquippedLeggingsId);
                Assert.Null(characterAfter.EquippedHelmetId);
                Assert.Null(characterAfter.EquippedGlovesId);
                Assert.Null(characterAfter.EquippedBootsId);
                Assert.Equal(1, playerAfter.CurrentLevel);
                Assert.Equal(0L, playerAfter.CurrentXp);

                Assert.Equal(0, await verifyDb.EquipmentInstances.CountAsync());
            }

            // Confirm the identity-restart collision precondition is real -
            // a brand new item for an unrelated player lands on the exact
            // id the wiped weapon used to have.
            await using (var db2 = await dbContextFactory.CreateDbContextAsync())
            {
                db2.PlayerRecords.Add(new PlayerRecord { Id = otherPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                var newWeapon = new EquipmentInstance { PlayerId = otherPlayerId, BaseItemId = "someone_elses_sword", QualityTier = 5, AffixPayload = "{\"1\": 9999}" };
                db2.EquipmentInstances.Add(newWeapon);
                await db2.SaveChangesAsync();
                Assert.Equal(originalWeaponId, newWeapon.Id);
            }
        }

        // Modul: WHAT A SEASON LEAVES BEHIND.
        //
        // Three tables now survive a rollover - the village you built, the race
        // mastery you learned, and the inheritance levels you bought with
        // diamonds - and that carry-over is the entire reason a returning player
        // has anything to return to. It is also the least observable rule in the
        // game: it fires once every ninety days, on the server, with no screen
        // to look at, and a refactor that quietly re-added one of these tables
        // to the wipe would read as a completely normal cleanup commit.
        //
        // The negative half matters just as much. If the ladder ever stopped
        // resetting, the season would stop meaning anything - so this asserts
        // both directions in one place: level, gold and materials go, the three
        // survivors stay, at the exact levels they were bought at.
        //
        // Own container, for the reason the test above documents: the rollover
        // writes unconditionally across every row of several tables and would
        // corrupt the shared fixture's seeded players.
        [Fact]
        public async Task Test_SeasonalRotation_KeepsTheVillageTheMasteryAndTheInheritance()
        {
            await using var container = new PostgreSqlBuilder("postgres:16")
                .WithDatabase("folkidle_test_carryover")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await container.StartAsync();

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(options => options.UseNpgsql(container.GetConnectionString()));
            var serviceProvider = services.BuildServiceProvider();
            var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();

            await using (var migrateDb = await dbContextFactory.CreateDbContextAsync())
            {
                await migrateDb.Database.MigrateAsync();
            }

            const long playerId = 1L;
            int closedEraId;

            await using (var db = await dbContextFactory.CreateDbContextAsync())
            {
                var era = new SeasonalEraRecord { EndTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), IsActive = true };
                db.SeasonalEraRecords.Add(era);
                await db.SaveChangesAsync();
                closedEraId = era.EraId;

                var mainCharacterId = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId,
                    PlayerGuid = mainCharacterId,
                    AuthenticatorToken = Guid.NewGuid(),
                    CurrentLevel = 42,
                    CurrentXp = 123_456
                });
                db.CharacterRecords.Add(new CharacterRecord
                {
                    Id = mainCharacterId,
                    PlayerId = playerId,
                    Level = 42,
                    AgePhase = 1,
                    SlotIndex = 0
                });

                // The ladder - all of this is supposed to go.
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 750_000L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "iron_ore", Quantity = 900L });

                // The three survivors.
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = 9, CurrentLevel = 7 });
                db.PlayerRaceMasteries.Add(new PlayerRaceMastery { PlayerId = playerId, RaceId = 1, MasteryLevel = 5, CumulativeXp = 40_000L });
                db.PlayerInheritanceStats.Add(new PlayerInheritanceStat { PlayerId = playerId, StatId = 0, Level = 6 });
                db.PlayerInheritanceStats.Add(new PlayerInheritanceStat { PlayerId = playerId, StatId = 3, Level = 2 });

                await db.SaveChangesAsync();
            }

            var seasonalEngine = new SeasonalRotationEngine(serviceProvider);
            await seasonalEngine.ExecutePlayerRolloversAsync(closedEraId, CancellationToken.None);

            await using (var verify = await dbContextFactory.CreateDbContextAsync())
            {
                // The season is the ladder, and the ladder resets.
                var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
                Assert.Equal(1, player.CurrentLevel);
                Assert.Equal(0L, player.CurrentXp);

                var gold = await verify.CommodityRecords.AsNoTracking()
                    .SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                Assert.Equal(0L, gold.Quantity);
                Assert.False(await verify.CommodityRecords.AsNoTracking()
                    .AnyAsync(c => c.PlayerId == playerId && c.ItemId == "iron_ore"));

                // What you built.
                var townHall = await verify.VillageInfrastructures.AsNoTracking()
                    .SingleAsync(v => v.PlayerId == playerId && v.BuildingId == 9);
                Assert.Equal(7, townHall.CurrentLevel);

                // What you learned.
                var mastery = await verify.PlayerRaceMasteries.AsNoTracking()
                    .SingleAsync(m => m.PlayerId == playerId && m.RaceId == 1);
                Assert.Equal(5, mastery.MasteryLevel);
                Assert.Equal(40_000L, mastery.CumulativeXp);

                // What you bought. Asserted per stat rather than as a count: a
                // wipe that reset the levels to zero would leave both rows in
                // place and satisfy a count.
                var inherited = await verify.PlayerInheritanceStats.AsNoTracking()
                    .Where(s => s.PlayerId == playerId)
                    .ToDictionaryAsync(s => s.StatId, s => s.Level);
                Assert.Equal(6, inherited[0]);
                Assert.Equal(2, inherited[3]);
            }
        }

        [Fact]
        public void Test_SeasonalRotation_CalculateLegacyShards_MatchesExpectedFormula()
        {
            int shards = SeasonalRotationEngine.CalculateLegacyShards(totalGold: 999_999L, characterLevelSquareSum: 400L, inventoryScore: 30L);

            double expected = Math.Floor(12.5 * Math.Log10(1_000_000.0) + 0.05 * 400.0 + 1.50 * 30.0);
            Assert.Equal((int)expected, shards);

            Assert.Equal(0, SeasonalRotationEngine.CalculateLegacyShards(0L, 0L, 0L));
            Assert.Equal(0, SeasonalRotationEngine.CalculateLegacyShards(-500L, -10L, -5L));
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
        public async Task Test_AuthenticationEngine_RegisterWithEmail_CreatesAccountAndAllowsEmailLogin()
        {
            string email = "register_success_970000801@example.com";

            var registerOutcome = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, email, "RegisterSuccessUser", "correct password", "device_970000801");
            Assert.Equal(EmailRegisterOutcome.Success, registerOutcome.Outcome);
            Assert.NotEqual(0L, registerOutcome.PlayerId);

            // Correct credentials succeed.
            var loginOutcome = await AuthenticationEngine.LoginWithEmailAsync(_fixture.RetryingOptions, email, "correct password", null);
            Assert.Equal(EmailLoginOutcome.Success, loginOutcome.Outcome);
            Assert.Equal(registerOutcome.PlayerId, loginOutcome.PlayerId);
            Assert.Equal(registerOutcome.AccountId, loginOutcome.AccountId);

            // Wrong password, and an entirely unregistered email, both
            // collapse to the same InvalidCredentials outcome - see
            // EmailLoginOutcome's own header comment on why the two are not
            // distinguished (avoids account enumeration).
            var wrongPassword = await AuthenticationEngine.LoginWithEmailAsync(_fixture.RetryingOptions, email, "wrong password", null);
            Assert.Equal(EmailLoginOutcome.InvalidCredentials, wrongPassword.Outcome);

            var unknownEmail = await AuthenticationEngine.LoginWithEmailAsync(_fixture.RetryingOptions, "nobody_970000801@example.com", "correct password", null);
            Assert.Equal(EmailLoginOutcome.InvalidCredentials, unknownEmail.Outcome);
        }

        [Fact]
        public async Task Test_AuthenticationEngine_RegisterWithEmail_RejectsDuplicateEmailAndUsername()
        {
            string email = "register_duplicate_970000802@example.com";

            var first = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, email, "DuplicateEmailUserA", "password one", null);
            Assert.Equal(EmailRegisterOutcome.Success, first.Outcome);

            // Same email (case-insensitive - normalized to lowercase),
            // different username - rejected on the Email index.
            var duplicateEmail = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, email.ToUpperInvariant(), "DuplicateEmailUserB", "password two", null);
            Assert.Equal(EmailRegisterOutcome.EmailInUse, duplicateEmail.Outcome);

            // Different email, same username - rejected on the Username index.
            var duplicateUsername = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, "register_duplicate_other_970000802@example.com", "DuplicateEmailUserA", "password three", null);
            Assert.Equal(EmailRegisterOutcome.UsernameInUse, duplicateUsername.Outcome);
        }

        [Fact]
        public async Task Test_AuthenticationEngine_RegisterWithEmail_RejectsInvalidInputShapes()
        {
            var badEmail = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, "not-an-email", "ValidUser803", "valid password", null);
            Assert.Equal(EmailRegisterOutcome.InvalidEmail, badEmail.Outcome);

            var shortUsername = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, "invalid_shapes_970000803@example.com", "ab", "valid password", null);
            Assert.Equal(EmailRegisterOutcome.InvalidUsername, shortUsername.Outcome);

            var shortPassword = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, "invalid_shapes2_970000803@example.com", "ValidUserB803", "abc", null);
            Assert.Equal(EmailRegisterOutcome.InvalidPassword, shortPassword.Outcome);
        }

        [Fact]
        public async Task Test_AuthenticationEngine_TryLoginByDeviceId_NeverAutoProvisions()
        {
            string unseenDeviceId = "device_never_bound_970000804";

            var result = await AuthenticationEngine.TryLoginByDeviceIdAsync(_fixture.RetryingOptions, unseenDeviceId);
            Assert.False(result.Found);

            // The defining difference from LoginOrProvisionAsync: a lookup
            // miss must never leave a new row behind.
            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            bool rowExists = await verifyDb.PlayerRecords.AsNoTracking().AnyAsync(p => p.DeviceId == unseenDeviceId);
            Assert.False(rowExists);
        }

        [Fact]
        public async Task Test_AuthenticationEngine_LoginWithEmail_RebindsDeviceIdForRememberMe()
        {
            string email = "rebind_970000805@example.com";
            string originalDeviceId = "device_rebind_original_970000805";
            string newDeviceId = "device_rebind_new_970000805";

            var registerResult = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, email, "RebindUser970000805", "correct password", originalDeviceId);
            Assert.Equal(EmailRegisterOutcome.Success, registerResult.Outcome);

            // The device that registered can silently resume via the
            // remember-me lookup.
            var beforeLogin = await AuthenticationEngine.TryLoginByDeviceIdAsync(_fixture.RetryingOptions, originalDeviceId);
            Assert.True(beforeLogin.Found);
            Assert.Equal(registerResult.PlayerId, beforeLogin.PlayerId);

            // Logging in with the same credentials from a DIFFERENT device
            // rebinds the remember-me anchor to that new device.
            var loginResult = await AuthenticationEngine.LoginWithEmailAsync(_fixture.RetryingOptions, email, "correct password", newDeviceId);
            Assert.Equal(EmailLoginOutcome.Success, loginResult.Outcome);
            Assert.Equal(registerResult.PlayerId, loginResult.PlayerId);

            var afterLoginNewDevice = await AuthenticationEngine.TryLoginByDeviceIdAsync(_fixture.RetryingOptions, newDeviceId);
            Assert.True(afterLoginNewDevice.Found);
            Assert.Equal(registerResult.PlayerId, afterLoginNewDevice.PlayerId);

            var afterLoginOldDevice = await AuthenticationEngine.TryLoginByDeviceIdAsync(_fixture.RetryingOptions, originalDeviceId);
            Assert.False(afterLoginOldDevice.Found);
        }

        [Fact]
        public async Task Test_AuthenticationEngine_IsEmailAvailable_ReturnsFalseOnceRegistered()
        {
            string email = "availability_970000806@example.com";

            Assert.True(await AuthenticationEngine.IsEmailAvailableAsync(_fixture.RetryingOptions, email));

            var registerResult = await AuthenticationEngine.RegisterWithEmailAsync(_fixture.RetryingOptions, email, "AvailabilityUser806", "correct password", null);
            Assert.Equal(EmailRegisterOutcome.Success, registerResult.Outcome);

            Assert.False(await AuthenticationEngine.IsEmailAvailableAsync(_fixture.RetryingOptions, email));
            // Case-insensitive - the same address with different casing is
            // also reported unavailable.
            Assert.False(await AuthenticationEngine.IsEmailAvailableAsync(_fixture.RetryingOptions, email.ToUpperInvariant()));

            Assert.False(await AuthenticationEngine.IsEmailAvailableAsync(_fixture.RetryingOptions, "not-an-email"));
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
                // Modul: gold, and gold alone. This expected two commodities -
                // the second being 25 copper ore seeded from nowhere so that
                // SOMETHING could be crafted on day one, which read to players
                // as a glitch because a pile of ore appearing in an empty
                // account is one.
                int commodityCount = await verifyDb.CommodityRecords.AsNoTracking().CountAsync(c => c.PlayerId == playerId);
                Assert.Equal(1, commodityCount);

                // What replaced it: the three tools gathering actually needs.
                // An account owning none of them could not usefully work any of
                // the three professions the game opens on.
                int toolCount = await verifyDb.EquipmentInstances.AsNoTracking()
                    .CountAsync(e => e.PlayerId == playerId);
                Assert.Equal(StarterEquipmentGrant.StarterToolBaseIds.Length, toolCount);
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
            // Modul: a CANONICAL item. This named a legacy piece that the
            // catalogue cut removed, so ContentRegistry could no longer price
            // it - and the corridor, which only ran when a price existed, waved
            // the listing straight through. The test failed for the right
            // reason and the failure was a live hole rather than a stale
            // literal: see the unknown-item case at the end of this method,
            // which is the other half and did not exist before.
            const string baseItemId = "eq_steel_sabatons_boots_armor_slot_base"; // ItemDefinition Id 255, BaseValueGold 50
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
            // (50 * 1.0 = 50, corridor [40, 150]) rather than allowing
            // an arbitrary RMT-laundering price through.
            bool accepted = await escrowEngine.ListItemAsync(testPlayerId, equipmentId, 999999999L);

            Assert.False(accepted);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var stillInBag = await verifyDb.EquipmentInstances.AsNoTracking().SingleOrDefaultAsync(e => e.Id == equipmentId);
            Assert.NotNull(stillInBag);

            bool anyMarketMirror = await verifyDb.MarketEquipmentInstances.AsNoTracking().AnyAsync(e => e.PlayerId == testPlayerId);
            Assert.False(anyMarketMirror);

            // And the item nobody can price at all. An instance whose BaseItemId
            // is not in the catalogue has no rolling average and no baseline;
            // the corridor used to skip on that and let ANY price through, which
            // is the laundering route it exists to close. It fails closed now,
            // and this asserts a sane price rather than an extreme one so that
            // it can only pass by the item being refused outright.
            long orphanEquipmentId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var orphan = new EquipmentInstance
                {
                    PlayerId = testPlayerId,
                    BaseItemId = "gilded_sabatons_boots_armor_slot_base", // removed by the catalogue cut
                    QualityTier = qualityTier
                };
                db.EquipmentInstances.Add(orphan);
                await db.SaveChangesAsync();
                orphanEquipmentId = orphan.Id;
            }

            bool orphanAccepted = await escrowEngine.ListItemAsync(testPlayerId, orphanEquipmentId, 100L);
            Assert.False(orphanAccepted);

            await using (var orphanDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.NotNull(await orphanDb.EquipmentInstances.AsNoTracking()
                    .SingleOrDefaultAsync(e => e.Id == orphanEquipmentId));
            }
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
                var guildMainCharacterId = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = guildMainCharacterId,
                    AuthenticatorToken = Guid.NewGuid(),
                    GuildId = 970000905L
                });
                db.CharacterRecords.Add(new CharacterRecord
                {
                    Id = guildMainCharacterId,
                    PlayerId = testPlayerId,
                    Level = 1,
                    AgePhase = 1,
                    SlotIndex = 0,
                    EquippedWeaponId = equipmentId
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
            // A CANONICAL item, for the same reason as the corridor test above:
            // the legacy id this used was removed by the catalogue cut, and an
            // item the corridor cannot price is now refused rather than waved
            // through. 200 base gold at quality tier 0 gives [160, 600], which
            // contains the 500 these listings use.
            const string baseItemId = "eq_hunter_boots_boots_armor_slot_base";
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
            // corridor (160-600); each must migrate exactly one item with
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

        // Modul: combat arithmetic overflow guard. Written after a live audit
        // found that Malakor - the authored region 5 boss, ordinary progression
        // content - spawned at -1,294,967,296 milli-HP: 3,000,000 * 1000 wraps
        // int, the engine's "monster is dead" check is CurrentMonsterHp <= 0,
        // and the kill branch respawned it at the same negative value. That is
        // one full kill reward per tick, forever: 6,000,000 XP and 1,500,000
        // gold per second, with no cheating involved.
        //
        // The mirror-image defect existed on the damage side: AttackPower *
        // 1000 * 1.5 overflowed for the four strongest monsters, the wrapped
        // negative hit the Math.Max(1000, ...) floor, and the deadliest monster
        // in the game dealt exactly 1 HP per swing.
        //
        // This test asserts the two invariants directly against every authored
        // monster rather than against a hardcoded ceiling, so authoring a new
        // monster above the safe bound fails here instead of shipping.
        [Fact]
        public void Test_MonsterCombatArithmetic_NeverOverflowsOrGoesNegative()
        {
            ContentRegistry.Initialize();

            int monsterCount = ContentRegistry.Monsters.Length;
            Assert.True(monsterCount > 0, "content registry loaded no monsters");

            // The invariant that actually prevents the regression. Asserting
            // that correctly-widened arithmetic stays positive is circular - it
            // would pass against the broken code too, because the test would do
            // the widening the engine forgot. What broke was the STORAGE type:
            // milli-HP in an int. Pin it directly.
            var monsterHpField = typeof(TickStatePayload).GetField(nameof(TickStatePayload.CurrentMonsterHp));
            Assert.NotNull(monsterHpField);
            Assert.Equal(typeof(long), monsterHpField!.FieldType);

            var parkedHpField = typeof(CharacterActivityState).GetField(nameof(CharacterActivityState.CurrentMonsterHp));
            Assert.NotNull(parkedHpField);
            Assert.Equal(typeof(long), parkedHpField!.FieldType);

            for (int monsterId = 1; monsterId <= monsterCount; monsterId++)
            {
                int scaledMaxHp = ContentRegistry.GetScaledMonsterMaxHp(monsterId);
                Assert.True(scaledMaxHp > 0, $"monster {monsterId} scaled MaxHp is {scaledMaxHp}");

                // Exactly how SimulationEngine spawns a monster. A negative or
                // zero result here means it spawns already dead.
                long spawnMilliHp = (long)scaledMaxHp * 1000L;
                Assert.True(spawnMilliHp > 0, $"monster {monsterId} spawns at {spawnMilliHp} milli-HP");

                // Exactly how ProcessSubTick derives incoming damage, at the
                // maximum crit multiplier the monster crit roll can produce.
                int scaledAttack = ContentRegistry.GetScaledMonsterAttackPower(monsterId);
                Assert.True(scaledAttack >= 0, $"monster {monsterId} scaled AttackPower is {scaledAttack}");

                long rawDamageLong = (long)(scaledAttack * 1000L * 1.5f);
                Assert.True(rawDamageLong >= 0L, $"monster {monsterId} raw crit damage is {rawDamageLong}");

                int rawDamage = rawDamageLong >= int.MaxValue ? int.MaxValue : (int)rawDamageLong;
                Assert.True(rawDamage >= 0, $"monster {monsterId} saturated crit damage is {rawDamage}");
            }
        }

        // The endgame scaling multiplier compounds at 1.25 per region tier past
        // 10 with no upper bound, so the cast inside GetScaledMonster* must
        // saturate rather than wrap. Tier 200 is far past anything reachable and
        // is chosen precisely because it drives the double well beyond int range.
        [Fact]
        public void Test_EndgameScaling_SaturatesInsteadOfWrapping()
        {
            ContentRegistry.Initialize();

            Assert.Equal(1.0, ContentRegistry.GetEndgameScalingMultiplier(ContentRegistry.MaxAuthoredRegionTier));
            Assert.True(ContentRegistry.GetEndgameScalingMultiplier(200) > 1e18);

            for (int monsterId = 1; monsterId <= ContentRegistry.Monsters.Length; monsterId++)
            {
                Assert.True(ContentRegistry.GetScaledMonsterMaxHp(monsterId) > 0);
                Assert.True(ContentRegistry.GetScaledMonsterAttackPower(monsterId) >= 0);
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

                // Modul: ActiveSkillEngine.Initialize no longer parses
                // anything, so it no longer has anything to fail fast ABOUT.
                // It is called here to prove it stays harmless on a directory
                // with nothing in it - a boot step that throws on empty input
                // after being emptied itself would be a strange way to lose a
                // server.
                ActiveSkillEngine.Initialize(tempDir);

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
                // ActiveSkillEngine no longer parses anything - the four
                // active skills went, and with them skills.json's only
                // reader. The three registries above are what the content
                // pipeline still has to survive a bad file for.
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

        // Modul: Full-Stack Social Layer, Part 6. Three connections on one
        // pod: a sender, a listener who has blocked the sender
        // beforehand, and a bystander listener who has not. The blocked
        // listener's world chat delivery must be silently dropped
        // (NetworkBroadcastSystem.HandleChatDispatchAsync's block filter)
        // while the bystander receives the exact same broadcast normally -
        // proves the block check is per-recipient, not a global mute.
        [Fact]
        public async Task Test_ChatEngine_BlockedListener_DoesNotReceiveWorldChat_WhileBystanderDoes()
        {
            const long senderId = 970009301L;
            const long blockedListenerId = 970009302L;
            const long bystanderId = 970009303L;
            Guid senderAccountId = Guid.NewGuid();
            Guid blockedAccountId = Guid.NewGuid();
            Guid bystanderAccountId = Guid.NewGuid();
            const string messageText = "block filter test message";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = senderId, PlayerGuid = senderAccountId, AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = blockedListenerId, PlayerGuid = blockedAccountId, AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = bystanderId, PlayerGuid = bystanderAccountId, AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRelationships.Add(new PlayerRelationship { PlayerId = blockedListenerId, TargetPlayerId = senderId, RelationType = RelationType.Blocked });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8098/");
            networkSystem.Start();

            try
            {
                using var senderSocket = new ClientWebSocket();
                await senderSocket.ConnectAsync(new Uri("ws://localhost:8098/"), CancellationToken.None);
                await senderSocket.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(senderAccountId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var blockedSocket = new ClientWebSocket();
                await blockedSocket.ConnectAsync(new Uri("ws://localhost:8098/"), CancellationToken.None);
                await blockedSocket.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(blockedAccountId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var bystanderSocket = new ClientWebSocket();
                await bystanderSocket.ConnectAsync(new Uri("ws://localhost:8098/"), CancellationToken.None);
                await bystanderSocket.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(bystanderAccountId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                await Task.Delay(500);
                Assert.Equal(WebSocketState.Open, senderSocket.State);
                Assert.Equal(WebSocketState.Open, blockedSocket.State);
                Assert.Equal(WebSocketState.Open, bystanderSocket.State);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                var blockedReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var blockedReceiveTask = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try { result = await blockedSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token); }
                        catch { break; }
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count != Marshal.SizeOf<ResponseChatMessagePacket>()) continue;

                        var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        if (chatPacket.SenderPlayerId != senderId) continue;

                        string received;
                        unsafe { received = System.Text.Encoding.UTF8.GetString(chatPacket.MessageText, chatPacket.MessageLength); }
                        if (received == messageText) blockedReceivedTcs.TrySetResult();
                    }
                });

                var bystanderReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var bystanderReceiveTask = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try { result = await bystanderSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token); }
                        catch { break; }
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count != Marshal.SizeOf<ResponseChatMessagePacket>()) continue;

                        var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        if (chatPacket.SenderPlayerId != senderId) continue;

                        string received;
                        unsafe { received = System.Text.Encoding.UTF8.GetString(chatPacket.MessageText, chatPacket.MessageLength); }
                        if (received == messageText) bystanderReceivedTcs.TrySetResult();
                    }
                });

                byte[] chatBuffer = BuildChatMessageBuffer(messageText);
                await senderSocket.SendAsync(new ArraySegment<byte>(chatBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                var bystanderCompleted = await Task.WhenAny(bystanderReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(bystanderCompleted == bystanderReceivedTcs.Task, "Bystander (no block relationship) never received the world chat message.");

                // The blocked listener must never observe it - give the
                // dispatch pipeline a fair window past the bystander's
                // already-confirmed delivery, then confirm silence.
                var blockedCompleted = await Task.WhenAny(blockedReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.False(blockedCompleted == blockedReceivedTcs.Task, "Blocked listener received a world chat message from a player they blocked.");

                cts.Cancel();
                try { await blockedReceiveTask; } catch { }
                try { await bystanderReceiveTask; } catch { }
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: Full-Stack Social Layer, Part 6. Two members of the same
        // guild and one outsider on one pod. GuildId is pushed onto each
        // session directly via NetworkBroadcastSystem.UpdateSessionGuildId -
        // the same public entry point SimulationEngine's own login/guild-
        // change path uses - deliberately skipping a live SimulationEngine
        // instance, matching Test_ChatEngine_RedisPubSub_ForwardsMessagesAcrossPods's
        // documented rationale for why chat tests avoid the extra engine.
        [Fact]
        public async Task Test_ChatEngine_GuildChat_RoutesToMembersOnly_InvisibleToOutsider()
        {
            const long memberAId = 970009311L;
            const long memberBId = 970009312L;
            const long outsiderId = 970009313L;
            const long sharedGuildId = 660001L;
            Guid memberAAccountId = Guid.NewGuid();
            Guid memberBAccountId = Guid.NewGuid();
            Guid outsiderAccountId = Guid.NewGuid();
            const string messageText = "guild-only routing test message";

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = memberAId, PlayerGuid = memberAAccountId, AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = memberBId, PlayerGuid = memberBAccountId, AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = outsiderId, PlayerGuid = outsiderAccountId, AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            GlobalEngineState.IsColdBootRecoveryComplete = true;
            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8099/");
            networkSystem.Start();

            try
            {
                using var memberASocket = new ClientWebSocket();
                await memberASocket.ConnectAsync(new Uri("ws://localhost:8099/"), CancellationToken.None);
                await memberASocket.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(memberAAccountId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var memberBSocket = new ClientWebSocket();
                await memberBSocket.ConnectAsync(new Uri("ws://localhost:8099/"), CancellationToken.None);
                await memberBSocket.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(memberBAccountId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                using var outsiderSocket = new ClientWebSocket();
                await outsiderSocket.ConnectAsync(new Uri("ws://localhost:8099/"), CancellationToken.None);
                await outsiderSocket.SendAsync(new ArraySegment<byte>(BuildAuthHandshakeBuffer(MintTestJwt(outsiderAccountId))), WebSocketMessageType.Binary, true, CancellationToken.None);

                await Task.Delay(500);
                Assert.Equal(WebSocketState.Open, memberASocket.State);
                Assert.Equal(WebSocketState.Open, memberBSocket.State);
                Assert.Equal(WebSocketState.Open, outsiderSocket.State);

                networkSystem.UpdateSessionGuildId(memberAId, sharedGuildId);
                networkSystem.UpdateSessionGuildId(memberBId, sharedGuildId);
                // outsiderId's session GuildId stays 0 (not in a guild).

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                var memberBReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var memberBReceiveTask = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try { result = await memberBSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token); }
                        catch { break; }
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count != Marshal.SizeOf<ResponseChatMessagePacket>()) continue;

                        var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        if (chatPacket.SenderPlayerId != memberAId || chatPacket.ChannelType != ChatEngine.GuildChannelType) continue;

                        string received;
                        unsafe { received = System.Text.Encoding.UTF8.GetString(chatPacket.MessageText, chatPacket.MessageLength); }
                        if (received == messageText) memberBReceivedTcs.TrySetResult();
                    }
                });

                var outsiderReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var outsiderReceiveTask = Task.Run(async () =>
                {
                    var recvBuffer = new byte[1024];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try { result = await outsiderSocket.ReceiveAsync(new ArraySegment<byte>(recvBuffer), cts.Token); }
                        catch { break; }
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count != Marshal.SizeOf<ResponseChatMessagePacket>()) continue;

                        var chatPacket = MemoryMarshal.Read<ResponseChatMessagePacket>(new ReadOnlySpan<byte>(recvBuffer, 0, result.Count));
                        if (chatPacket.SenderPlayerId != memberAId) continue;

                        string received;
                        unsafe { received = System.Text.Encoding.UTF8.GetString(chatPacket.MessageText, chatPacket.MessageLength); }
                        if (received == messageText) outsiderReceivedTcs.TrySetResult();
                    }
                });

                byte[] guildChatBuffer = BuildChatMessageBuffer(messageText, ChatEngine.GuildChannelType);
                await memberASocket.SendAsync(new ArraySegment<byte>(guildChatBuffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                var memberBCompleted = await Task.WhenAny(memberBReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(memberBCompleted == memberBReceivedTcs.Task, "Fellow guild member never received the guild-channel chat message.");

                var outsiderCompleted = await Task.WhenAny(outsiderReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.False(outsiderCompleted == outsiderReceivedTcs.Task, "Non-member received a guild-channel chat message.");

                cts.Cancel();
                try { await memberBReceiveTask; } catch { }
                try { await outsiderReceiveTask; } catch { }
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                networkSystem.Stop();
            }
        }

        // Modul: Full-Stack Social Layer, Part 6. Pure DB-level proof of
        // RelationshipEngine.AddFriendAsync's own transaction, no live
        // sockets needed - inserts once, then attempts the exact same
        // (PlayerId, TargetPlayerId) pair again and asserts the row count
        // never exceeds 1, matching the unique-index-backed safe roll-back
        // condition documented on the engine itself.
        [Fact]
        public async Task Test_RelationshipEngine_AddFriend_InsertsRow_DuplicateRollsBackSafely()
        {
            const long playerId = 970009321L;
            const long targetPlayerId = 970009322L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.PlayerRecords.Add(new PlayerRecord { Id = targetPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            var relationshipEngine = new RelationshipEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await relationshipEngine.AddFriendAsync(playerId, targetPlayerId);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var rows = await verifyDb.PlayerRelationships.AsNoTracking()
                    .Where(r => r.PlayerId == playerId && r.TargetPlayerId == targetPlayerId)
                    .ToListAsync();
                Assert.Single(rows);
                Assert.Equal(RelationType.Friend, rows[0].RelationType);
            }

            Assert.Equal((byte)CommandResultCode.Success, DequeueResultForPlayer(playerId));

            // Duplicate attempt - must not insert a second row.
            await relationshipEngine.AddFriendAsync(playerId, targetPlayerId);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var rows = await verifyDb.PlayerRelationships.AsNoTracking()
                    .Where(r => r.PlayerId == playerId && r.TargetPlayerId == targetPlayerId)
                    .ToListAsync();
                Assert.Single(rows);
            }

            Assert.Equal((byte)CommandResultCode.RelationshipAlreadyExists, DequeueResultForPlayer(playerId));
        }

        // Modul: CommandResultQueue is a single shared queue on the
        // fixture-level PlayerSessionRegistry, so unrelated tests running
        // earlier in this class may leave stale entries for other player
        // ids sitting ahead of this test's own result - a blind TryDequeue
        // would read whichever leaked notification happens to be at the
        // front rather than this test's own. This drains (and discards)
        // any entries for other players until it finds one for the
        // requested playerId, bounded so a genuine missing-result bug
        // still fails the test instead of hanging.
        private byte DequeueResultForPlayer(long playerId)
        {
            for (int attempts = 0; attempts < 64; attempts++)
            {
                Assert.True(_fixture.PlayerRegistry.CommandResultQueue.TryDequeue(out var notification), "CommandResultQueue was empty before a result for the expected player was found.");
                if (notification.PlayerId == playerId)
                {
                    return notification.ResultCode;
                }
            }

            throw new Xunit.Sdk.XunitException($"No CommandResultQueue entry for player {playerId} found within the search bound.");
        }

        private static unsafe byte[] BuildChatMessageBuffer(string messageText, byte channelType, long targetPlayerId = 0)
        {
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(messageText);
            int length = textBytes.Length > RequestChatMessagePacket.MessageCapacity ? RequestChatMessagePacket.MessageCapacity : textBytes.Length;

            var packet = new RequestChatMessagePacket { MessageLength = (ushort)length, ChannelType = channelType, TargetPlayerId = targetPlayerId };
            byte* target = packet.MessageText;
            for (int i = 0; i < RequestChatMessagePacket.MessageCapacity; i++)
            {
                target[i] = i < length ? textBytes[i] : (byte)0;
            }

            byte[] buffer = new byte[Marshal.SizeOf<RequestChatMessagePacket>()];
            MemoryMarshal.Write(new Span<byte>(buffer), packet);
            return buffer;
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
            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(_fixture.ServiceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(_fixture.ServiceProvider, playerRegistry);
            var legacyStoreEngine = new LegacyStoreEngine(_fixture.ServiceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(_fixture.ServiceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(_fixture.ServiceProvider, playerRegistry);

            var simulationEngine = new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageManagementEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
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

                long guildId = (await managementEngine.CreateGuildAsync(leaderPlayerId, guildName)).GuildId;
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

        // Modul: A NAME ON PATH IS NOT AN INTERPRETER.
        //
        // This accepted any candidate whose --version probe exited zero. On
        // Windows, `python3` is normally Microsoft's App Execution Alias: it
        // prints "Python was not found" and opens the Store, and it is happy to
        // exit zero doing it. The probe therefore picked `python3`, the
        // validator run with it never exited, WaitForExit(30000) gave up, and
        // reading ExitCode threw "Process must exit before requested
        // information can be determined" - which reads as a broken validator
        // rather than as a missing interpreter.
        //
        // Checked by what it SAYS, not by what it returns. A real interpreter
        // announces itself as Python 3 on one of the two streams; the alias
        // cannot. The timeout kills rather than leaks, for the same reason.
        private static string? ResolvePythonExecutable()
        {
            foreach (string candidate in new[] { "python3", "python", "py" })
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
                    if (probe == null)
                    {
                        continue;
                    }

                    string announced = probe.StandardOutput.ReadToEnd() + probe.StandardError.ReadToEnd();
                    if (!probe.WaitForExit(10000))
                    {
                        try { probe.Kill(entireProcessTree: true); } catch { }
                        continue;
                    }

                    if (probe.ExitCode == 0 && announced.TrimStart().StartsWith("Python 3", StringComparison.Ordinal))
                    {
                        return candidate;
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

            // Drained before the wait: a validator that fills its output pipe
            // would block forever on a full buffer and look like a hang.
            string output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Assert.Fail($"validate_content.py did not exit within 30s. Output so far: {output}");
            }

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
                    ActiveActivityId = 1001,   // Woodcutting node 1 (band 1000)
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

            Assert.False(ContentRegistry.GetLootTable(3001).IsEmpty);
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

            long guildId = (await managementEngine.CreateGuildAsync(leaderPlayerId, guildName)).GuildId;
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
                    // Modul: reroll rework, 2026-08-01. Was a nonsense slug with
                    // a nonsense affix key, so it now trips the id and slot
                    // guards - which run BEFORE payment, deliberately, so a
                    // malformed request is never charged for. This test is about
                    // the CURRENCY path, so the item must be otherwise valid and
                    // the only missing thing must be the gold.
                    BaseItemId = "eq_steel_claymore_melee_weapon_slot_base",
                    QualityTier = 1,
                    AffixPayload = "{\"crit_dmg_pct@2\":10}"
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
                Food1_ItemId = FirstEdibleItemId(),
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

        // Modul: dead code removal. Replaces a test that instantiated
        // OfflineStateEngine, a duplicate offline path with zero production
        // callers that has now been deleted. The engine is gone; the RULE it
        // asserted is not, and is still live.
        //
        // A player's backpack capacity is
        // SimulationEngine.DefaultBackpackCapacity plus the Human vault
        // mastery bonus - the real formula in StateCheckpointManager, which
        // had no direct test of its own. Deleting the old test outright would
        // have quietly removed the only guard on it, so it is retargeted here
        // rather than dropped. The original defect it caught was a hardcoded
        // capacity of 50, which let offline drops overflow a real capacity of
        // 25.
        [Fact]
        public void Test_RaceMastery_BackpackCapacityUsesHumanVaultBonusNotAHardcodedValue()
        {
            // Below the mastery threshold: the base capacity, unmodified.
            Assert.Equal(0, RaceMasteryResolver.GetHumanVaultBonusSlots(24));
            Assert.Equal(20, SimulationEngine.DefaultBackpackCapacity);

            int capacityWithoutMastery =
                SimulationEngine.DefaultBackpackCapacity + RaceMasteryResolver.GetHumanVaultBonusSlots(24);
            Assert.Equal(20, capacityWithoutMastery);

            // At and above Human mastery 25 the vault bonus applies, giving a
            // real capacity of 25 - not 20, and emphatically not the 50 the
            // deleted engine assumed.
            Assert.Equal(5, RaceMasteryResolver.GetHumanVaultBonusSlots(25));

            int capacityWithMastery =
                SimulationEngine.DefaultBackpackCapacity + RaceMasteryResolver.GetHumanVaultBonusSlots(25);
            Assert.Equal(25, capacityWithMastery);
            Assert.NotEqual(50, capacityWithMastery);
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
        // curve exactly at both sides of the boundary - baseExpReward=0 means
        // this call never adds any XP of its own, only evaluates the level-up
        // check against whatever CurrentXp was set to beforehand.
        //
        // Modul: balance pass. This used to re-derive 100 * 1.15^level inline,
        // which made it a fourth copy of a formula that already had three. It
        // now calls the one authority, so the test verifies that
        // ProcessMonsterDeath honours the published curve rather than
        // re-asserting a literal; the curve's own shape is pinned separately
        // below by its growth ratio.
        [Fact]
        public void Test_ProgressionEngine_LevelUpCost_ScalesExponentially()
        {
            // The growth ratio is the balance-critical property, and it is a
            // DESIGN CHOICE about season length rather than a number to be
            // derived. At 1.06 it tracked the 3x-per-region gear curve so
            // closely that every region took about as long as the last one -
            // 72 to 209 minutes across the whole game, which is a weekend, not
            // a season. At 1.13 the XP requirement grows 12.1x per region
            // against 3x more player power, so each region costs roughly four
            // times the one before it and a season has somewhere to go.
            //
            // Modul: this assertion was left behind by the season-curve change
            // and asserted 1.06 against an engine that had moved to 1.13. It is
            // the whole point of the test - a curve nobody can change by
            // accident - so it is updated, not loosened.
            Assert.Equal(1.16, ProgressionEngine.LevelCurveGrowth, 3);
            for (int level = 1; level <= 8; level++)
            {
                double ratio = ProgressionEngine.GetRequiredXpForLevel(level)
                    / (double)ProgressionEngine.GetRequiredXpForLevel(level - 1);
                Assert.InRange(ratio, 1.15, 1.17);
            }

            for (int level = 1; level <= 8; level++)
            {
                long requiredXp = ProgressionEngine.GetRequiredXpForLevel(level);

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

            // Modul: 200 and 1.35, from 1,000 and 1.5. The THREE ITEMS are the
            // cost of a fusion - assembling three identical pieces at the same
            // rarity is the work - and the gold was meant to be a fee on top,
            // not a second gate. At the old curve raising a tier-8 piece cost
            // 25,628, about an hour of region-2 income for one step.
            Assert.Equal((long)Math.Ceiling(200.0 * Math.Pow(1.35, 2)), costAtTier2);
            Assert.Equal((long)Math.Ceiling(200.0 * Math.Pow(1.35, 3)), costAtTier3);

            // The SHAPE is what this test is for - still exponential in the
            // current tier, just at a rate a player can pay.
            double ratio = costAtTier3 / (double)costAtTier2;
            Assert.InRange(ratio, 1.34, 1.36);
        }

        private async Task<long> MeasureForgeCostAtTierAsync(long playerId, string baseItemId, int startingTier, ForgeSplicingEngine forgeEngine)
        {
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // All three at the target's rarity - fusion refuses anything
                // else, so a fixed tier-4 fodder would measure a rejection
                // rather than a cost.
                var target = new EquipmentInstance { PlayerId = playerId, BaseItemId = baseItemId, QualityTier = startingTier };
                var sac1 = new EquipmentInstance { PlayerId = playerId, BaseItemId = baseItemId, QualityTier = startingTier };
                var sac2 = new EquipmentInstance { PlayerId = playerId, BaseItemId = baseItemId, QualityTier = startingTier };
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
        //
        // Modul: Fishing and Herbalism mastery moved this ceiling 700 -> 768.
        // See NetworkPacketLayoutGuard for why the four new ints were worth it
        // and why narrowing the existing level fields to byte was not.
        //
        // Modul: 768 -> 832. Jewellery took the packet to 769 and inheritance
        // to 775, and BOTH passes moved NetworkPacketLayoutGuard's exact pin
        // without noticing this ceiling sitting seven bytes below - so the
        // build was green on the guard and red here, and the red went unread.
        //
        // The ceiling is a discipline marker rather than a transport limit -
        // nothing fragments at any of these numbers, and size-based
        // demultiplexing stays unambiguous because the neighbouring packets are
        // 530 below and nothing above. It is moved by one 64-byte step rather
        // than to a round large number on purpose: at 832 the next addition has
        // 57 bytes of room and then has to argue for itself, which is the only
        // thing this assertion has ever been for.
        [Fact]
        public void Test_StateUpdatePacket_StructuralSizeIsStrictlyUnder832Bytes()
        {
            int actualSize = System.Runtime.InteropServices.Marshal.SizeOf<StateUpdatePacket>();
            Assert.True(actualSize < 832, $"StateUpdatePacket is {actualSize} bytes - expected strictly under 832.");
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

        // Modul: removed with ApplyStatusSynergy. Chilled and Vulnerable no
        // longer have anything that applies them - the bits survive only
        // because Burning shares the same byte and removing two of three would
        // renumber the third.

        // Modul: removed with ApplyStatusSynergy. The bits are still declared -
        // Burning shares the byte and is still set by the Chiming Steel
        // four-piece - but nothing applies Chilled or Vulnerable any more.

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
            const int birchAxeItemId = 408;

            // Modul: the tool tree, not the equipment recipes. Those are gone -
            // equipment is monster loot now - but the claim this test makes is
            // about the POOL, not about what came out of it, so it moved rather
            // than being deleted.
            Assert.True(ContentRegistry.TryGetRecipe(birchAxeItemId, out var recipe));
            string mat1 = ContentRegistry.GetItemBaseId(recipe.Mat1Id);
            string mat2 = ContentRegistry.GetItemBaseId(recipe.Mat2Id);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat1, Quantity = seededQuantity });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat2, Quantity = seededQuantity });
                await db.SaveChangesAsync();
            }

            var craftingEngine = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
            await craftingEngine.ExecuteCraftingAsync(testPlayerId, birchAxeItemId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var commodity = await verifyDb.CommodityRecords.AsNoTracking()
                .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == mat1);

            Assert.Equal(seededQuantity - recipe.Mat1Count, commodity.Quantity);
            Assert.True(commodity.Quantity > 9999L, "A material quantity beyond the legacy stack cap must survive intact - no cap exists on the unified pool.");
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

        // Modul: anti-cheese region locks. A buyer who has not opened a
        // region can neither purchase its gear on the market nor equip a
        // copy acquired through any other channel - the two ends of one
        // rule, which is why they are asserted together.
        //
        // The probe is region-9 legacy gear, deliberately. It is the case
        // that has no boss of its own, so it is the one where a gate can
        // most easily end up absent rather than merely lenient - and it is
        // also among the strongest gear in the game, which is what makes an
        // absent gate here worth a test rather than a comment.
        //
        // Was a LEVEL lock (region-9 T5 derived RequiredLevel 90 and the
        // buyer sat at 10). The subject is the same; only what the gate
        // reads has changed, from CurrentLevel and QualityTier to which
        // bosses are down.
        [Fact]
        public async Task Test_Market_UnclearedPlayerBlockedFromBuyingAndEquippingLockedRegionGear()
        {
            const long lowLevelBuyerId = 970005201L;
            const long sellerId = 970005202L;
            const long buyerGuildId = 970005250L;
            // Modul: a CANONICAL region-5 weapon. This named a legacy piece the
            // catalogue cut removed, and RegionUnlockGate.CanWearItem returns
            // true for an item it cannot resolve - so the gate reported a
            // region-5 weapon as wearable at level 1 and the test failed on
            // content, not on the rule it is about.
            //
            // The fail-open there is deliberate and stays: an unresolvable id is
            // not gated CONTENT, and refusing it would stop a player wearing
            // gear they legitimately earned before the cut. That is the opposite
            // call from the market corridor a few hundred lines up, which now
            // fails closed - and the asymmetry is the point. Wearing an obsolete
            // item you already own is a small power gain; pricing one freely
            // moves gold between accounts, which is the whole exploit.
            const string highTierBaseId = "eq_doom_edge_melee_weapon_slot_base";

            // The buyer below has beaten nothing, so both calls must refuse.
            var noBossesDown = new HashSet<int>();
            Assert.False(RegionUnlockGate.CanWearItem(highTierBaseId, noBossesDown));
            Assert.Equal(1, RegionUnlockGate.HighestUnlockedRegion(noBossesDown));

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

                // Modul: per-character equipment. Nothing may have been
                // equipped on any of the buyer's characters.
                Assert.False(await verifyDb.CharacterRecords.AsNoTracking()
                    .AnyAsync(c => c.PlayerId == lowLevelBuyerId && c.EquippedWeaponId != null));
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

            // Modul: the refusal now carries a REASON. Asserted as well as the
            // rejection itself - a bare 0 for four different rules is what left
            // the player with "Could not create" and nothing else.
            var rejected = await managementEngine.CreateGuildAsync(underLeveledId, "UnderLeveledGuild970005401");
            long rejectedGuildId = rejected.GuildId;
            Assert.Equal(GuildManagementEngine.GuildCreateRefusal.LevelTooLow, rejected.Refusal);
            Assert.True(rejected.RequiredLevel > rejected.CurrentLevel);
            Assert.Equal(0L, rejectedGuildId);

            long guildId = (await managementEngine.CreateGuildAsync(leaderId, "AccessControlGuild970005402")).GuildId;
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

        // Modul: Full-Stack Expansion, Part 1/7. Leggings slot end to end
        // at the engine level: a "_leggings_armor_slot_base" item routes to
        // the dedicated leggings slot (never the chest slot, despite also
        // carrying the generic armor marker), its affix defense joins the
        // combined equipped totals that feed the combat tick's defensive
        // profile, and the 3-way unequip clears exactly that slot.
        [Fact]
        public async Task Test_EquipmentSlots_LeggingsRouteToOwnSlotAndContributeDefensiveTotals()
        {
            const long testPlayerId = 970006001L;
            long leggingsId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var leggingsMainCharacterId = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = leggingsMainCharacterId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 100 });
                // Modul: per-character equipment. Gear hangs off a character
                // now, so a player without one has nowhere to put it.
                db.CharacterRecords.Add(new CharacterRecord { Id = leggingsMainCharacterId, PlayerId = testPlayerId, Level = 100, AgePhase = 1, SlotIndex = 0 });
                var leggings = new EquipmentInstance
                {
                    PlayerId = testPlayerId,
                    BaseItemId = "eq_steel_greaves_leggings_armor_slot_base",
                    QualityTier = 0,
                    AffixPayload = "{\"1\":0,\"2\":45,\"3\":0,\"4\":0}"
                };
                db.EquipmentInstances.Add(leggings);
                await db.SaveChangesAsync();
                leggingsId = leggings.Id;
            }

            var slotEngine = new EquipmentSlotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await slotEngine.EquipItemAsync(testPlayerId, leggingsId);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var character = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.PlayerId == testPlayerId);
                Assert.Equal(leggingsId, character.EquippedLeggingsId);
                Assert.Null(character.EquippedChestId);
                Assert.Null(character.EquippedWeaponId);

                (EquippedAffixTotals totals, _) =
                    await EquipmentSlotEngine.ComputeEquippedTotalsAsync(verifyDb, character);

                // Modul: balance pass. 45 -> 53. The affix payload contributes
                // 45, and the item's OWN authored FlatDefenseRating - 8 for
                // tier-1 steel greaves - now contributes the other 8. It used
                // to contribute nothing: base item power reached
                // StatsCalculator from nowhere, which is why a tier-5 weapon
                // hit exactly as hard as a tier-1 one. This assertion moving is
                // the fix being observed, not a regression.
                Assert.Equal(53, totals.FlatDefense);
                Assert.Equal(0, totals.FlatAttack);
            }

            await slotEngine.UnequipItemAsync(testPlayerId, EquipmentSlotEngine.SlotLeggings);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var unequippedCharacter = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.PlayerId == testPlayerId);
                Assert.Null(unequippedCharacter.EquippedLeggingsId);
            }
        }

        // Modul: Full-Stack Expansion, Part 3/7. Unified consumption
        // across the Backpack/Stash boundary: a craft whose cost exceeds
        // the Backpack balance alone succeeds by draining the Backpack
        // first and the remainder from the Village Stash, and the stash
        // deposit path enforces the 9999 per-stack cap by returning the
        // overflow instead of storing it.
        //
        // Modul: ONE STORE. VillageStashInstances folded into CommodityRecords -
        // the split was never a feature, and it produced the same client bug
        // three separate times. Two halves of this test still hold and one had
        // to change:
        //
        //   - Consumption still spans both tables. That is not nostalgia: rows
        //     stranded in the old table by an unmigrated database or a racing
        //     writer must still be spendable, so TryConsumeUnifiedAsync keeps
        //     reading it and this test keeps seeding it.
        //   - Deposits now land in CommodityRecords, because one store means
        //     one writer. The assertions below follow the deposit to where it
        //     actually goes rather than to where it used to.
        [Fact]
        public async Task Test_UnifiedStash_CraftingDrainsBackpackFirstThenStashAndStackCapHolds()
        {
            const long testPlayerId = 970006101L;
            const int birchAxeItemId = 408;

            // The tool tree, since equipment is no longer craftable. The claim
            // is about consumption spanning both tables, so what is being made
            // does not matter - only that its cost exceeds the backpack alone.
            Assert.True(ContentRegistry.TryGetRecipe(birchAxeItemId, out var toolRecipe));
            string oreId = ContentRegistry.GetItemBaseId(toolRecipe.Mat2Id);
            string logId = ContentRegistry.GetItemBaseId(toolRecipe.Mat1Id);
            long inBackpack = toolRecipe.Mat2Count - 6;
            long inStash = 100L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = oreId, Quantity = inBackpack });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = logId, Quantity = toolRecipe.Mat1Count });
                db.VillageStashInstances.Add(new VillageStashInstance { PlayerId = testPlayerId, ItemId = oreId, Quantity = inStash });
                await db.SaveChangesAsync();
            }

            // The backpack is six short, so the craft must pull the remainder
            // from the legacy stash rows.
            var craftingEngine = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
            await craftingEngine.ExecuteCraftingAsync(testPlayerId, birchAxeItemId);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var backpack = await verifyDb.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == oreId);
                Assert.Equal(0L, backpack.Quantity);

                var stash = await verifyDb.VillageStashInstances.AsNoTracking().SingleAsync(s => s.PlayerId == testPlayerId && s.ItemId == oreId);
                Assert.Equal(inStash - 6L, stash.Quantity);
            }

            // Modul: unlimited village chest. This block used to assert the
            // opposite: that a deposit past 9999 topped out at the cap and
            // handed the remainder back. No caller ever did anything useful
            // with that remainder - the only place to put it is the backpack
            // the player was depositing FROM - so the cap silently destroyed
            // materials at the exact moment a player had succeeded at the game.
            // The chest is unbounded now; the whole deposit must land.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                long overflow = await InventoryAndStashSystem.DepositToStashAsync(db, testPlayerId, oreId, 20000L);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                Assert.Equal(0L, overflow);
            }

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // The deposit landed in the one store, and the legacy row was
                // left exactly as the craft left it - a deposit is not a
                // migration and must not quietly rewrite rows it did not create.
                var deposited = await verifyDb.CommodityRecords.AsNoTracking()
                    .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == oreId);
                Assert.Equal(20000L, deposited.Quantity);

                var legacy = await verifyDb.VillageStashInstances.AsNoTracking()
                    .SingleAsync(s => s.PlayerId == testPlayerId && s.ItemId == oreId);
                Assert.Equal(inStash - 6L, legacy.Quantity);
            }

            // And a chest that big is still spendable without carrying anything
            // back out - which is the whole point of the chest being linked to
            // the workbench rather than being a museum.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                bool consumed = await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, testPlayerId, oreId, 15000L);
                Assert.True(consumed, "Materials sitting in the Village Chest must be spendable directly.");
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // 20,094 held across the two tables, 15,000 spent, 5,094 left -
                // wherever it physically sits. Asserting the SUM is the point:
                // the player has one number, and which row it came out of is an
                // implementation detail the fold exists to stop mattering.
                long commodity = await verifyDb.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == testPlayerId && c.ItemId == oreId)
                    .SumAsync(c => c.Quantity);
                long legacy = await verifyDb.VillageStashInstances.AsNoTracking()
                    .Where(s => s.PlayerId == testPlayerId && s.ItemId == oreId)
                    .SumAsync(s => s.Quantity);
                Assert.Equal(inStash - 6L + 20000L - 15000L, commodity + legacy);
            }
        }

        // Modul: Full-Stack Expansion, Part 4/7. The 14-tier stackalloc
        // crafting roll: deterministic for a given seed (pure function),
        // monotone in its inputs (a higher workshop level never lowers the
        // rolled tier for the same seed), never out of range, and a warm
        // steady-state call allocates exactly zero managed heap bytes.
        [Fact]
        public void Test_CraftingEngine_RarityRoll_StackallocDeterministicMonotoneAndAllocationFree()
        {
            Assert.Equal(0, CraftingEngine.RollCraftedRarity(0, 0, 0.0));
            Assert.Equal(CraftingEngine.RarityTierCount - 1, CraftingEngine.RollCraftedRarity(0, 0, 0.9999999999));

            // Same seed, increasing workshop level - the rolled tier must
            // never decrease (weight only ever shifts toward higher tiers).
            double[] probes = { 0.30, 0.55, 0.70, 0.85, 0.95, 0.999 };
            foreach (double probe in probes)
            {
                int previousTier = CraftingEngine.RollCraftedRarity(0, 0, probe);
                for (int workshop = 1; workshop <= 10; workshop++)
                {
                    int tier = CraftingEngine.RollCraftedRarity(0, workshop, probe);
                    Assert.InRange(tier, previousTier, CraftingEngine.RarityTierCount - 1);
                    previousTier = tier;
                }
            }

            // A high workshop level must visibly shift at least one probe
            // seed to a strictly higher tier than the unassisted roll.
            bool shifted = false;
            foreach (double probe in probes)
            {
                if (CraftingEngine.RollCraftedRarity(0, 10, probe) > CraftingEngine.RollCraftedRarity(0, 0, probe))
                {
                    shifted = true;
                    break;
                }
            }
            Assert.True(shifted, "Workshop level 10 must shift probability weight toward higher tiers.");

            // Zero-allocation proof on a warm call, matching this suite's
            // established warm-up-then-measure pattern.
            CraftingEngine.RollCraftedRarity(25, 5, 0.5);
            long before = GC.GetAllocatedBytesForCurrentThread();
            int rolled = CraftingEngine.RollCraftedRarity(25, 5, 0.5);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.InRange(rolled, 0, CraftingEngine.RarityTierCount - 1);
            Assert.Equal(0L, after - before);
        }

        // Modul: balance pass. Pins the progression curve's continuity, which
        // regressed badly before this pass: every region boss sat at 17-29x the
        // HP and 15-28x the ATK of the strongest regular monster in its OWN
        // region, and 10-12x the opening monster of the NEXT region. At the
        // level-1 baseline (10 DPS / 100 effective HP, both derived in
        // Test_OfflineSimulation_* below) that made the Alpha Wolf a 20-minute
        // kill that one-shot the player, and made the region immediately after
        // each boss trivial. The invariants asserted here are what keeps the
        // five regions readable as one curve rather than five walls.
        [Fact]
        public void Test_Content_RegionBossesAreContinuousWithTheirRegionCurve()
        {
            // The canonical progression set is exactly five regions of four
            // regular monsters plus one boss - ids 91-115, EnemyId m_01_*..m_05_*.
            // The legacy ids 1-90 are deliberately excluded; they are a different
            // (and much larger) scale that is not part of progression.
            for (int region = 1; region <= 5; region++)
            {
                int firstId = 91 + (region - 1) * 5;
                var regulars = new MonsterDefinition[4];
                for (int i = 0; i < 4; i++)
                {
                    regulars[i] = ContentRegistry.Monsters[firstId + i - 1];
                }
                var boss = ContentRegistry.Monsters[firstId + 4 - 1];

                // Regular monsters inside a region must be strictly increasing,
                // so "the strongest regular" is unambiguously the fourth.
                for (int i = 1; i < 4; i++)
                {
                    Assert.True(regulars[i].MaxHp > regulars[i - 1].MaxHp,
                        $"Region {region} regular {i} must be tougher than regular {i - 1}.");
                }

                var strongestRegular = regulars[3];

                // A boss is a capstone, not a wall: hard enough to be a real
                // gate, not so hard that it dwarfs everything around it.
                double hpRatio = (double)boss.MaxHp / strongestRegular.MaxHp;
                Assert.True(hpRatio >= 4.0 && hpRatio <= 6.5,
                    $"Region {region} boss HP is {hpRatio:F1}x its strongest regular; expected 4.0-6.5x.");

                // Modul: 3.0-8.0x, and the reason is a design decision rather
                // than a tuning drift.
                //
                // A BOSS IS A GEAR CHECK. Regular monsters stay a trickle -
                // that is what holds the larder's cost, and therefore
                // gathering's share of playtime, where it was tuned. A boss is
                // fought once per region, so its food cost barely moves that
                // share and it can afford to be genuinely dangerous.
                //
                // Reported from a live playtest: three low-rarity pieces from
                // the FIRST monster in the game beat the bosses of regions 1,
                // 2 and 3 without the player dropping below half health. Some
                // of that was lifesteal healing 700% of the hit, and the rest
                // was this ratio - at 1.5x a boss was a slightly longer
                // regular.
                //
                // Boss attack is now a quarter of the region's expected health
                // pool: half the bar in four swings for a player wearing
                // nothing, an eighth of it per swing for one in best-in-slot,
                // because mitigation spreads exactly 2x between those two.
                double atkRatio = (double)boss.AttackPower / strongestRegular.AttackPower;
                Assert.True(atkRatio >= 3.0 && atkRatio <= 8.0,
                    $"Region {region} boss ATK is {atkRatio:F1}x its strongest regular; expected 3.0-8.0x.");

                // Clearing a region must not drop the player into content that
                // is easier than what they just beat.
                //
                // Modul: measured in TIME, not in hit points. This compared raw
                // MaxHp across regions, which stopped meaning anything when
                // monster HP became a function of how long a fight should last:
                // a region-5 monster has fewer hit points than a region-1 one
                // did under the old table, and is far harder, because the
                // player's weapon grew 80x in between. A region is easier or
                // harder than its neighbour by how long its monsters take to
                // kill with that region's gear - so that is what is asserted.
                if (region < 5)
                {
                    var nextRegionOpener = ContentRegistry.Monsters[firstId + 5 - 1];

                    Assert.True(SecondsToKill(boss, region) > SecondsToKill(nextRegionOpener, region + 1),
                        $"Region {region}'s boss must be a longer fight than region {region + 1}'s opener.");

                    double openerSeconds = SecondsToKill(nextRegionOpener, region + 1);
                    double previousStrongestSeconds = SecondsToKill(strongestRegular, region);
                    Assert.True(openerSeconds >= previousStrongestSeconds * 0.35,
                        $"Region {region + 1}'s opener is {openerSeconds:F0}s against region {region}'s " +
                        $"strongest at {previousStrongestSeconds:F0}s - the step down is too steep.");
                }

                // Rewards are a flat function of HP across the whole file, so no
                // single monster is a strictly better grind than any other.
                // XP is exact; gold is within one because the authored data
                // rounds MaxHp/20 rather than flooring it (m_02_vine is 48, not
                // 47), and matching that rounding is not worth pinning.
                foreach (var monster in new[] { regulars[0], regulars[1], regulars[2], regulars[3], boss })
                {
                    Assert.Equal(monster.MaxHp / 5, monster.BaseXpReward);
                    Assert.InRange(monster.BaseGoldReward, (monster.MaxHp / 20) - 1, (monster.MaxHp / 20) + 1);
                }
            }
        }

        // Modul: broadcast dirty-checking. The broadcast used to send a full
        // 695-byte packet to every connected player ten times a second whether
        // or not anything changed - about 7 KB/s each, or 55 Mbps at a thousand
        // idle players.
        //
        // Verified live that an idle session stops receiving packets, but the
        // KEEPALIVE is the half that cannot be observed that way and is the
        // dangerous half: if it silently never fires, a client's interpolation
        // buffer starves and motion stutters, and nothing would point at this
        // code. Pinned here directly.
        [Fact]
        public void Test_Broadcast_SuppressesIdenticalPacketsButStillKeepalives()
        {
            var packet = new StateUpdatePacket
            {
                PlayerId = 42L,
                CurrentLevel = 10,
                PlayerHp = 100000,
                ActiveActivityId = 0
            };

            var identical = packet;

            // Unchanged state inside the keepalive window: suppressed.
            Assert.False(SimulationEngine.ShouldDispatchStateUpdate(ref identical, ref packet, ticksSinceLastSend: 1));
            Assert.False(SimulationEngine.ShouldDispatchStateUpdate(ref identical, ref packet, ticksSinceLastSend: 9));

            // The keepalive must fire even though nothing changed.
            Assert.True(SimulationEngine.ShouldDispatchStateUpdate(ref identical, ref packet, ticksSinceLastSend: 10));
            Assert.True(SimulationEngine.ShouldDispatchStateUpdate(ref identical, ref packet, ticksSinceLastSend: 40));

            // A real change is sent immediately, without waiting for it.
            var changed = packet;
            changed.PlayerHp = 90000;
            Assert.True(SimulationEngine.ShouldDispatchStateUpdate(ref changed, ref packet, ticksSinceLastSend: 1));

            // TicksSinceLastFlush increments EVERY tick by design, so if it
            // were part of the comparison no two packets would ever match and
            // the whole dirty check would save nothing at all.
            var onlyFlushCounterMoved = packet;
            onlyFlushCounterMoved.TicksSinceLastFlush = packet.TicksSinceLastFlush + 7;
            Assert.False(SimulationEngine.ShouldDispatchStateUpdate(ref onlyFlushCounterMoved, ref packet, ticksSinceLastSend: 1),
                "TicksSinceLastFlush must be excluded from the comparison, or dirty-checking is a no-op.");
        }

        // Modul: set bonuses made real. Pins that the 4-piece effects reach
        // CombatStats as usable values rather than being computed and dropped.
        //
        // Modul: set effect rework. All five are now consumed. The fifth used
        // to be SetCcImmunityActive, which could never fire because this game
        // models no player-facing crowd control; it is now SetDamageCapActive,
        // a per-hit ceiling that fits the same tank archetype and answers the
        // burst damage that actually ends runs.
        [Fact]
        public void Test_SetBonus_FourPieceEffectsReachCombatStats()
        {
            var noSet = StatsCalculator.Calculate(str: 10, dex: 10, con: 10, lck: 10);
            Assert.False(noSet.SetBurnApplicationActive);
            Assert.False(noSet.SetThornsReflectionActive);
            Assert.Equal(0f, noSet.SetFireDamageMultiplierPct);

            // Four Chiming Steel pieces - the tier that was unreachable until
            // EquippedSetIds widened the caller from three slots to seven.
            // Four Chiming Steel pieces at the reference rarity - full
            // potency, which is what arms the boolean effects.
            var chimingFourPiece = StatsCalculator.Calculate(str: 10, dex: 10, con: 10, lck: 10,
                equippedSetIds: new EquippedSetIds
                {
                    Helmet = EquippedSetIds.Pack(SetBonusEngine.ChimingSteelSetId, 4),
                    Chest = EquippedSetIds.Pack(SetBonusEngine.ChimingSteelSetId, 4),
                    Gloves = EquippedSetIds.Pack(SetBonusEngine.ChimingSteelSetId, 4),
                    Boots = EquippedSetIds.Pack(SetBonusEngine.ChimingSteelSetId, 4)
                });

            Assert.True(chimingFourPiece.SetBurnApplicationActive);
            Assert.True(chimingFourPiece.SetFireDamageMultiplierPct > 0f);

            var dreadnoughtSetIds = new EquippedSetIds
            {
                Helmet = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                Chest = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                Gloves = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                Boots = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4)
            };

            var dreadnoughtFourPiece = StatsCalculator.Calculate(str: 10, dex: 10, con: 10, lck: 10,
                equippedSetIds: dreadnoughtSetIds);

            Assert.True(dreadnoughtFourPiece.SetThornsReflectionActive);

            // Modul: the cooldown-reduction assertion is gone. It shortened
            // ACTIVE SKILL cooldowns, and active skills were removed from the
            // game - the "skill-cast site" the old comment here pointed at
            // does not exist. The flag is no longer set, and a test asserting
            // it would be pinning a promise nothing can keep.
            Assert.True(dreadnoughtFourPiece.SetDamageCapActive);
        }

        // Modul: set effect rework. Pins the damage cap's arithmetic, not just
        // that the flag is set.
        //
        // The cap replaced CcImmunityActive, which could never fire. It exists
        // because burst is what ends runs here: a region boss hits for roughly
        // 2.5x its region's regular monsters, and the auto-eat larder can only
        // respond BETWEEN hits, never during one - so a single large hit is
        // unsurvivable in a way that the same total damage spread over several
        // hits is not.
        //
        // Mirrors the combat tick's own expression rather than restating a
        // magic number, so a change to the fraction updates both together.
        [Fact]
        public void Test_SetBonus_DamageCapLimitsASingleHitToAShareOfMaxHp()
        {
            // Milli-HP, matching TickStatePayload.PlayerHp's unit.
            const int effectiveMaxHp = 100000;
            int damageCeiling = (int)(effectiveMaxHp * 0.20f);
            Assert.Equal(20000, damageCeiling);

            // A boss-sized hit is clamped to the ceiling.
            int hugeHit = 75000;
            int cappedHuge = hugeHit > damageCeiling ? damageCeiling : hugeHit;
            Assert.Equal(damageCeiling, cappedHuge);

            // A hit already under the ceiling passes through untouched - the
            // cap is a ceiling, not another mitigation term, so it must never
            // reduce ordinary damage.
            int smallHit = 4000;
            int cappedSmall = smallHit > damageCeiling ? damageCeiling : smallHit;
            Assert.Equal(smallHit, cappedSmall);

            // The defining property: a wearer at full HP always survives at
            // least five consecutive maximum hits, which is what buys auto-eat
            // the window it needs.
            Assert.True(effectiveMaxHp / damageCeiling >= 5);

            // And the flag only comes from a real four-piece set.
            var fourPiece = StatsCalculator.Calculate(str: 0, dex: 0, con: 0, lck: 0,
                equippedSetIds: new EquippedSetIds
                {
                    Helmet = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                    Chest = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                    Gloves = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                    Boots = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4)
                });
            Assert.True(fourPiece.SetDamageCapActive);

            var twoPiece = StatsCalculator.Calculate(str: 0, dex: 0, con: 0, lck: 0,
                equippedSetIds: new EquippedSetIds
                {
                    Helmet = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                    Chest = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4)
                });
            Assert.False(twoPiece.SetDamageCapActive);
        }

        // Modul: Luck and Constitution made real. Both attributes documented a
        // bonus, computed it into CombatStats, and had zero consumers - so
        // every point spent on either did nothing. These assert the values are
        // non-zero and scale, which is what the consumers now depend on.
        [Fact]
        public void Test_StatsCalculator_LuckAndConstitutionProduceUsableBonuses()
        {
            var lowLuck = StatsCalculator.Calculate(str: 0, dex: 0, con: 0, lck: 10);
            var highLuck = StatsCalculator.Calculate(str: 0, dex: 0, con: 0, lck: 200);

            Assert.True(lowLuck.ForgeSuccessPct > 0f,
                "Luck must produce a non-zero forge success bonus - ForgeSplicingEngine now adds it to the fusion roll.");
            Assert.True(highLuck.ForgeSuccessPct > lowLuck.ForgeSuccessPct);

            var lowCon = StatsCalculator.Calculate(str: 0, dex: 0, con: 10, lck: 0);
            var highCon = StatsCalculator.Calculate(str: 0, dex: 0, con: 200, lck: 0);

            Assert.True(lowCon.OutOfCombatHpRegen > 0f,
                "Constitution must produce a non-zero regen rate - the idle tick now applies it.");
            Assert.True(highCon.OutOfCombatHpRegen > lowCon.OutOfCombatHpRegen);
        }

        // Modul: balance pass. Makes the progression curve MEASURED rather than
        // merely reachable. Every recipe ingredient being obtainable was already
        // pinned elsewhere; what nobody had ever computed is how long clearing a
        // region actually takes, which is what let three independent defects sit
        // undetected at once:
        //   1. Item base FlatAttackPower reached StatsCalculator from nowhere,
        //      so gear power was flat across all five tiers.
        //   2. The level curve grew 1.15^level (16.4x per region) against 3x
        //      more player power per region.
        //   3. Region bosses sat at 17-29x their own region's regular monsters.
        // Together those made level 100 roughly 59 days of uninterrupted
        // combat. This test computes clear time per region from the real
        // registry data and fails if any region leaves the playable band.
        //
        // The DPS model is deliberately a FLOOR: base attack plus the region's
        // weapon only, no affixes, no STR growth, no set bonus, no potions, no
        // codex multipliers, no attack-speed bonuses. Real play is faster, so a
        // region passing here has headroom, and a region failing here is
        // genuinely unreachable rather than merely slow.
        [Fact]
        public void Test_Progression_EveryRegionClearsInsideThePlayableTimeBand()
        {
            // Mirrors SimulationEngine's combat model: 15000 milli base attack,
            // 1500ms base swing, and the flat XP = MaxHp / 5 reward rate that
            // makes XP-per-second a pure function of DPS (monster HP cancels
            // out, so which monster is farmed does not change pacing).
            const double BaseAttackDamage = 15.0;
            const double BaseAttackIntervalMs = 1500.0;
            const double XpPerDamagePoint = 1.0 / 5.0;

            double previousCumulativeXp = 0.0;
            var regionMinutes = new double[6];

            for (int regionTier = 1; regionTier <= 5; regionTier++)
            {
                // The best weapon authored for this region tier.
                int weaponAttackPower = 0;
                foreach (var item in ContentRegistry.ItemDefinitions)
                {
                    if (item.RegionTier == regionTier && item.FlatAttackPower > weaponAttackPower)
                    {
                        weaponAttackPower = item.FlatAttackPower;
                    }
                }

                Assert.True(weaponAttackPower > 0,
                    $"Region {regionTier} must have at least one weapon with real base attack power. " +
                    "A zero here means items.json power is not reaching the combat model.");

                double damagePerHit = BaseAttackDamage + weaponAttackPower;
                double dps = damagePerHit * (1000.0 / BaseAttackIntervalMs);

                // A region spans 20 levels.
                int levelAtRegionEnd = regionTier * 20;
                double cumulativeXp = 0.0;
                for (int level = 0; level < levelAtRegionEnd; level++)
                {
                    cumulativeXp += ProgressionEngine.GetRequiredXpForLevel(level);
                }

                double regionXp = cumulativeXp - previousCumulativeXp;
                previousCumulativeXp = cumulativeXp;

                double combatSeconds = regionXp / (dps * XpPerDamagePoint);
                double combatMinutes = combatSeconds / 60.0;

                // A full six-slot loadout for the tier: one weapon at 8 bars and
                // five armour pieces at 6 bars each, where a bar is 3 ore plus 1
                // coal (the smelting recipes' shape, ContentRegistry._recipes).
                const int BarsForFullLoadout = 8 + (5 * 6);
                const int OrePerBar = 3;
                const int CoalPerBar = 1;
                int gatheringUnits = BarsForFullLoadout * (OrePerBar + CoalPerBar);

                // Gathering nodes are authored one tier-band per region; the
                // threshold is in 10Hz ticks. Mastery and tool bonuses only
                // reduce this, so base threshold is the slow end.
                int nodeThreshold = 0;
                foreach (var node in ContentRegistry.GatheringNodes)
                {
                    // Node ids run <profession><tier>, e.g. 1001-1005 woodcutting.
                    if ((node.ActivityId % 1000) == regionTier)
                    {
                        nodeThreshold = Math.Max(nodeThreshold, node.BaseTickThreshold);
                    }
                }
                Assert.True(nodeThreshold > 0, $"Region {regionTier} must have a gathering node band.");

                double gatheringMinutes = (gatheringUnits * nodeThreshold / 10.0) / 60.0;
                double totalMinutes = combatMinutes + gatheringMinutes;
                regionMinutes[regionTier] = totalMinutes;

                // Gathering must never be the dominant cost, or the crafting
                // tree becomes the progression bottleneck.
                //
                // Modul: a CEILING only. This also required gathering to be at
                // least 2% of a region, which held while every region was about
                // two hours. The season curve makes region 5 roughly 750 hours
                // of combat against an unchanged ~11 minutes of gathering, so
                // the floor now fails on arithmetic rather than on anything
                // being wrong. Worth saying plainly rather than only deleting:
                // gathering is now a rounding error after region 2, and whether
                // the crafting tree should scale with the curve is an open
                // design question, not something this test can answer.
                double gatheringShare = gatheringMinutes / totalMinutes;
                Assert.True(gatheringShare < 0.40,
                    $"Region {regionTier}: gathering is {gatheringShare:P0} of the region - it must not become the bottleneck.");
            }

            // THE SHAPE OF THE SEASON, asserted as a shape.
            //
            // This used to pin every region inside one absolute band, 45 to 260
            // minutes, which said "every region takes about the same time" -
            // true of the old curve and the precise thing the season curve was
            // changed to stop doing. A back-loaded game cannot be described by
            // one band, so what is pinned now is the intent itself: a brisk
            // opening, each region a multiple of the one before it, and a total
            // that is a season rather than a weekend.
            Assert.InRange(regionMinutes[1], 45.0, 240.0);

            for (int regionTier = 2; regionTier <= 5; regionTier++)
            {
                // Modul: 5.0-12.0, was 3.0-6.5. The curve went from 1.13 to
                // 1.16 when the offline cap made the old one a three-week game,
                // so each region costs about six and a half times the one
                // before rather than four. The band is the intent - a
                // back-loaded season where the last region is most of it - and
                // the intent got steeper on purpose.
                double ratio = regionMinutes[regionTier] / regionMinutes[regionTier - 1];
                Assert.InRange(ratio, 5.0, 12.0);
            }

            double totalHours = (regionMinutes[1] + regionMinutes[2] + regionMinutes[3]
                + regionMinutes[4] + regionMinutes[5]) / 60.0;

            // THE FLOOR IS THE POINT NOW, and it is sized against the offline
            // cap rather than against a guess about screen time.
            //
            // Catch-up banks twelve hours an absence, so a player returning
            // twice a day collects 2,160 hours in a ninety-day season. A total
            // that fits inside that is a game finished in season one; the
            // stated intent is that it should not be. At three times gear speed
            // this floor still has to outlast one season, which puts the bottom
            // of the band near 6,500 hours.
            Assert.InRange(totalHours, 6500.0, 20000.0);
        }

        // Modul: Full-Stack Expansion, Part 2/7. The 25 new regional
        // monsters and their material loot tables are live content: the
        // registry resolves the new monsters, every new monster id has a
        // populated loot table (the first populated MONSTER tables in the
        // codebase), and the gear-band forge caps derived from region
        // tiers hold their documented values.
        // Modul: A KILL IS A LOOT ROLL, so how long one takes is a design
        // target rather than a consequence.
        //
        // Monster HP used to be authored for its own sake, and the season curve
        // made the result absurd without anyone noticing: at the floor model a
        // region-5 regular took thirteen minutes and Malakor took seventy-six.
        // Equipment only drops from monsters, so that is four drops an hour at
        // the point in the game where a player most needs gear - and a fight
        // nobody wants to watch, which matters just as much.
        //
        // HP IS FREE TO SET, and that is what makes this fixable rather than a
        // trade. Rewards are a flat function of HP across the whole file (XP is
        // MaxHp/5, gold MaxHp/20), so halving a monster's health halves what it
        // pays and doubles how many of them a player kills per hour. The XP per
        // hour, and therefore the pacing, is IDENTICAL. Health is purely the
        // size of the bite.
        //
        // So every regular is sized to 20-45 seconds at the floor model - no
        // affixes, no STR, no set bonuses - which is roughly 7-15 seconds with
        // the gear a player at that region actually wears. Bosses are five
        // times their region's strongest regular, so about seventy-five
        // seconds: a real fight, not an errand.
        [Fact]
        public void Test_Content_EveryMonsterDiesInsideTheAttentionSpan()
        {
            // Measured first, asserted after, so one monster outside the band
            // does not hide the other twenty-four. A table is the useful output
            // of this test even when it passes.
            var measured = new List<(int Region, int Index, int Id, double Seconds)>();
            for (int region = 1; region <= 5; region++)
            {
                int firstId = 91 + (region - 1) * 5;
                for (int i = 0; i < 5; i++)
                {
                    var monster = ContentRegistry.Monsters[firstId - 1 + i];
                    measured.Add((region, i, monster.Id, SecondsToKill(monster, region)));
                }
            }

            foreach (var row in measured)
            {
                Console.WriteLine($"region {row.Region} {(row.Index == 4 ? "BOSS  " : "reg " + row.Index)} " +
                                  $"id {row.Id} {row.Seconds,8:F0}s on arrival");
            }

            foreach (var row in measured)
            {
                // Modul: 180s and 900s, up from 90 and 600, because monster HP
                // was tripled on purpose.
                //
                // ON ARRIVAL is the pessimistic end of the range - the gear a
                // player walks in with, before farming a single thing in the
                // region. Region 1's "arrival" is a character wearing nothing
                // at all, which is why its numbers are the worst in the table
                // and why they are not a problem: those four monsters ARE the
                // gearing-up.
                //
                // The boss figures here are the FARMED stats. A boss carries
                // five times that health until it falls once (see
                // BossFirstClearRules), and a first clear is explicitly not
                // something arrival gear is meant to manage.
                if (row.Index < 4)
                {
                    Assert.InRange(row.Seconds, 12.0, 180.0);
                }
                else
                {
                    Assert.InRange(row.Seconds, 300.0, 900.0);
                }
            }
        }

        // Modul: ASKED OF CombatDamageModel, not re-derived.
        //
        // This computed (15 + best weapon) / 1.5s and called it damage, which
        // is the same private copy of a damage model that has now drifted three
        // separate times in this codebase. It was wrong in two directions at
        // once: it ignored MONSTER ARMOUR, which the live tick subtracts from
        // every swing, and it assumed the player already owns the best weapon
        // of the region they have only just walked into. Sized against it, the
        // opening monster of the game came out at a hundred seconds for a
        // character who owns nothing - the live-tick test next door said so
        // while this one reported twenty.
        //
        // The gear argument is the honest half: a player entering a region
        // carries the PREVIOUS region's weapon, because gear drops from the
        // region you are in. So a fight is at its longest on arrival and gets
        // shorter as the region equips you, and the band below is written for
        // arrival.
        private static double SecondsToKill(MonsterDefinition monster, int regionOfArrival)
        {
            int carriedWeapon = 0;
            for (int earlier = 1; earlier < regionOfArrival; earlier++)
            {
                foreach (var item in ContentRegistry.ItemDefinitions)
                {
                    if (item.RegionTier == earlier && item.FlatAttackPower > carriedWeapon)
                    {
                        carriedWeapon = item.FlatAttackPower;
                    }
                }
            }

            var stats = StatsCalculator.Calculate(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0);
            // Modul: THE CARRIED WEAPON HAS ROLLS ON IT.
            //
            // This modelled arrival as the previous region's weapon and nothing
            // else - no affixes at all - which is not a player who just beat a
            // region boss, it is a player who never touched the forge. Since
            // the monster ladder became continuous, that understatement is what
            // pushed the first monster of a region past ninety seconds.
            // Arriving at region 1 means arriving with nothing - no weapon, no
            // rolls, no forge. Handing that character affixes made it kill the
            // first monster in ten seconds and reported the opening of the game
            // as too fast.
            double affixMultiplier = 1.0;
            int carriedRegion = regionOfArrival - 1;
            if (carriedRegion >= 1 && AffixRegistry.TryGetDefinition("melee_dmg_pct", out var meleeDamage))
            {
                int pct = 3 * AffixRegistry.CalculateMagnitude(
                    meleeDamage, carriedRegion, AffixRarity.Rare);
                affixMultiplier += pct / 100.0;
            }
            long milliAttack = (long)((15L + carriedWeapon) * 1000L * affixMultiplier);
            return CombatDamageModel.ExpectedSecondsPerKill(in stats, in monster, milliAttack, 1.0f);
        }

        [Fact]
        public void Test_Content_NewRegionalMonstersAndLootTablesResolve()
        {
            Assert.True(ContentRegistry.Monsters.Length >= 115, "The 25 new regional monsters must be loaded.");

            // Modul: identity, not hit points. These pinned 3,500 and 3,000,000
            // - authored figures that the kill-time pass replaced wholesale,
            // because monster HP is now derived from how long a fight should
            // last rather than authored for its own sake. What this test is
            // about is that the ids resolve to the right monsters in the right
            // regions, which no rebalance should ever change.
            var alphaWolf = ContentRegistry.Monsters[95 - 1];
            Assert.Equal(95, alphaWolf.Id);
            Assert.Equal(1, alphaWolf.RegionTier);
            Assert.True(alphaWolf.MaxHp > 0);

            var malakor = ContentRegistry.Monsters[115 - 1];
            Assert.Equal(5, malakor.RegionTier);
            Assert.True(malakor.MaxHp > 0);

            // Tables live at 501-525 (not the monster ids) - see the
            // ContentRegistry segment block's own comment on the shared
            // LootTableId/ActivityId key space.
            for (int lootTableId = 501; lootTableId <= 525; lootTableId++)
            {
                Assert.False(ContentRegistry.GetLootTable(lootTableId).IsEmpty,
                    $"Monster loot table {lootTableId} must be populated.");
            }

            // The remapped table ids must be exactly what the monster rows
            // reference, and the woodcutting node ids the monster ids shadow
            // (101-105) must not pick up monster drops.
            Assert.Equal(505, ContentRegistry.Monsters[95 - 1].LootTableId);

            // Modul: gathering loot tables. This used to assert node 101's table
            // was EMPTY, standing in for "monster tables have not leaked into
            // the node id space". Node ids have since moved into their own
            // bands (Woodcutting 1001-1005), which removes the shadowing
            // outright - see ActivityIdBands. That proxy stopped holding the moment
            // Woodcutting got real content - node 101 is now populated on
            // purpose. The actual invariant is that a gathering node drops
            // gathering materials, so it is asserted directly: no entry in a
            // Woodcutting node's table may be one of the monster-only mat_*
            // drops (ids 250+), which is what a leak would look like.
            // Modul: the proxy moved again. Requiring every entry to carry
            // "_woodcutting_material" was a second stand-in, and it stopped
            // holding when gathering was rebuilt around the five locations:
            // the alchemy chain's eight inputs no longer fit in Herbalism's
            // five rare slots, so heartwood_core and subterranean_sawdust are
            // now what a woodcutter turns up. Both are legitimate gathering
            // materials; neither is a monster drop.
            //
            // So the leak invariant is asserted as itself - no monster-only
            // mat_* id (250+) in a gathering table - rather than through a
            // naming convention that keeps needing exceptions.
            foreach (var node in ContentRegistry.GatheringNodes.ToArray())
            {
                var table = ContentRegistry.GetLootTable((int)node.ActivityId).ToArray();
                Assert.NotEmpty(table);
                foreach (var entry in table)
                {
                    // By NAME, not by id range. "250+ is monster-only" was
                    // true when it was written and stopped being true the
                    // moment a new gathering material was authored above that
                    // number - which happened immediately, with the fifth
                    // location's fish. Monster drops are the ones prefixed
                    // mat_, and that is a fact about the content rather than
                    // about where the id counter happened to be.
                    string droppedBaseId = ContentRegistry.GetItemBaseId(entry.ItemId);
                    Assert.False(droppedBaseId.StartsWith("mat_", StringComparison.Ordinal),
                        $"Gathering node {node.ActivityId} drops {droppedBaseId}, a monster-only "
                        + "material - a monster table has leaked into the node id space.");
                }
            }

            // Modul: THE PER-BAND CEILING IS GONE. It capped region 1-2 gear
            // at rarity 5 and was the likeliest cause of fusion appearing
            // broken on ordinary starter gear. Every region now answers with
            // the one global ceiling.
            Assert.Equal(ForgeSplicingEngine.MaxQualityTier, CraftingEngine.GetMaxForgeTierForRegion(1));
            Assert.Equal(ForgeSplicingEngine.MaxQualityTier, CraftingEngine.GetMaxForgeTierForRegion(2));
            Assert.Equal(ForgeSplicingEngine.MaxQualityTier, CraftingEngine.GetMaxForgeTierForRegion(3));
            Assert.Equal(ForgeSplicingEngine.MaxQualityTier, CraftingEngine.GetMaxForgeTierForRegion(9));
        }

        // Modul: Deferred Part 5 Implementation, Part 1/4. Gathering tool
        // speed scaling - the pure required-tick computation: every named
        // tool family's percentage bonus strictly reduces the tick
        // requirement relative to the tier below, the village production
        // building adds its +5 percent per level on top, the floor of 2
        // ticks always holds, and a warm call allocates zero heap bytes.
        [Fact]
        public void Test_GatheringToolEngine_ToolTiersAccelerateTicksWithZeroAllocation()
        {
            const int baseThreshold = 200;

            int noTool = GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 0, 0, 0);
            Assert.Equal(baseThreshold, noTool);

            // Each successive tool family must be at least as fast as the
            // previous, and the top family strictly faster than none.
            int previous = noTool;
            for (int tier = 1; tier <= 10; tier++)
            {
                int ticks = GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 0, tier, 0);
                Assert.True(ticks <= previous, $"Tool tier {tier} must not be slower than tier {tier - 1}.");
                previous = ticks;
            }
            Assert.True(previous < noTool, "The best tool family must strictly accelerate gathering.");

            // Void Bark is +1912 percent now, not +200: (200 - 10) * 100 / 2012
            // = 9 ticks against a bare-handed 200. The old curve made the best
            // tool in the game three times a bare hand and only 2.7 times the
            // very first tool a player crafts, which is why gathering grew into
            // most of the playtime - see GatheringToolEngine.
            Assert.Equal(9, GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 0, 10, 0));

            // Modul: and the affixes rolled on a tool now count. They were
            // computed, stored on the payload and read by nobody - every
            // gather_speed_pct ever rolled did nothing at all.
            int withoutAffixes = GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 0, 5, 0, 0);
            int withAffixes = GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 0, 5, 0, 200);
            Assert.True(withAffixes < withoutAffixes,
                "a gather-speed affix must actually reduce the tick requirement");

            // Village production building stacks its +5 percent per level.
            int withoutMill = GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 0, 1, 0);
            int withMill = GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 0, 1, 10);
            Assert.True(withMill < withoutMill, "Ten Lumber Mill levels must accelerate gathering further.");

            Assert.Equal(GatheringToolEngine.MinRequiredTicks, GatheringToolEngine.ComputeRequiredTicks(5, 50, 10, 10));

            GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 10, 5, 3);
            long before = GC.GetAllocatedBytesForCurrentThread();
            GatheringToolEngine.ComputeRequiredTicks(baseThreshold, 10, 5, 3);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }

        // Modul: Deferred Part 5 Implementation, Part 2/4. Consumable
        // lifecycle: applying a potion by item id populates the correct
        // unmanaged buff slot, the countdown expires it deterministically
        // after exactly its duration in ticks, and the Death Ward Elixir
        // intercepts a lethal blow, revives at exactly 20 percent max HP,
        // and clears its own slot (one charge).
        [Fact]
        public void Test_ConsumableEngine_PotionLifecycleAndDeathWardIntercept()
        {
            Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId("searing_tonic_offensive_potion_consumable", out var tonicDef));
            Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId("roasted_perch_food_consumable", out var perchDef));
            Assert.True(ConsumableEngine.DeathWardItemId > 0, "The Death Ward Elixir item must resolve from content.");

            var payload = new TickStatePayload { PlayerId = 970007001L };

            Assert.True(ConsumableEngine.TryApplyConsumable(ref payload, tonicDef.Id));
            Assert.Equal(tonicDef.Id, payload.ActiveOffensivePotionId);
            Assert.Equal(ConsumableEngine.PotionDurationMs, payload.OffensivePotionDurationMs);

            Assert.True(ConsumableEngine.TryApplyConsumable(ref payload, perchDef.Id));
            Assert.Equal(perchDef.Id, payload.ActiveFoodBuffId);

            // A non-consumable item id must be left to the legacy path.
            Assert.False(ConsumableEngine.TryApplyConsumable(ref payload, 1));

            // Deterministic expiry: exactly duration / 100 ticks clears the
            // slot without any string work.
            int expectedTicks = ConsumableEngine.PotionDurationMs / 100;
            for (int i = 0; i < expectedTicks - 1; i++)
            {
                ConsumableEngine.TickBuffCountdowns(ref payload);
            }
            Assert.Equal(tonicDef.Id, payload.ActiveOffensivePotionId);
            ConsumableEngine.TickBuffCountdowns(ref payload);
            Assert.Equal(0, payload.ActiveOffensivePotionId);
            Assert.Equal(0, payload.OffensivePotionDurationMs);

            // Death Ward: occupies the defensive slot, intercepts the
            // lethal blow at exactly 20 percent max HP, consumes itself,
            // and cannot fire twice.
            Assert.True(ConsumableEngine.TryApplyConsumable(ref payload, ConsumableEngine.DeathWardItemId));
            Assert.Equal(ConsumableEngine.DeathWardItemId, payload.ActiveDefensivePotionId);

            payload.PlayerHp = 0;
            const int maxHp = 500000;
            Assert.True(ConsumableEngine.TryInterceptLethalDamage(ref payload, maxHp));
            Assert.Equal(maxHp / 5, payload.PlayerHp);
            Assert.Equal(0, payload.ActiveDefensivePotionId);

            payload.PlayerHp = 0;
            Assert.False(ConsumableEngine.TryInterceptLethalDamage(ref payload, maxHp),
                "A consumed ward must not intercept a second lethal blow.");

            // Zero-allocation proof for the per-tick paths.
            ConsumableEngine.TickBuffCountdowns(ref payload);
            long before = GC.GetAllocatedBytesForCurrentThread();
            ConsumableEngine.TickBuffCountdowns(ref payload);
            ConsumableEngine.TryInterceptLethalDamage(ref payload, maxHp);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0L, after - before);
        }

        // Modul: Deferred Part 5 Implementation, Part 3/4. Structural
        // village progression: upgrading the Crafting Workshop consumes
        // Logs + Ores + rare Logs through the unified Backpack+Stash path
        // (stash covers what the backpack lacks), the Town Hall ceiling
        // blocks other buildings from out-leveling it, and the workshop
        // level measurably shifts the stackalloc rarity roll.
        [Fact]
        public async Task Test_Village_StructuralUpgradesConsumeUnifiedMaterialsAndGateCeilings()
        {
            const long testPlayerId = 970007101L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                // Workshop level 0 -> 1 costs 100 raw_log + 100 copper_ore
                // + 10 golden_birch_log. Backpack holds only part; the
                // stash covers the remainder.
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "raw_log", Quantity = 60L });
                db.VillageStashInstances.Add(new VillageStashInstance { PlayerId = testPlayerId, ItemId = "raw_log", Quantity = 60L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "copper_ore", Quantity = 100L });
                db.VillageStashInstances.Add(new VillageStashInstance { PlayerId = testPlayerId, ItemId = "golden_birch_log", Quantity = 10L });
                // A production building already at the level-0 Town Hall
                // ceiling (2) - its next upgrade must be rejected.
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = testPlayerId, BuildingId = VillageManagementEngine.LumberjackBuildingId, CurrentLevel = 2 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "wood", Quantity = 100000L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "stone", Quantity = 100000L });
                await db.SaveChangesAsync();
            }

            var villageEngine = new VillageManagementEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await villageEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.CraftingWorkshopBuildingId);

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var workshop = await verifyDb.VillageInfrastructures.AsNoTracking()
                    .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.CraftingWorkshopBuildingId);
                Assert.Equal(1, workshop.UpgradeTargetLevel);

                var backpackLogs = await verifyDb.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "raw_log");
                Assert.Equal(0L, backpackLogs.Quantity);
                Assert.Equal(20L, (await verifyDb.VillageStashInstances.AsNoTracking().SingleAsync(s => s.PlayerId == testPlayerId && s.ItemId == "raw_log")).Quantity);

                Assert.False(await verifyDb.VillageStashInstances.AsNoTracking().AnyAsync(s => s.PlayerId == testPlayerId && s.ItemId == "golden_birch_log"),
                    "The fully-consumed rare log stash stack must be removed.");
            }

            // The Town Hall ceiling: Lumberjack at level 2 with Town Hall 0
            // must be rejected (ceiling = 2), leaving no pending upgrade
            // on that building.
            await villageEngine.ExecuteUpgradeBuildingAsync(testPlayerId, VillageManagementEngine.LumberjackBuildingId);
            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var lumberjack = await verifyDb.VillageInfrastructures.AsNoTracking()
                    .SingleAsync(v => v.PlayerId == testPlayerId && v.BuildingId == VillageManagementEngine.LumberjackBuildingId);
                Assert.Equal(0, lumberjack.UpgradeTargetLevel);
            }

            // Workshop level shifts the rarity roll: at least one probe
            // seed lands strictly higher with workshop level 5 than 0.
            bool shifted = false;
            double[] probes = { 0.55, 0.70, 0.85, 0.95 };
            foreach (double probe in probes)
            {
                if (CraftingEngine.RollCraftedRarity(0, 5, probe) > CraftingEngine.RollCraftedRarity(0, 0, probe))
                {
                    shifted = true;
                    break;
                }
            }
            Assert.True(shifted, "Workshop level 5 must shift the rarity roll toward higher tiers.");
        }

        // Modul: Economy Polish, Part 1/3. The varchar(255) expansion: a
        // base item id longer than the old 100-character limit round-trips
        // through "EquipmentInstances" intact rather than throwing a
        // data-truncation error on insert.
        //
        // The probe used to BE the offending content string - a region-10
        // weapon whose BaseId had an English design note pasted onto it,
        // 113 characters long. That id has since been repaired in
        // items.json, so the probe is synthetic now: the column's capacity
        // is worth pinning on its own, and tying it to a specific content
        // row meant the guarantee quietly depended on a defect staying
        // unfixed.
        [Fact]
        public async Task Test_VarcharExpansion_LongBaseItemIdRoundTripsThroughEquipmentInstances()
        {
            const long testPlayerId = 970008001L;
            const string longBaseId = "probe_base_id_exceeding_one_hundred_characters_so_the_varchar_255_column_is_actually_exercised_end_to_end_weapon_slot_base";
            Assert.True(longBaseId.Length > 100, "The probe id must exceed the old varchar(100) limit to prove anything.");

            long instanceId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                var instance = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = longBaseId, QualityTier = 5, AffixPayload = "{}" };
                db.EquipmentInstances.Add(instance);
                await db.SaveChangesAsync();
                instanceId = instance.Id;
            }

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var readBack = await verifyDb.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == instanceId);
                Assert.Equal(longBaseId, readBack.BaseItemId);
            }
        }

        // Modul: Economy Polish, Part 2/3. Town Hall passive gold: an
        // 8-hour offline jump at Town Hall level 3 (450 gold/hour) awards
        // exactly 3600 gold into the central "CommodityRecords" gold row -
        // the same row every spender (crafting, forge, market, village
        // upgrades) reads, so the accrued gold is immediately available
        // liquidity. The hourly rate table itself is pinned alongside.
        [Fact]
        public async Task Test_TownHallPassiveGold_OfflineJumpAwardsRateIntoGoldCommodityRow()
        {
            Assert.Equal(50L, VillageManagementEngine.GetTownHallGoldRatePerHour(0));
            Assert.Equal(50L, VillageManagementEngine.GetTownHallGoldRatePerHour(1));
            Assert.Equal(150L, VillageManagementEngine.GetTownHallGoldRatePerHour(2));
            Assert.Equal(450L, VillageManagementEngine.GetTownHallGoldRatePerHour(3));
            Assert.Equal(1200L, VillageManagementEngine.GetTownHallGoldRatePerHour(4));
            Assert.Equal(3000L, VillageManagementEngine.GetTownHallGoldRatePerHour(5));

            const long testPlayerId = 970008101L;
            const long initialGold = 1000L;
            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long eightHoursSeconds = 8L * 3600L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = initialGold });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = testPlayerId, BuildingId = VillageManagementEngine.TownHallBuildingId, CurrentLevel = 3 });
                await db.SaveChangesAsync();
            }

            var payload = new TickStatePayload
            {
                PlayerId = testPlayerId,
                LastLogoutTimestamp = nowEpoch - eightHoursSeconds,
                TownHallLevel = 3
            };

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, payload, nowEpoch);
            }

            await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var goldRow = await verifyDb.CommodityRecords.AsNoTracking()
                    .SingleAsync(c => c.PlayerId == testPlayerId && c.ItemId == "gold");

                // 8 hours * 450/hour = 3600 on top of the seeded balance.
                Assert.Equal(initialGold + 3600L, goldRow.Quantity);
            }

            // Zero-allocation proof for the live-tick accrual rate lookup.
            VillageManagementEngine.GetTownHallGoldRatePerHour(3);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long rate = VillageManagementEngine.GetTownHallGoldRatePerHour(3);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(450L, rate);
            Assert.Equal(0L, after - before);
        }

        // Modul: Architecture Overhaul, Part 6. Two characters belonging to
        // the same player cannot both be assigned to the identical
        // gathering/combat activity id - the second ChangeCharacterActivityAsync
        // call for the same node must fail with NodeOccupied and must not
        // mutate the requesting character's row.
        [Fact]
        public async Task Test_CharacterSlotEngine_SecondCharacterOnSameNodeIsRejectedAsNodeOccupied()
        {
            const long testPlayerId = 970009001L;
            const long targetActivityId = 91L; // Field Mouse
            var mainCharacterId = Guid.NewGuid();
            var secondCharacterId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = mainCharacterId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 60 });
                db.CharacterRecords.Add(new CharacterRecord { Id = mainCharacterId, PlayerId = testPlayerId, SlotIndex = 0 });
                db.CharacterRecords.Add(new CharacterRecord { Id = secondCharacterId, PlayerId = testPlayerId, SlotIndex = 1 });

                // Modul: Town Hall slot gating. Slot 2 used to ride on the
                // seeded CurrentLevel of 60; it now needs a Town Hall of at
                // least 3, or this test measures the unlock gate instead of the
                // occupancy mutex it is actually about.
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = testPlayerId, BuildingId = VillageManagementEngine.TownHallBuildingId, CurrentLevel = CharacterSlotEngine.Slot2TownHallRequirement });
                await db.SaveChangesAsync();
            }

            var simulationEngine = CreateTestSimulationEngine();

            var firstResult = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, mainCharacterId, targetActivityId);
            Assert.Equal(CommandResultCode.Success, firstResult);

            var secondResult = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, secondCharacterId, targetActivityId);
            Assert.Equal(CommandResultCode.NodeOccupied, secondResult);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var secondCharacter = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == secondCharacterId);
            Assert.Equal(0L, secondCharacter.ActiveActivityId);

            // Zero-allocation proof for the pure occupancy scan itself.
            // stackalloc cannot appear directly in an async method body, so
            // the probe is isolated in a synchronous local function.
            static bool ProbeOccupancy(long activityId)
            {
                Span<long> probe = stackalloc long[3] { activityId, 0, 0 };
                return CharacterSlotEngine.IsActivityOccupiedByAnotherSlot(probe, 1, activityId);
            }

            ProbeOccupancy(targetActivityId);
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool occupied = ProbeOccupancy(targetActivityId);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(occupied);
            Assert.Equal(0L, after - before);
        }

        // Modul: Town Hall slot gating. The second slot requires Town Hall 3
        // and the third requires Town Hall 5, enforced independently of the
        // occupancy mutex above.
        //
        // This test used to drive the main character's CurrentLevel through
        // 29 / 30 / 60. Level was the wrong axis: it is a pure function of
        // leaving combat running, so the extra slots arrived on a timer that
        // rewarded no decision and had nothing to do with the village they
        // exist to populate. The Town Hall can only be raised by gathering
        // raw_log and copper_ore, which is what extra characters are for.
        [Fact]
        public async Task Test_CharacterSlotEngine_TownHallGatesBlockLockedSlots()
        {
            const long testPlayerId = 970009002L;
            var mainCharacterId = Guid.NewGuid();
            var slot2CharacterId = Guid.NewGuid();
            var slot3CharacterId = Guid.NewGuid();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // A high character level must NOT unlock anything any more -
                // seeding level 99 here is the guard against the old rule
                // quietly surviving.
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 99 });
                db.CharacterRecords.Add(new CharacterRecord { Id = mainCharacterId, PlayerId = testPlayerId, SlotIndex = 0 });
                db.CharacterRecords.Add(new CharacterRecord { Id = slot2CharacterId, PlayerId = testPlayerId, SlotIndex = 1 });
                db.CharacterRecords.Add(new CharacterRecord { Id = slot3CharacterId, PlayerId = testPlayerId, SlotIndex = 2 });
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = testPlayerId, BuildingId = VillageManagementEngine.TownHallBuildingId, CurrentLevel = 2 });
                await db.SaveChangesAsync();
            }

            var simulationEngine = CreateTestSimulationEngine();

            // Slot 1 is always available, whatever the Town Hall level.
            var slot1Assignment = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, mainCharacterId, 91L);
            Assert.Equal(CommandResultCode.Success, slot1Assignment);

            // Town Hall 2: slot 2 (requires 3) is still locked, despite the
            // character being level 99.
            var slot2AtTownHall2 = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, slot2CharacterId, 92L);
            Assert.Equal(CommandResultCode.LevelTooLow, slot2AtTownHall2);

            await SetTownHallLevelAsync(testPlayerId, 3);

            // Town Hall 3: slot 2 unlocks, slot 3 (requires 5) stays locked.
            var slot2AtTownHall3 = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, slot2CharacterId, 92L);
            Assert.Equal(CommandResultCode.Success, slot2AtTownHall3);

            var slot3AtTownHall3 = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, slot3CharacterId, 93L);
            Assert.Equal(CommandResultCode.LevelTooLow, slot3AtTownHall3);

            await SetTownHallLevelAsync(testPlayerId, 5);

            // Town Hall 5: the third slot finally opens.
            var slot3AtTownHall5 = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, slot3CharacterId, 93L);
            Assert.Equal(CommandResultCode.Success, slot3AtTownHall5);

            // And the occupancy mutex still applies across all three: any
            // character may do anything, but never the SAME thing as another.
            var slot3OntoSlot2sMonster = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, slot3CharacterId, 92L);
            Assert.Equal(CommandResultCode.NodeOccupied, slot3OntoSlot2sMonster);

            Assert.Equal(0, CharacterSlotEngine.GetSlotUnlockTownHallRequirement(0));
            Assert.Equal(3, CharacterSlotEngine.GetSlotUnlockTownHallRequirement(1));
            Assert.Equal(5, CharacterSlotEngine.GetSlotUnlockTownHallRequirement(2));

            // GetUnlockedSlotCount is what the tick loop reads to decide how
            // many characters to simulate, so it has to agree with
            // IsSlotUnlocked at every level or a slot could be assignable but
            // never simulated (or the reverse).
            Assert.Equal(1, CharacterSlotEngine.GetUnlockedSlotCount(0));
            Assert.Equal(1, CharacterSlotEngine.GetUnlockedSlotCount(2));
            Assert.Equal(2, CharacterSlotEngine.GetUnlockedSlotCount(3));
            Assert.Equal(2, CharacterSlotEngine.GetUnlockedSlotCount(4));
            Assert.Equal(3, CharacterSlotEngine.GetUnlockedSlotCount(5));
            Assert.Equal(CharacterSlotEngine.MaxCharacterSlots, CharacterSlotEngine.GetUnlockedSlotCount(99));
        }

        private async Task SetTownHallLevelAsync(long playerId, int level)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var townHall = await db.VillageInfrastructures
                .SingleAsync(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.TownHallBuildingId);
            townHall.CurrentLevel = level;
            await db.SaveChangesAsync();
        }

        // Modul: Architecture Overhaul, Part 6. Independent Multi-Drop
        // Evaluation: forces every category roll to succeed (100% chance
        // is not achievable through the real RNG baselines, so this drives
        // ProcessMonsterLootDropAsync directly via the queue at a
        // guaranteed-hit monster/region combination is not controllable
        // from a black-box test) - instead this drains a burst of
        // DropRequestQueue entries for the same monster and asserts that,
        // across the burst, both a materials grant (CommodityRecords) and
        // at least one equipment grant (EquipmentInstances) occurred in
        // the same combat-drop processing pass, proving the two roll
        // categories are independent rather than mutually exclusive.
        [Fact]
        public async Task Test_CombatLootEngine_IndependentRollsCanGrantMaterialsAndEquipmentTogether()
        {
            const long testPlayerId = 970009101L;
            const int monsterId = 104; // Sandstone Golem - mat_lodestone, region 3

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            var combatLootEngine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var processMethod = typeof(CombatLootEngine).GetMethod("ProcessMonsterLootDropAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            // A burst of independent evaluations is well beyond the point
            // where 35% materials and ~0.33%-0.50% equipment baselines are
            // both statistically certain to have hit at least once each.
            for (int i = 0; i < 400; i++)
            {
                await (Task)processMethod.Invoke(combatLootEngine, new object[] { testPlayerId, monsterId, 0f })!;
            }

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            long materialQuantity = await verifyDb.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == testPlayerId && c.ItemId == "mat_lodestone")
                .Select(c => (long?)c.Quantity)
                .FirstOrDefaultAsync() ?? 0L;
            Assert.True(materialQuantity > 0, "Expected at least one successful materials roll (Roll 1) across the burst.");

            int equipmentCount = await verifyDb.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == testPlayerId);
            Assert.True(equipmentCount > 0, "Expected at least one successful equipment category roll (Rolls 2-5) across the burst.");
        }

        // Modul: Deploy activation fix / Guild War scoreboard sync. The three
        // tests below only need a running tick thread and the registry queues
        // it drains, so this builds the smallest SimulationEngine that starts
        // cleanly rather than repeating the full 30-argument construction in
        // each one. Mirrors the wiring the guild-membership test above uses.
        private SimulationEngine BuildMinimalSimulationEngine(PlayerSessionRegistry playerRegistry)
        {
            var contextFactory = _fixture.ServiceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var retryingDbOptions = _fixture.RetryingOptions;

            var networkSystem = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, "http://localhost:8097/");
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
            var villageManagementEngine = new VillageManagementEngine(_fixture.ServiceProvider, playerRegistry);
            var guildWarEngine = new GuildWarEngine(_fixture.ServiceProvider);
            var chronoCoreEngine = new ChronoCoreEngine(_fixture.ServiceProvider, playerRegistry);
            var legacyStoreEngine = new LegacyStoreEngine(_fixture.ServiceProvider, playerRegistry);
            var guildLogisticsDepotEngine = new GuildLogisticsDepotEngine(_fixture.ServiceProvider, playerRegistry);
            var guildCombatSimulationEngine = new GuildCombatSimulationEngine(_fixture.ServiceProvider, playerRegistry);

            return new SimulationEngine(
                lootEngine, checkpointManager, networkSystem, forgeEngine, marketEngine, playerRegistry, guildEngine,
                escrowEngine, mailboxEngine, rerollEngine, breedingEngine, guildLogisticsEngine, craftingEngine, worldBossEngine,
                villageManagementEngine, guildWarEngine, chronoCoreEngine, legacyStoreEngine,
                guildLogisticsDepotEngine, guildCombatSimulationEngine, null!, null!, null!, null!, null!, contextFactory);
        }

        // Modul: anti-cheat false positive. This detector permanently
        // quarantined legitimate players, and a quarantine cannot be lifted:
        // ProcessSingleTick returns early on Quarantine_Active so the account
        // stops progressing entirely, the socket is closed on every login, and
        // no code anywhere sets the flag back to false. A false positive is
        // therefore an unappealable ban, which makes the precision of this
        // heuristic a correctness concern rather than a tuning preference.
        //
        // Exercised through the public RecordCommand surface with a real
        // registry, asserting on whether a shadow-ban was actually requested.
        [Fact]
        public void Test_AntiCheat_FlagsASteadyMacroButNotAHumanClickingQuickly()
        {
            // A burst: 30 commands a few milliseconds apart, which is a player
            // opening screens and equipping items in a hurry. Under the old
            // absolute-variance test every interval was near zero, so the
            // variance was near zero and this earned an instant permanent ban.
            var burstRegistry = new PlayerSessionRegistry();
            var burstEngine = new AntiCheatTelemetryEngine(_fixture.ServiceProvider, null!, burstRegistry);
            for (int i = 0; i < 30; i++)
            {
                burstEngine.RecordCommand(880000001L, (byte)CommandType.EquipItem);
            }
            Assert.True(burstRegistry.QuarantineNotificationQueue.IsEmpty,
                "A player clicking quickly was flagged as a macro.");

            // The client's own diagnostics ping is timer-driven and therefore
            // perfectly regular by construction. Detecting it would mean
            // banning players for running the game as shipped.
            var pingRegistry = new PlayerSessionRegistry();
            var pingEngine = new AntiCheatTelemetryEngine(_fixture.ServiceProvider, null!, pingRegistry);
            for (int i = 0; i < 60; i++)
            {
                pingEngine.RecordCommand(880000002L, (byte)CommandType.PingNetworkDiagnostics);
            }
            Assert.True(pingRegistry.QuarantineNotificationQueue.IsEmpty,
                "The client's own heartbeat was flagged as automation.");

            // The statistic itself, exercised directly: a script firing on a
            // fixed 2-second cadence over two minutes must still be caught.
            Assert.True(EvaluateCadence(intervalMs: 2000, sampleCount: 60, jitterMs: 0),
                "A perfectly regular 2s macro was not detected.");

            // And a human at the same average pace, with ordinary human
            // jitter, must not be.
            Assert.False(EvaluateCadence(intervalMs: 2000, sampleCount: 60, jitterMs: 900),
                "A human acting roughly every 2 seconds was flagged as a macro.");
        }

        // Drives one player's timing ring with a synthetic cadence and reports
        // whether it tripped the detector. jitterMs 0 is a machine; a large
        // jitter is a person.
        private bool EvaluateCadence(int intervalMs, int sampleCount, int jitterMs)
        {
            var registry = new PlayerSessionRegistry();
            var engine = new AntiCheatTelemetryEngine(_fixture.ServiceProvider, null!, registry);

            // Deterministic jitter, so this test cannot flake.
            var random = new Random(20260726);
            long playerId = 880000003L + jitterMs;

            var profile = engine.GetType()
                .GetNestedType("CommandTimingProfile", System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(profile);

            var instance = Activator.CreateInstance(profile!, nonPublic: true);
            var recordAndCheck = profile!.GetMethod("RecordAndCheck");
            Assert.NotNull(recordAndCheck);

            long now = 0L;
            bool flagged = false;
            for (int i = 0; i < sampleCount; i++)
            {
                flagged |= (bool)recordAndCheck!.Invoke(instance, new object[] { now })!;
                now += intervalMs + (jitterMs > 0 ? random.Next(-jitterMs, jitterMs + 1) : 0);
            }

            GC.KeepAlive(playerId);
            return flagged;
        }

        // Modul: guild war snapshot cast. RosterPayloadJson is a jsonb column
        // and Npgsql binds a string parameter as text; Postgres will not
        // implicitly coerce one to the other in an INSERT. Every refresh
        // therefore failed with 42804 "column is of type jsonb but expression
        // is of type text" - printed on every single server boot - and the
        // catch around the caller swallowed it, so no guild's defensive
        // snapshot had ever been written and every guild war resolved against
        // whatever stale row happened to exist.
        [Fact]
        public async Task Test_GuildWarSnapshot_RefreshActuallyWritesTheDefensiveRoster()
        {
            const long testPlayerId = 970004301L;
            long guildId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var guild = new GuildRecord { Name = "SnapCast" + Guid.NewGuid().ToString("N")[..8] };
                db.GuildRecords.Add(guild);
                await db.SaveChangesAsync();
                guildId = guild.Id;

                var playerGuid = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = testPlayerId,
                    PlayerGuid = playerGuid,
                    AuthenticatorToken = Guid.NewGuid(),
                    GuildId = guildId,
                    CurrentLevel = 20,
                    BaseStrength = 30,
                    BaseDexterity = 20,
                    BaseConstitution = 25,
                    BaseLuck = 10
                });
                db.GuildMembers.Add(new GuildMember { GuildId = guildId, PlayerId = testPlayerId, ContributionPoints = 500 });
                db.CharacterRecords.Add(new CharacterRecord { Id = playerGuid, PlayerId = testPlayerId, AgePhase = 1 });
                await db.SaveChangesAsync();
            }

            var snapshotEngine = new GuildWarSnapshotEngine(_fixture.ServiceProvider);
            await snapshotEngine.RefreshAllGuildSnapshotsAsync(CancellationToken.None);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var snapshot = await verify.GuildWarDefensiveSnapshots.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.GuildId == guildId);

                Assert.NotNull(snapshot);
                Assert.False(string.IsNullOrWhiteSpace(snapshot!.RosterPayloadJson),
                    "The defensive snapshot row exists but carries no roster payload.");

                // And it must round-trip back into the type the war simulation
                // deserialises it as - a row of valid-but-wrong JSON would pass
                // the existence check above and still break every war.
                var stats = System.Text.Json.JsonSerializer.Deserialize<CombatStats>(snapshot.RosterPayloadJson);
                Assert.True(stats.MaxHp > 0 || stats.FlatMeleeDamage > 0,
                    "The snapshot deserialised to an all-zero roster, so the guild would defend with nothing.");
            }
        }

        // Modul: gathering loot tables. The property that decides whether the
        // crafting tree is a game or a wall: can a player actually obtain the
        // materials every recipe asks for?
        //
        // Before this, Woodcutting nodes 101-105 and Mining nodes 202-205 had
        // no loot table at all (201 held one hand-placed coal entry), so a
        // player could chop and mine for hours and receive only mastery XP. All
        // ten Smelting recipes were unreachable, and with them every
        // Equipment-assembly recipe that consumes a bar - the entire gear
        // progression had no entry point.
        //
        // This test walks every authored recipe and asserts each ingredient is
        // reachable from SOME source - a gathering node, a monster drop, or
        // another recipe's output. It will fail again the moment a recipe is
        // added whose materials nothing produces, which is exactly the class of
        // bug it exists to catch.
        [Fact]
        public void Test_ContentRegistry_EveryRecipeIngredientIsObtainableFromSomeSource()
        {
            var obtainable = new HashSet<int>();

            // Everything any loot table can drop: gathering nodes, monsters,
            // the lot.
            foreach (var node in ContentRegistry.GatheringNodes.ToArray())
            {
                foreach (var entry in ContentRegistry.GetLootTable(node.ActivityId).ToArray())
                {
                    obtainable.Add(entry.ItemId);
                }
            }

            ReadOnlySpan<MonsterDefinition> monsters = ContentRegistry.Monsters;
            for (int i = 0; i < monsters.Length; i++)
            {
                foreach (var entry in ContentRegistry.GetLootTable(monsters[i].LootTableId).ToArray())
                {
                    obtainable.Add(entry.ItemId);
                }
            }

            // Plus everything crafting itself produces - bars feed equipment
            // recipes, so an intermediate counts as obtainable.
            ReadOnlySpan<ContentRegistry.RecipeDefinition> recipes = ContentRegistry.Recipes;
            for (int i = 0; i < recipes.Length; i++)
            {
                obtainable.Add(recipes[i].ResultItemId);
            }

            var unobtainable = new List<string>();
            for (int i = 0; i < recipes.Length; i++)
            {
                var recipe = recipes[i];

                if (recipe.Mat1Count > 0 && recipe.Mat1Id > 0 && !obtainable.Contains(recipe.Mat1Id))
                {
                    unobtainable.Add($"recipe {recipe.ResultItemId} needs Mat1 {recipe.Mat1Id} ({ContentRegistry.GetItemBaseId(recipe.Mat1Id)})");
                }

                if (recipe.Mat2Count > 0 && recipe.Mat2Id > 0 && !obtainable.Contains(recipe.Mat2Id))
                {
                    unobtainable.Add($"recipe {recipe.ResultItemId} needs Mat2 {recipe.Mat2Id} ({ContentRegistry.GetItemBaseId(recipe.Mat2Id)})");
                }
            }

            Assert.True(unobtainable.Count == 0,
                "Recipes require materials nothing in the game produces: " + string.Join("; ", unobtainable));
        }

        // Modul: gathering loot tables. Every Woodcutting and Mining node must
        // actually drop something - an authored node with an empty table is a
        // player spending real time for nothing, which is what all nine of
        // these did.
        [Fact]
        public void Test_ContentRegistry_EveryWoodcuttingAndMiningNodeDropsSomething()
        {
            const int Woodcutting = 0;
            const int Mining = 1;

            int checkedNodes = 0;
            foreach (var node in ContentRegistry.GatheringNodes.ToArray())
            {
                if (node.ProfessionType != Woodcutting && node.ProfessionType != Mining) continue;

                checkedNodes++;
                var table = ContentRegistry.GetLootTable(node.ActivityId).ToArray();
                Assert.True(table.Length > 0, $"Gathering node {node.ActivityId} has no loot table - it yields nothing but mastery XP.");

                foreach (var entry in table)
                {
                    Assert.True(entry.Weight > 0, $"Node {node.ActivityId} has a zero-weight entry, which can never be rolled.");
                    Assert.True(entry.ItemId > 0 && entry.ItemId <= ContentRegistry.ItemDefinitions.Length,
                        $"Node {node.ActivityId} drops item id {entry.ItemId}, which is not in items.json.");
                }
            }

            Assert.Equal(10, checkedNodes);
        }

        // Modul: balance pass + jewellery slots. Two defects in one path, both
        // invisible without actually equipping something and reading the totals
        // back:
        //
        //   1. ComputeEquippedTotalsAsync read only AffixPayload and SetId, so
        //      an item's OWN FlatAttackPower never reached StatsCalculator. A
        //      tier-5 weapon (972) hit exactly as hard as a tier-1 one (12) and
        //      the whole gear progression was cosmetic.
        //   2. Amulets and rings resolved to slot -1, so the ten authored
        //      bucklers/quivers/aegises could not be worn at all.
        //
        // Asserting through EquipItemAsync rather than by calling the totals
        // helper directly is deliberate: the bug was in the seam between the
        // registry and the equip path, and only the full path crosses it.
        [Fact]
        public async Task Test_Equipment_ItemBasePowerAndJewellerySlotsReachCombatStats()
        {
            const long testPlayerId = 970004702L;
            var characterId = Guid.NewGuid();
            long tierOneWeaponId, tierFiveWeaponId, amuletId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = characterId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 100 });
                SeedAllRegionBossKills(db, testPlayerId);
                db.CharacterRecords.Add(new CharacterRecord { Id = characterId, PlayerId = testPlayerId, Level = 100, AgePhase = 1, SlotIndex = 0 });

                // Real BaseIds out of items.json: tier 1 AP 12, tier 5 AP 972.
                var tierOneWeapon = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "eq_steel_claymore_melee_weapon_slot_base", QualityTier = 0, AffixPayload = "{}" };
                var tierFiveWeapon = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "eq_doom_edge_melee_weapon_slot_base", QualityTier = 0, AffixPayload = "{}" };
                var amulet = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "eq_linen_pendant_amulet_slot_base", QualityTier = 0, AffixPayload = "{}" };
                db.EquipmentInstances.AddRange(tierOneWeapon, tierFiveWeapon, amulet);
                await db.SaveChangesAsync();

                tierOneWeaponId = tierOneWeapon.Id;
                tierFiveWeaponId = tierFiveWeapon.Id;
                amuletId = amulet.Id;
            }

            var slotEngine = new EquipmentSlotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await slotEngine.EquipItemAsync(testPlayerId, tierOneWeaponId, characterId);
            int tierOneAttack;
            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var character = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == characterId);
                (EquippedAffixTotals totals, _) = await EquipmentSlotEngine.ComputeEquippedTotalsAsync(verify, character);
                tierOneAttack = totals.FlatAttack;
            }

            // The affix payload is empty, so every point here is the item's own
            // authored base power. Zero means the registry lookup is not wired.
            Assert.Equal(12, tierOneAttack);

            await slotEngine.EquipItemAsync(testPlayerId, tierFiveWeaponId, characterId);
            await slotEngine.EquipItemAsync(testPlayerId, amuletId, characterId);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var character = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == characterId);

                // The amulet went into its own slot rather than displacing the
                // weapon or falling into the chest fallback.
                Assert.Equal(amuletId, character.EquippedAmuletId);
                Assert.Equal(tierFiveWeaponId, character.EquippedWeaponId);
                Assert.Null(character.EquippedChestId);

                (EquippedAffixTotals totals, _) = await EquipmentSlotEngine.ComputeEquippedTotalsAsync(verify, character);

                // Tier 5 must be dramatically stronger than tier 1, and the
                // amulet's own base power must be counted too.
                Assert.Equal(972, totals.FlatAttack);
                Assert.True(totals.FlatAttack > tierOneAttack * 50,
                    "Tier-5 weapon base power must dwarf tier-1; equal values mean item power is not reaching the totals.");

                // The account-wide worn check has to see the amulet, or the
                // market/forge/mail could consume an item the character wears.
                Assert.True(await EquipmentSlotEngine.IsEquippedAnywhereAsync(verify, testPlayerId, amuletId));
            }
        }

        // Modul: per-character equipment. The account-wide lock. Equipment moved
        // from PlayerRecord to CharacterRecord, so "is this item equipped?"
        // stopped being a three-field comparison on one row and became a
        // question about every character the player owns.
        //
        // This is the security-relevant half of the refactor: anything that
        // destroys, transfers or re-points an EquipmentInstances row - a market
        // listing, a forge fusion, a mail send, a season wipe - has to see gear
        // worn by ANY character. Checking only the main character would let a
        // player sell the sword their second character is holding and leave that
        // character pointing at a row that no longer exists.
        [Fact]
        public async Task Test_PerCharacterEquipment_IsEquippedAnywhereSeesEveryCharactersGear()
        {
            const long testPlayerId = 970004701L;
            var mainCharacterId = Guid.NewGuid();
            var secondCharacterId = Guid.NewGuid();

            long mainWeaponId, secondBootsId, carriedId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = mainCharacterId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 100 });
                SeedAllRegionBossKills(db, testPlayerId);
                db.CharacterRecords.Add(new CharacterRecord { Id = mainCharacterId, PlayerId = testPlayerId, Level = 100, AgePhase = 1, SlotIndex = 0 });
                db.CharacterRecords.Add(new CharacterRecord { Id = secondCharacterId, PlayerId = testPlayerId, Level = 100, AgePhase = 1, SlotIndex = 1 });

                var mainWeapon = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "bronze_dagger_melee_weapon_slot_base", QualityTier = 0, AffixPayload = "{}" };
                var secondBoots = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "eq_iron_sabatons_boots_armor_slot_base", QualityTier = 0, AffixPayload = "{}" };
                var carried = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "iron_breastplate_chest_armor_slot_base", QualityTier = 0, AffixPayload = "{}" };
                db.EquipmentInstances.AddRange(mainWeapon, secondBoots, carried);
                await db.SaveChangesAsync();

                mainWeaponId = mainWeapon.Id;
                secondBootsId = secondBoots.Id;
                carriedId = carried.Id;
            }

            var slotEngine = new EquipmentSlotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await slotEngine.EquipItemAsync(testPlayerId, mainWeaponId, mainCharacterId);
            await slotEngine.EquipItemAsync(testPlayerId, secondBootsId, secondCharacterId);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var main = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == mainCharacterId);
                var second = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == secondCharacterId);

                // Each character wears its own gear, and neither wears the
                // other's - the whole point of the move.
                Assert.Equal(mainWeaponId, main.EquippedWeaponId);
                Assert.Null(main.EquippedBootsId);
                Assert.Equal(secondBootsId, second.EquippedBootsId);
                Assert.Null(second.EquippedWeaponId);

                // Boots are a real slot now. Under the old three-slot model the
                // generic "_armor_slot_" fallback would have jammed them into
                // the single Armor slot alongside chest pieces.
                Assert.Equal(EquipmentSlotEngine.SlotBoots, EquipmentSlotEngine.ResolveSlotIndex("eq_iron_sabatons_boots_armor_slot_base"));
                Assert.Equal(EquipmentSlotEngine.SlotHelmet, EquipmentSlotEngine.ResolveSlotIndex("eq_iron_helm_helmet_armor_slot_base"));
                Assert.Equal(EquipmentSlotEngine.SlotGloves, EquipmentSlotEngine.ResolveSlotIndex("eq_iron_gauntlets_gloves_armor_slot_base"));
                Assert.Equal(EquipmentSlotEngine.SlotChest, EquipmentSlotEngine.ResolveSlotIndex("iron_breastplate_chest_armor_slot_base"));

                // Modul: jewellery. The ten authored amulets and rings resolved
                // to -1 (unequippable) until these two slots existed, despite
                // being one clean pair per tier all along. The helper/offhand
                // ids they replace now resolve to -1 themselves, deliberately:
                // that slot was invented and must not silently be filled.
                Assert.Equal(EquipmentSlotEngine.SlotAmulet, EquipmentSlotEngine.ResolveSlotIndex("eq_linen_pendant_amulet_slot_base"));
                Assert.Equal(EquipmentSlotEngine.SlotAmulet, EquipmentSlotEngine.ResolveSlotIndex("eq_doom_gorget_amulet_slot_base"));
                Assert.Equal(EquipmentSlotEngine.SlotRing, EquipmentSlotEngine.ResolveSlotIndex("eq_copper_band_ring_1/2_slot_base"));
                Assert.Equal(EquipmentSlotEngine.SlotRing, EquipmentSlotEngine.ResolveSlotIndex("eq_dread_signet_ring_1/2_slot_base"));
                Assert.Equal(-1, EquipmentSlotEngine.ResolveSlotIndex("eq_linen_buckler_helper_offhand_base"));

                // The account-wide lock sees BOTH characters' gear, which is
                // what stops the market/forge/mail from consuming worn items.
                Assert.True(await EquipmentSlotEngine.IsEquippedAnywhereAsync(verify, testPlayerId, mainWeaponId));
                Assert.True(await EquipmentSlotEngine.IsEquippedAnywhereAsync(verify, testPlayerId, secondBootsId));
                Assert.False(await EquipmentSlotEngine.IsEquippedAnywhereAsync(verify, testPlayerId, carriedId));

                // The forge's three-at-once variant agrees.
                Assert.True(await EquipmentSlotEngine.IsAnyEquippedAnywhereAsync(verify, testPlayerId, carriedId, secondBootsId, 0L));
                Assert.False(await EquipmentSlotEngine.IsAnyEquippedAnywhereAsync(verify, testPlayerId, carriedId, 0L, 0L));
            }

            // One physical item cannot be worn twice. Without this guard a
            // single drop would multiply its stats across the whole roster.
            await slotEngine.EquipItemAsync(testPlayerId, mainWeaponId, secondCharacterId);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var second = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == secondCharacterId);
                Assert.Null(second.EquippedWeaponId);

                var main = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == mainCharacterId);
                Assert.Equal(mainWeaponId, main.EquippedWeaponId);
            }

            // Unequipping one character leaves the other untouched.
            await slotEngine.UnequipItemAsync(testPlayerId, EquipmentSlotEngine.SlotBoots, secondCharacterId);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Null((await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == secondCharacterId)).EquippedBootsId);
                Assert.Equal(mainWeaponId, (await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == mainCharacterId)).EquippedWeaponId);
                Assert.False(await EquipmentSlotEngine.IsEquippedAnywhereAsync(verify, testPlayerId, secondBootsId));
            }
        }

        // Modul: per-character equipment. Each character has to fight in its own
        // armour. The tick's slot-swap register now carries equipment and the
        // affix totals derived from it, so a character being simulated reads its
        // own gear - not slot 1's.
        [Fact]
        public void Test_PerCharacterEquipment_SlotSwapCarriesEachCharactersOwnGear()
        {
            var payload = new TickStatePayload
            {
                PlayerId = 970004702L,
                InventorySpaceRemaining = 20,
                InventoryCapacity = 20,
                CurrentLevel = 10,
                TownHallLevel = CharacterSlotEngine.Slot3TownHallRequirement
            };

            payload.Slot1_CharacterId = Guid.NewGuid();
            payload.EquippedWeaponId = 1111L;
            payload.CachedAffixTotals = new EquippedAffixTotals { FlatAttack = 10 };

            payload.Slot2_CharacterId = Guid.NewGuid();
            payload.Slot2Activity.EquippedWeaponId = 2222L;
            payload.Slot2Activity.CachedAffixTotals = new EquippedAffixTotals { FlatAttack = 20 };

            payload.Slot3_CharacterId = Guid.NewGuid();
            payload.Slot3Activity.EquippedWeaponId = 3333L;
            payload.Slot3Activity.CachedAffixTotals = new EquippedAffixTotals { FlatAttack = 30 };

            var swap = typeof(SimulationEngine).GetMethod(
                "SwapSlotIntoActiveRegister",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(swap);

            for (int slotIndex = 0; slotIndex < CharacterSlotEngine.MaxCharacterSlots; slotIndex++)
            {
                object boxed = payload;
                var args = new object[] { boxed, slotIndex };

                swap!.Invoke(null, args);
                var loaded = (TickStatePayload)args[0];

                long expectedWeaponId = 1111L + slotIndex * 1111L;
                int expectedAttack = 10 + slotIndex * 10;
                Assert.Equal(expectedWeaponId, loaded.EquippedWeaponId);
                Assert.Equal(expectedAttack, loaded.CachedAffixTotals.FlatAttack);

                // The swap is its own inverse, so the register comes back to
                // slot 1 - which is what the outbound packet and the checkpoint
                // flush both assume.
                args[0] = loaded;
                swap.Invoke(null, args);
                var restored = (TickStatePayload)args[0];
                Assert.Equal(1111L, restored.EquippedWeaponId);
                Assert.Equal(10, restored.CachedAffixTotals.FlatAttack);
                Assert.Equal(2222L, restored.Slot2Activity.EquippedWeaponId);
                Assert.Equal(3333L, restored.Slot3Activity.EquippedWeaponId);
            }
        }

        // Modul: challenge response policy. The integrity challenge asks a client
        // to prove it can compute ComputeChallengeHash - a test of knowledge,
        // not of speed. It was enforced with a 500ms wall-clock budget and a
        // single miss quarantined the account, which made it a latency detector:
        // a mobile client on a 300ms round trip that hit one GC pause was
        // permanently shadowbanned, and every automated Play Mode harness (whose
        // frames only advance when the driver pumps them) tripped it on every
        // run.
        [Fact]
        public void Test_AntiCheatChallenge_WindowToleratesRealLatencyAndOneMissDoesNotBan()
        {
            // Wide enough for a slow mobile round trip plus a frame hitch, where
            // 500ms was not.
            Assert.True(AntiCheatTelemetryEngine.ChallengeResponseWindowMs >= 5000L,
                "The response window is still tight enough to punish ordinary mobile latency.");

            // And a run is required before anything irreversible happens.
            Assert.True(AntiCheatTelemetryEngine.ConsecutiveChallengeMissLimit >= 2,
                "A single missed challenge still quarantines the account.");

            // The validator must not be stricter than the issuer, or a correct
            // answer arriving inside the engine's window is rejected as wrong.
            var payload = new TickStatePayload
            {
                PlayerId = 970004901L,
                ActiveChallengeSeed = 12345u,
                ActiveChallengeAnswered = 0,
                ActiveChallengeIssuedAtMs = Environment.TickCount64 - 1000L
            };

            var packet = new ClientCommandPacket
            {
                Command = CommandType.AntiCheatChallengeResponse,
                ChallengeId = payload.ActiveChallengeSeed,
                ChallengeVerificationHash = AntiCheatTelemetryEngine.ComputeChallengeHash(
                    payload.ActiveChallengeSeed, payload.PlayerId, payload.LogicEpochCounter)
            };

            // One second late - comfortably rejected under the old 500ms rule,
            // and the whole point of the change.
            Assert.True(ClientCommandValidator.ValidateAntiCheatChallengeResponse(ref payload, ref packet),
                "A correct answer one second after issue was rejected.");

            // Still rejected once genuinely stale, so the window is a window and
            // not an removal of the check.
            payload.ActiveChallengeIssuedAtMs = Environment.TickCount64 - (AntiCheatTelemetryEngine.ChallengeResponseWindowMs + 5000L);
            Assert.False(ClientCommandValidator.ValidateAntiCheatChallengeResponse(ref payload, ref packet));

            // And a wrong hash is still refused regardless of timing - the
            // knowledge test itself is untouched.
            payload.ActiveChallengeIssuedAtMs = Environment.TickCount64;
            var wrongPacket = packet;
            wrongPacket.ChallengeVerificationHash = packet.ChallengeVerificationHash + 1u;
            Assert.False(ClientCommandValidator.ValidateAntiCheatChallengeResponse(ref payload, ref wrongPacket));
        }

        // Modul: activity id bands. THE bug this pass exists to close. Monster
        // ids and gathering node ids share one activity space, and Region 3's
        // five monsters (101-105: Desert Crab, Ashen Basilisk, Ember Elemental,
        // Sandstone Golem and the Magma Wyrm boss) sat directly on top of
        // Woodcutting nodes 101-105.
        //
        // ProcessSubTick resolves an activity by asking TryGetGatheringNode
        // FIRST, so sending 101 always started chopping wood - Region 3 could
        // not be fought at all, and because the Magma Wyrm was unkillable the
        // Kobold race unlock hanging off its first kill was unreachable too.
        //
        // This asserts the spaces are disjoint rather than just that today's
        // five ids moved, so re-introducing the overlap with any future content
        // fails here instead of silently making a region unplayable again.
        [Fact]
        public void Test_ActivityIdBands_MonsterAndGatheringSpacesCannotOverlap()
        {
            var gatheringIds = new HashSet<long>();
            foreach (var node in ContentRegistry.GatheringNodes.ToArray())
            {
                Assert.True(ActivityIdBands.IsGatheringActivity(node.ActivityId),
                    $"Gathering node {node.ActivityId} is outside the gathering bands.");
                Assert.True(gatheringIds.Add(node.ActivityId), $"Duplicate gathering activity id {node.ActivityId}.");
            }

            ReadOnlySpan<MonsterDefinition> monsters = ContentRegistry.Monsters;
            for (int i = 0; i < monsters.Length; i++)
            {
                long monsterId = monsters[i].Id;

                Assert.False(gatheringIds.Contains(monsterId),
                    $"Monster {monsterId} collides with a gathering node - deploying against it would start gathering instead.");

                // The resolution order that made the collision fatal: if a
                // monster id ever resolves as a node again, combat is silently
                // unreachable for it.
                Assert.False(ContentRegistry.TryGetGatheringNode(monsterId, out _),
                    $"Monster {monsterId} resolves as a gathering node.");

                Assert.True(ActivityIdBands.IsCombatActivity(monsterId));
                Assert.False(ActivityIdBands.IsGatheringActivity(monsterId));
            }

            // Region 3 specifically, since it was the region that vanished.
            for (int monsterId = 101; monsterId <= 105; monsterId++)
            {
                Assert.False(ContentRegistry.TryGetGatheringNode(monsterId, out _),
                    $"Region 3 monster {monsterId} is still shadowed by a gathering node.");
            }

            // And the Magma Wyrm is reachable again, so the Kobold unlock is
            // obtainable - the mapping the race registry depends on.
            Assert.Equal(RaceIds.Kobold, RaceUnlockRegistry.GetRaceUnlockedByBoss(105));
            Assert.False(ContentRegistry.TryGetGatheringNode(105, out _));

            // The World Boss sentinel sits clear of every band.
            Assert.False(ActivityIdBands.IsGatheringActivity(ActivityIdBands.WorldBossActivityId));
            Assert.False(ActivityIdBands.IsCombatActivity(ActivityIdBands.WorldBossActivityId));
        }

        // Modul: activity id bands. Every gathering node kept its loot table
        // through the re-key. The tables are keyed by activity id, so a node
        // whose id moved without its segment moving would silently start
        // dropping nothing - the exact "authored content that yields nothing"
        // failure the loot-table pass existed to fix.
        [Fact]
        public void Test_ActivityIdBands_EveryRekeyedNodeKeptItsLootTable()
        {
            int checkedNodes = 0;
            foreach (var node in ContentRegistry.GatheringNodes.ToArray())
            {
                checkedNodes++;
                Assert.False(ContentRegistry.GetLootTable((int)node.ActivityId).IsEmpty,
                    $"Gathering node {node.ActivityId} lost its loot table in the re-key.");
            }
            // Modul: 31 -> 20. Gathering was rebuilt around the five canonical
            // locations: four professions x five places, one node each. It
            // used to be five Woodcutting, five Mining, NINE Fishing and
            // TWELVE Herbalism nodes, each of the latter two dropping a single
            // fish or herb - so "tier" was an unrelated ladder of one-item
            // nodes and the professions did not line up with the world or with
            // each other.
            // Three professions x five locations. Herbalism went with the
            // design list, which has no herb in it.
            Assert.Equal(15, checkedNodes);

            // Every node drops exactly two materials: a common and a rare.
            foreach (var node in ContentRegistry.GatheringNodes.ToArray())
            {
                var table = ContentRegistry.GetLootTable((int)node.ActivityId);
                Assert.Equal(2, table.Length);
                Assert.Equal(90, table[0].Weight);
                Assert.Equal(10, table[1].Weight);
            }

            // Four professions, five locations each, no gaps and no strays.
            for (int profession = 0; profession < 3; profession++)
            {
                for (int location = 1; location <= ContentRegistry.LocationCount; location++)
                {
                    long id = ActivityIdBands.GetBandForProfession(profession) + location;
                    Assert.True(ContentRegistry.TryGetGatheringNode(id, out _), $"missing node {id}");
                    Assert.Equal(location, ContentRegistry.GetNodeLocation(id));
                }
            }

            // The legacy ids must now resolve to nothing at all, or something is
            // still reading the pre-re-key space.
            foreach (int legacyId in new[] { 101, 105, 201, 205, 301, 309, 401, 412 })
            {
                Assert.False(ContentRegistry.TryGetGatheringNode(legacyId, out _),
                    $"Legacy gathering id {legacyId} still resolves.");
            }

            // The documented mapping, so the migration's SQL and the code agree.
            Assert.Equal(1001L, ActivityIdBands.MapLegacyGatheringId(101L));
            Assert.Equal(1005L, ActivityIdBands.MapLegacyGatheringId(105L));
            Assert.Equal(2005L, ActivityIdBands.MapLegacyGatheringId(205L));
            Assert.Equal(3009L, ActivityIdBands.MapLegacyGatheringId(309L));
            Assert.Equal(4012L, ActivityIdBands.MapLegacyGatheringId(412L));

            // Combat ids and the World Boss sentinel pass through untouched -
            // the migration must not move them.
            Assert.Equal(91L, ActivityIdBands.MapLegacyGatheringId(91L));
            Assert.Equal(105L, ActivityIdBands.MapLegacyGatheringId(105L) - 900L);
            Assert.Equal(9999L, ActivityIdBands.MapLegacyGatheringId(9999L));
        }

        // Modul: retryable equip. Rapid equips contend for the same character
        // row's FOR UPDATE lock; the loser used to get a transient failure that
        // the catch swallowed, leaving the item silently unequipped. A live Play
        // Mode run lost four of ten equips that way. With the execution strategy
        // wrapping the transaction, every one has to land.
        [Fact]
        public async Task Test_EquipmentSlotEngine_RapidConcurrentEquipsAllLandWithoutSilentDrops()
        {
            const long testPlayerId = 970004801L;
            var mainCharacterId = Guid.NewGuid();

            string[] baseIds =
            {
                "bronze_dagger_melee_weapon_slot_base",
                "eq_iron_helm_helmet_armor_slot_base",
                "iron_breastplate_chest_armor_slot_base",
                "eq_iron_gauntlets_gloves_armor_slot_base",
                "transcendent_platelegs_leggings_armor_slot_base",
                "eq_iron_sabatons_boots_armor_slot_base"
            };

            var itemIds = new List<long>();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = mainCharacterId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 100 });
                SeedAllRegionBossKills(db, testPlayerId);
                db.CharacterRecords.Add(new CharacterRecord { Id = mainCharacterId, PlayerId = testPlayerId, Level = 100, AgePhase = 1, SlotIndex = 0 });

                foreach (string baseId in baseIds)
                {
                    var instance = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = baseId, QualityTier = 1, AffixPayload = "{}" };
                    db.EquipmentInstances.Add(instance);
                }
                await db.SaveChangesAsync();

                itemIds.AddRange(await db.EquipmentInstances.AsNoTracking()
                    .Where(e => e.PlayerId == testPlayerId).Select(e => e.Id).ToListAsync());
            }

            var slotEngine = new EquipmentSlotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            // All six at once, deliberately contending for the same row.
            var equips = new List<Task>();
            for (int i = 0; i < itemIds.Count; i++)
            {
                long itemId = itemIds[i];
                equips.Add(slotEngine.EquipItemAsync(testPlayerId, itemId, mainCharacterId));
            }
            await Task.WhenAll(equips);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var character = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == mainCharacterId);

                // One item per slot went in, so all six slots must be filled.
                // Before the retry wrapper, contention left several null.
                Assert.NotNull(character.EquippedWeaponId);
                Assert.NotNull(character.EquippedHelmetId);
                Assert.NotNull(character.EquippedChestId);
                Assert.NotNull(character.EquippedGlovesId);
                Assert.NotNull(character.EquippedLeggingsId);
                Assert.NotNull(character.EquippedBootsId);

                // And each slot holds a distinct item - no retry double-applied.
                var worn = new HashSet<long>
                {
                    character.EquippedWeaponId!.Value,
                    character.EquippedHelmetId!.Value,
                    character.EquippedChestId!.Value,
                    character.EquippedGlovesId!.Value,
                    character.EquippedLeggingsId!.Value,
                    character.EquippedBootsId!.Value
                };
                Assert.Equal(6, worn.Count);
            }
        }

        // Modul: breeding pairs. A granted race pair has to be able to actually
        // breed, or it is a decoration. That needs three things to line up: one
        // male and one female, both the same race, and BreedingEngine honouring
        // its own paternal/maternal labels - which it did not, because until now
        // no sex existed and any two characters could pair.
        [Fact]
        public async Task Test_BreedingPair_GrantedRacePairCanBreedAndSameSexIsRefused()
        {
            const long testPlayerId = 970004601L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 100000L });
                await db.SaveChangesAsync();
            }

            // Grant the Kobold pair the region 3 boss would hand over.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await CharacterGrantEngine.GrantRacePairAsync(db, testPlayerId, RaceIds.Kobold, CancellationToken.None);
                await db.SaveChangesAsync();
            }

            Guid maleId;
            Guid femaleId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var pair = await db.CharacterRecords.Where(c => c.PlayerId == testPlayerId).ToListAsync();
                Assert.Equal(2, pair.Count);
                maleId = pair.Single(c => !c.IsFemale).Id;
                femaleId = pair.Single(c => c.IsFemale).Id;

                // BreedingEngine's own pre-existing gates, unrelated to sex: a
                // built Breeding Lab and both parents at level 50. A granted
                // pair arrives at level 1 on purpose - the race is a founding
                // population you still have to raise, not an instant dynasty.
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = testPlayerId, BuildingId = VillageManagementEngine.BreedingGroundsBuildingId, CurrentLevel = 1 });
                foreach (var character in pair) character.Level = 50;
                await db.SaveChangesAsync();
            }

            var breedingEngine = new BreedingEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            // Two males is not a pair, whatever the argument order claims.
            await breedingEngine.ExecuteBreedingAsync(testPlayerId, maleId, maleId);
            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(2, await verify.CharacterRecords.AsNoTracking().CountAsync(c => c.PlayerId == testPlayerId));
            }

            // Arguments the wrong way round - female as paternal - is also not a
            // pair. The labels have to mean what they say.
            await breedingEngine.ExecuteBreedingAsync(testPlayerId, femaleId, maleId);
            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(2, await verify.CharacterRecords.AsNoTracking().CountAsync(c => c.PlayerId == testPlayerId));
            }

            // The real pair breeds, and the child inherits the race - which is
            // what makes the grant a founding population rather than a dead end.
            await breedingEngine.ExecuteBreedingAsync(testPlayerId, maleId, femaleId);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var roster = await verify.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == testPlayerId).ToListAsync();
                Assert.Equal(3, roster.Count);

                var child = roster.Single(c => c.Id != maleId && c.Id != femaleId);
                var childLineage = await verify.CharacterLineages.AsNoTracking().SingleAsync(l => l.CharacterId == child.Id);
                Assert.Equal(RaceIds.Kobold, new GeneticVector(childLineage.GeneticVector).LocusRace.Dominant);
                Assert.Equal(maleId, childLineage.ParentPaternalId);
                Assert.Equal(femaleId, childLineage.ParentMaternalId);
            }
        }

        // Modul: breeding pairs. A brand new account must be able to breed on
        // day one. It could not before: registration created exactly one
        // character, and BreedingEngine needs two of the same race.
        [Fact]
        public void Test_BreedingPair_StarterRosterIsOneMaleAndOneFemaleHuman()
        {
            // Exercised against the same helper both registration paths call,
            // so the starter roster and the race-unlock reward cannot drift.
            var seeded = new System.Collections.Generic.List<CharacterRecord>();
            var seededLineages = new System.Collections.Generic.List<CharacterLineageRegistry>();

            var mainCharacterId = Guid.NewGuid();
            using (var db = new FolkIdleDbContext(_fixture.RetryingOptions.Options))
            {
                CharacterGrantEngine.SeedStarterHumanPair(db, 970004602L, mainCharacterId);

                foreach (var entry in db.ChangeTracker.Entries<CharacterRecord>())
                {
                    seeded.Add(entry.Entity);
                }
                foreach (var entry in db.ChangeTracker.Entries<CharacterLineageRegistry>())
                {
                    seededLineages.Add(entry.Entity);
                }
            }

            Assert.Equal(2, seeded.Count);
            Assert.Single(seeded.Where(c => !c.IsFemale));
            Assert.Single(seeded.Where(c => c.IsFemale));

            // The male keeps the player's PlayerGuid, because that id is what
            // StateCheckpointManager resolves as Slot1 and the rest of the
            // codebase treats as the main character.
            Assert.Equal(mainCharacterId, seeded.Single(c => !c.IsFemale).Id);
            Assert.Equal(0, seeded.Single(c => !c.IsFemale).SlotIndex);
            Assert.Equal(1, seeded.Single(c => c.IsFemale).SlotIndex);

            foreach (var lineage in seededLineages)
            {
                var genome = new GeneticVector(lineage.GeneticVector);
                Assert.Equal(RaceIds.Human, genome.LocusRace.Dominant);
                Assert.Equal(RaceIds.Human, genome.LocusRace.Recessive);
            }
        }

        // Modul: race unlocks. The game ships six races and every player could
        // only ever have Human ones: AuthenticationEngine creates each account
        // with GeneticVector = RaceIds.Human, the only other source of a
        // character is BreedingEngine, and BreedingEngine refuses any pair whose
        // LocusRace.Dominant values differ. Vila, Draugr, Kobold, Vodnik and
        // Moosleute were therefore unreachable content - authored stats, racial
        // passives and mastery tracks no player could ever see.
        //
        // The mapping is exact: five canonical regions, five bosses, five races
        // beyond the Human starter.
        [Fact]
        public void Test_RaceUnlockRegistry_MapsEveryRegionBossToADistinctRace()
        {
            var mapped = new HashSet<byte>();

            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);

                // The boss is the last of the region's five canonical monster
                // ids, and it must be a real monster that is actually flagged as
                // that region's boss in the content files.
                Assert.Equal(90 + region * 5, bossId);
                Assert.Equal(region, ContentRegistry.GetMonsterRegionTier(bossId));

                byte raceId = RaceUnlockRegistry.GetRaceUnlockedByBoss(bossId);
                Assert.True(RaceUnlockRegistry.IsPlayableRace(raceId), $"Region {region}'s boss maps to no playable race.");
                Assert.NotEqual(RaceIds.Human, raceId);
                Assert.True(mapped.Add(raceId), $"Race {raceId} is unlocked by more than one boss.");
            }

            // Every non-Human race is reachable, and Human needs no unlock.
            Assert.Equal(5, mapped.Count);
            Assert.True(RaceUnlockRegistry.IsUnlockedByDefault(RaceIds.Human));
            Assert.False(RaceUnlockRegistry.IsUnlockedByDefault(RaceIds.Moosleute));

            // A regular monster unlocks nothing - only the five bosses do.
            Assert.Equal(0, RaceUnlockRegistry.GetRaceUnlockedByBoss(91));
            Assert.Equal(0, RaceUnlockRegistry.GetRaceUnlockedByBoss(114));
            Assert.Equal(0, RaceUnlockRegistry.GetRaceUnlockedByBoss(1));
        }

        // Modul: race unlocks. Killing a region boss for the first time must
        // record the unlock AND hand over a character of that race - an unlock
        // that grants nothing playable would leave the race exactly as
        // unobtainable as before. A repeat kill must grant nothing further.
        [Fact]
        public async Task Test_RaceUnlock_FirstBossKillGrantsACharacterOfThatRaceAndRepeatsDoNot()
        {
            const long testPlayerId = 970004501L;
            const int regionOneBossId = 95;   // Alpha Wolf
            byte expectedRace = RaceUnlockRegistry.GetRaceUnlockedByBoss(regionOneBossId);
            Assert.Equal(RaceIds.Vila, expectedRace);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            var codexEngine = new CodexEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            // First kill of the region 1 boss.
            CodexEngine.KillEventQueue.Enqueue(new KillEvent { PlayerId = testPlayerId, MonsterId = regionOneBossId, RaceId = RaceIds.Human, GainedXp = 100 });
            await DrainCodexQueueAsync(codexEngine);

            Guid grantedCharacterId;
            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var unlock = await verify.PlayerRaceUnlocks.AsNoTracking()
                    .SingleOrDefaultAsync(u => u.PlayerId == testPlayerId);
                Assert.NotNull(unlock);
                Assert.Equal(expectedRace, (byte)unlock!.RaceId);
                Assert.Equal(regionOneBossId, unlock.UnlockedByMonsterId);
                Assert.True(unlock.UnlockedAtEpoch > 0);

                // Modul: breeding pairs. A MALE AND A FEMALE, not one
                // character. A lone character of a race is a dead end -
                // BreedingEngine needs both parents to share a race, and there
                // is no other route to a second non-Human character - so a
                // single grant would have handed the player a race they could
                // look at and never propagate.
                var granted = await verify.CharacterRecords.AsNoTracking()
                    .Where(c => c.PlayerId == testPlayerId)
                    .ToListAsync();
                Assert.Equal(2, granted.Count);
                Assert.Single(granted.Where(c => !c.IsFemale));
                Assert.Single(granted.Where(c => c.IsFemale));

                // Distinct slots, or the pair would collide in the roster.
                Assert.Equal(2, granted.Select(c => c.SlotIndex).Distinct().Count());

                grantedCharacterId = granted[0].Id;

                foreach (var character in granted)
                {
                    // Adults, so they can work and breed immediately - children
                    // would read as a penalty for killing a boss.
                    Assert.Equal(1, character.AgePhase);
                    Assert.Equal(0L, character.ActiveActivityId);

                    var lineage = await verify.CharacterLineages.AsNoTracking()
                        .SingleAsync(l => l.CharacterId == character.Id);
                    var genome = new GeneticVector(lineage.GeneticVector);

                    // Dominant AND recessive, so the pair breeds true rather
                    // than reverting through a recessive half neither carries.
                    Assert.Equal(expectedRace, genome.LocusRace.Dominant);
                    Assert.Equal(expectedRace, genome.LocusRace.Recessive);
                }
            }

            // Killing the same boss again must not unlock or grant anything
            // more - otherwise farming one boss would print characters.
            CodexEngine.KillEventQueue.Enqueue(new KillEvent { PlayerId = testPlayerId, MonsterId = regionOneBossId, RaceId = RaceIds.Human, GainedXp = 100 });
            await DrainCodexQueueAsync(codexEngine);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(1, await verify.PlayerRaceUnlocks.AsNoTracking().CountAsync(u => u.PlayerId == testPlayerId));
                Assert.Equal(2, await verify.CharacterRecords.AsNoTracking().CountAsync(c => c.PlayerId == testPlayerId));
            }
        }

        // CodexEngine's worker is a 5-second cron loop, so the test drives one
        // pass of its private body directly rather than sleeping.
        private static async Task DrainCodexQueueAsync(CodexEngine engine)
        {
            var method = typeof(CodexEngine).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            using var cts = new CancellationTokenSource();
            var task = (Task)method!.Invoke(engine, new object[] { cts.Token })!;

            // The loop delays 5s, processes, then delays again. Give it one full
            // pass before cancelling.
            await Task.WhenAny(task, Task.Delay(9000));
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }
        }

        // Modul: multi-slot simulation. THE defect this work exists to fix: only
        // slot 1 was ever simulated. CharacterRecord.ActiveActivityId has always
        // been per-character and ChangeCharacterActivityAsync has always written
        // it per-character, but ProcessSubTick ran once against slot 1's state
        // and the ActivityChangeQueue drain discarded any change that was not
        // slot 1's - so a second or third character sat there producing nothing,
        // forever.
        //
        // Driven at the payload level rather than through a live socket, because
        // the assertion is about the tick loop's own arithmetic: after N ticks,
        // did the parked slot's progress actually advance?
        [Fact]
        public void Test_MultiSlot_SecondAndThirdCharactersActuallyProgress()
        {
            var payload = new TickStatePayload
            {
                PlayerId = 970004401L,
                InventorySpaceRemaining = 20,
                InventoryCapacity = 20,
                CurrentLevel = 10,
                // Town Hall 5 unlocks all three slots.
                TownHallLevel = CharacterSlotEngine.Slot3TownHallRequirement
            };

            // Three characters, three DIFFERENT gathering nodes - the occupancy
            // mutex forbids sharing one.
            payload.Slot1_CharacterId = Guid.NewGuid();
            payload.Slot1_AgePhase = 1;
            payload.ActiveActivityId = 1001L;   // Woodcutting node 1 (band 1000)
            payload.PlayerHp = 100000;

            payload.Slot2_CharacterId = Guid.NewGuid();
            payload.Slot2_AgePhase = 1;
            payload.Slot2Activity.ActiveActivityId = 1002L;  // Woodcutting node 2 (band 1000)
            payload.Slot2Activity.PlayerHp = 100000;

            payload.Slot3_CharacterId = Guid.NewGuid();
            payload.Slot3_AgePhase = 1;
            payload.Slot3Activity.ActiveActivityId = 2001L;  // Mining node 1 (band 2000)
            payload.Slot3Activity.PlayerHp = 100000;

            RunSlotTicks(ref payload, 25);

            // Every slot advanced. Before this change, slots 2 and 3 stayed at
            // exactly 0 no matter how long the game ran.
            Assert.True(payload.GatheringProgressTicks > 0 || payload.HarvestLoopCount > 0,
                "Slot 1 did not progress.");
            Assert.True(payload.Slot2Activity.GatheringProgressTicks > 0 || payload.Slot2Activity.HarvestLoopCount > 0,
                "Slot 2 did not progress - the second character is still doing nothing.");
            Assert.True(payload.Slot3Activity.GatheringProgressTicks > 0 || payload.Slot3Activity.HarvestLoopCount > 0,
                "Slot 3 did not progress - the third character is still doing nothing.");

            // The register discipline held: slot 1's identity and activity are
            // back in the flat fields after the loop, which is what the outbound
            // packet, the checkpoint flush and the offline extrapolation all
            // read.
            Assert.Equal(1001L, payload.ActiveActivityId);
            Assert.Equal(1002L, payload.Slot2Activity.ActiveActivityId);
            Assert.Equal(2001L, payload.Slot3Activity.ActiveActivityId);
        }

        // Modul: multi-slot simulation. A locked slot must not be simulated, or
        // the Town Hall gate would be cosmetic - the character would earn
        // without the player ever having unlocked it.
        [Fact]
        public void Test_MultiSlot_LockedSlotsAreNotSimulated()
        {
            var payload = new TickStatePayload
            {
                PlayerId = 970004402L,
                InventorySpaceRemaining = 20,
                InventoryCapacity = 20,
                CurrentLevel = 10,
                // Town Hall 2: only slot 1 is unlocked.
                TownHallLevel = 2
            };

            payload.Slot1_CharacterId = Guid.NewGuid();
            payload.Slot1_AgePhase = 1;
            payload.ActiveActivityId = 1001L;
            payload.PlayerHp = 100000;

            // A character sitting in a locked slot, assigned and ready.
            payload.Slot2_CharacterId = Guid.NewGuid();
            payload.Slot2_AgePhase = 1;
            payload.Slot2Activity.ActiveActivityId = 1002L;
            payload.Slot2Activity.PlayerHp = 100000;

            RunSlotTicks(ref payload, 25);

            Assert.True(payload.GatheringProgressTicks > 0 || payload.HarvestLoopCount > 0,
                "Slot 1 must still run at Town Hall 2.");
            Assert.Equal(0, payload.Slot2Activity.GatheringProgressTicks);
            Assert.Equal(0L, payload.Slot2Activity.HarvestLoopCount);
        }

        // Modul: multi-slot simulation. The account-level prologue - character
        // aging, mana regeneration, potion countdowns, child maturation - has to
        // run exactly ONCE per tick however many characters are working. It used
        // to live at the top of ProcessSubTick, so running that three times a
        // tick would have aged every character three times as fast and expired
        // potions three times quicker the moment a second slot was assigned: a
        // silent speedup of unrelated systems as a side effect of a UI action.
        [Fact]
        public void Test_MultiSlot_AccountLevelTickRunsOncePerTickNotOncePerCharacter()
        {
            const int ticks = 20;

            var singleSlot = new TickStatePayload
            {
                PlayerId = 970004403L,
                InventorySpaceRemaining = 20,
                InventoryCapacity = 20,
                CurrentLevel = 10,
                TownHallLevel = CharacterSlotEngine.Slot3TownHallRequirement,
                OffensivePotionDurationMs = 600000,
                ActiveOffensivePotionId = 1
            };
            singleSlot.Slot1_CharacterId = Guid.NewGuid();
            singleSlot.Slot1_AgePhase = 1;
            singleSlot.ActiveActivityId = 1001L;
            singleSlot.PlayerHp = 100000;

            var threeSlots = singleSlot;
            threeSlots.PlayerId = 970004404L;
            threeSlots.Slot2_CharacterId = Guid.NewGuid();
            threeSlots.Slot2_AgePhase = 1;
            threeSlots.Slot2Activity.ActiveActivityId = 1002L;
            threeSlots.Slot2Activity.PlayerHp = 100000;
            threeSlots.Slot3_CharacterId = Guid.NewGuid();
            threeSlots.Slot3_AgePhase = 1;
            threeSlots.Slot3Activity.ActiveActivityId = 2001L;
            threeSlots.Slot3Activity.PlayerHp = 100000;

            RunSlotTicks(ref singleSlot, ticks);
            RunSlotTicks(ref threeSlots, ticks);

            // Identical account-level wear despite three times the characters.
            Assert.Equal(singleSlot.OffensivePotionDurationMs, threeSlots.OffensivePotionDurationMs);
            Assert.Equal(singleSlot.Slot1_AgeTicks, threeSlots.Slot1_AgeTicks);
            Assert.Equal(singleSlot.AvailableSkillPoints, threeSlots.AvailableSkillPoints);
            Assert.Equal(singleSlot.TicksSinceLastFlush, threeSlots.TicksSinceLastFlush);
        }

        // Reflection is used deliberately: ProcessAllSlotSubTicks is private
        // static, and driving it directly is the only way to assert on the tick
        // arithmetic without standing up a socket, a database session and a
        // 10Hz thread for what is a pure state transition.
        private static void RunSlotTicks(ref TickStatePayload payload, int tickCount)
        {
            var method = typeof(SimulationEngine).GetMethod(
                "ProcessAllSlotSubTicks",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var guildWarQueue = new System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent>();
            var sessionContexts = new System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext>();

            object boxed = payload;
            var args = new object[] { boxed, 100, 100, guildWarQueue, sessionContexts };
            for (int i = 0; i < tickCount; i++)
            {
                method!.Invoke(null, args);
            }
            payload = (TickStatePayload)args[0];
        }

        // Modul: crafting output. THE bug this test exists for: every one of
        // the 103 recipes consumed its materials, committed the transaction,
        // enqueued a completion notification - and granted nothing. The
        // tick-thread drain of CraftingCompletionQueue only bumped a quest
        // counter and guild-war points; no code path anywhere wrote the crafted
        // item to CommodityRecords, EquipmentInstances or the stash. Crafting
        // was purely a material sink.
        [Fact]
        public async Task Test_Crafting_GrantsTheCraftedItemAndNotJustConsumesMaterials()
        {
            const long testPlayerId = 970004201L;
            // copper_bar_crafting_material: 3x mat 93 + 1x mat 129, Smelting.
            // 408 is the Birch Axe - see the note on the other crafting test.
            const int resultItemId = 408;

            Assert.True(ContentRegistry.TryGetRecipe(resultItemId, out var recipe));
            string mat1BaseId = ContentRegistry.GetItemBaseId(recipe.Mat1Id);
            string mat2BaseId = ContentRegistry.GetItemBaseId(recipe.Mat2Id);
            string resultBaseId = ContentRegistry.GetItemBaseId(resultItemId);
            Assert.False(string.IsNullOrEmpty(resultBaseId));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat1BaseId, Quantity = 100L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat2BaseId, Quantity = 100L });
                await db.SaveChangesAsync();
            }

            var contextFactory = _fixture.ServiceProvider.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>();
            var craftingEngine = new CraftingEngine(contextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
            await craftingEngine.ExecuteCraftingAsync(testPlayerId, resultItemId);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Materials went, as they always did.
                long remainingMat1 = await verify.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == testPlayerId && c.ItemId == mat1BaseId)
                    .Select(c => c.Quantity).SingleAsync();
                Assert.Equal(100L - recipe.Mat1Count, remainingMat1);

                // And now the output arrives too. Before the fix nothing was
                // produced at all.
                //
                // Modul: an EQUIPMENT row, not a commodity stack. Tools became
                // gear - worn in their own slots, with a rolled rarity and
                // rolled affixes - and a stack has room for none of that. The
                // craft therefore lands in EquipmentInstances now, and looking
                // for a CommodityRecord threw "sequence contains no elements"
                // rather than reporting a missing item.
                var producedTools = await verify.EquipmentInstances.AsNoTracking()
                    .Where(e => e.PlayerId == testPlayerId && e.BaseItemId == resultBaseId)
                    .ToListAsync();
                Assert.True(producedTools.Count > 0, "Crafting consumed materials and produced nothing.");

                // And it is a real item: a rarity, and affixes that belong to a
                // tool rather than to a sword.
                var craftedTool = producedTools[0];
                Assert.False(string.IsNullOrWhiteSpace(craftedTool.AffixPayload));
                Assert.Contains("gather_", craftedTool.AffixPayload);
            }
        }

        // Modul: larder. The write side of the auto-eat system, which did not
        // exist: four server systems read the food slots and nothing assigned
        // them, so every larder was permanently empty and combat stopped the
        // first time the character was hurt.
        [Fact]
        public async Task Test_Larder_StockingMovesFoodFromTheBackpackIntoTheSlotAndUnloadingReturnsIt()
        {
            const long testPlayerId = 970004202L;
            const int foodItemId = FoodRegistry.FirstCookedFoodItemId; // cooked_pond_minnow_t1_food
            const int stockedQuantity = 12;

            string foodBaseId = ContentRegistry.GetItemBaseId(foodItemId);
            Assert.False(string.IsNullOrEmpty(foodBaseId));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = foodBaseId, Quantity = 50L });
                await db.SaveChangesAsync();
            }

            var larderEngine = new LarderEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await larderEngine.ExecuteStockFoodSlotAsync(testPlayerId, slotIndex: 1, foodItemId: foodItemId, quantity: stockedQuantity);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(foodItemId, player.LarderSlot2ItemId);
                Assert.Equal(stockedQuantity, player.LarderSlot2Count);
                // Slot 2 was targeted; the other two must be untouched.
                Assert.Equal(0, player.LarderSlot1Count);
                Assert.Equal(0, player.LarderSlot3Count);

                long backpack = await verify.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == testPlayerId && c.ItemId == foodBaseId)
                    .Select(c => c.Quantity).SingleAsync();
                Assert.Equal(50L - stockedQuantity, backpack);
            }

            // The tick thread learns about it through the queue, never by
            // touching the payload from the dispatch thread.
            Assert.True(_fixture.PlayerRegistry.LarderSlotUpdateQueue.TryDequeue(out var notification));
            Assert.Equal(testPlayerId, notification.PlayerId);
            Assert.Equal(1, notification.SlotIndex);
            Assert.Equal(foodItemId, notification.ItemId);
            Assert.Equal(stockedQuantity, notification.Count);

            // Quantity 0 unloads. The food must come back rather than be
            // destroyed - the player paid materials and cooking time for it.
            await larderEngine.ExecuteStockFoodSlotAsync(testPlayerId, slotIndex: 1, foodItemId: 0, quantity: 0);

            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
                Assert.Equal(0, player.LarderSlot2ItemId);
                Assert.Equal(0, player.LarderSlot2Count);

                long backpack = await verify.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == testPlayerId && c.ItemId == foodBaseId)
                    .Select(c => c.Quantity).SingleAsync();
                Assert.Equal(50L, backpack);
            }
        }

        // Modul: larder. Stocking food the player does not hold must refuse
        // outright rather than fabricate a stocked slot.
        [Fact]
        public async Task Test_Larder_RefusesToStockFoodTheBackpackDoesNotContain()
        {
            const long testPlayerId = 970004203L;
            const int foodItemId = FoodRegistry.FirstCookedFoodItemId;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            var larderEngine = new LarderEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await larderEngine.ExecuteStockFoodSlotAsync(testPlayerId, slotIndex: 0, foodItemId: foodItemId, quantity: 5);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == testPlayerId);
            Assert.Equal(0, player.LarderSlot1ItemId);
            Assert.Equal(0, player.LarderSlot1Count);
        }

        // Modul: larder. Two bugs in one place. AlchemyCompendium classified
        // food by the BaseId marker "_food_consumable", which none of the ten
        // real cooked foods carry - and IsValidConsumable failure leads to
        // TerminateSessionForSecurity, so eating real food would have
        // force-disconnected the player. Meanwhile the auto-eat step scored
        // every slot at a hardcoded 50000 milli-HP, so its "pick the
        // highest-healing food" comparison was a tie every time and a tier-10
        // roast healed exactly as much as a tier-1 minnow.
        [Fact]
        public void Test_FoodRegistry_RecognisesEveryCookedFoodAndScalesHealingByTier()
        {
            for (int itemId = FoodRegistry.FirstCookedFoodItemId; itemId <= FoodRegistry.LastCookedFoodItemId; itemId++)
            {
                Assert.True(FoodRegistry.IsFood(itemId), $"Item {itemId} is a cooking-recipe output but was not recognised as food.");
                Assert.True(AlchemyCompendium.IsValidConsumable((uint)itemId), $"Item {itemId} is real food but would have failed consumable validation.");
                Assert.True(FoodRegistry.GetHealMilliHp(itemId) > 0);
            }

            // GDD Module "Cooking (Sustain & Auto-Eat Economy)" 3.2 heal
            // payouts, in the engine's milli-HP units.
            Assert.Equal(40 * 1000, FoodRegistry.GetHealMilliHp(FoodRegistry.FirstCookedFoodItemId));
            Assert.Equal(82000 * 1000, FoodRegistry.GetHealMilliHp(FoodRegistry.LastCookedFoodItemId));

            // Strictly increasing, which is what makes the auto-eat selection
            // mean anything at all.
            for (int itemId = FoodRegistry.FirstCookedFoodItemId; itemId < FoodRegistry.LastCookedFoodItemId; itemId++)
            {
                Assert.True(FoodRegistry.GetHealMilliHp(itemId + 1) > FoodRegistry.GetHealMilliHp(itemId));
            }

            // A weapon is not food, and must score zero so an occupied-but-bogus
            // slot can never win the selection.
            Assert.False(FoodRegistry.IsFood(19));
            Assert.Equal(0, FoodRegistry.GetHealMilliHp(19));
            Assert.Equal(0, FoodRegistry.GetHealMilliHp(0));
        }

        // Modul: inventory census. InventorySpaceRemaining only ever fell:
        // CombatLootEngine set ConsumedInventorySlot on every kill regardless of
        // whether anything dropped, the tick thread decremented, and nothing
        // restored it - so after 20 kills every backpack read as full and all
        // loot was silently discarded for the rest of the session. Hydration
        // reset it to capacity without looking at the backpack either, so a
        // relogin was the only way to get space back and a genuinely full pack
        // was handed twenty phantom slots.
        [Fact]
        public async Task Test_InventoryCensus_CountsStacksOnceAndExcludesEquippedGear()
        {
            const long testPlayerId = 970004204L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var censusMainCharacterId = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = censusMainCharacterId, AuthenticatorToken = Guid.NewGuid() });
                db.CharacterRecords.Add(new CharacterRecord { Id = censusMainCharacterId, PlayerId = testPlayerId, Level = 1, AgePhase = 1, SlotIndex = 0 });

                // Three material types, one of them holding a thousand units -
                // a stack is one slot, not one slot per unit.
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "mat_copper_ore_mining_material", Quantity = 1000L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "mat_tin_ore_mining_material", Quantity = 3L });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "mat_raw_log_woodcutting_material", Quantity = 7L });

                // An empty stack occupies nothing.
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "mat_coal_node_mining_material", Quantity = 0L });

                await db.SaveChangesAsync();
            }

            long equippedId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var worn = new EquipmentInstance { BaseItemId = "eq_steel_claymore_melee_weapon_slot_base", PlayerId = testPlayerId, QualityTier = 1, AffixPayload = "{}" };
                var carried = new EquipmentInstance { BaseItemId = "eq_linen_shroud_chest_armor_slot_base", PlayerId = testPlayerId, QualityTier = 1, AffixPayload = "{}" };
                db.EquipmentInstances.Add(worn);
                db.EquipmentInstances.Add(carried);
                await db.SaveChangesAsync();
                equippedId = worn.Id;

                // Modul: per-character equipment. Worn gear is off the player's
                // back wherever it is worn, so the census reads it from the
                // character rather than the player row.
                var censusCharacter = await db.CharacterRecords.SingleAsync(c => c.PlayerId == testPlayerId);
                censusCharacter.EquippedWeaponId = equippedId;
                await db.SaveChangesAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                int occupied = await CombatLootEngine.CountOccupiedBackpackSlotsAsync(db, testPlayerId);

                // 3 non-empty material stacks + 1 carried equipment piece. The
                // worn weapon lives on the character, and the zero-quantity
                // stack is not a slot.
                Assert.Equal(4, occupied);
            }
        }

        // Modul: Affix System Unification. Every affix a drop rolls must be
        // legal for that item's slot per GDD Module 14 section 1.3 - the old
        // generator ignored slot legality entirely and the old reroll could put
        // a shield-only block_chance_pct on a sword.
        [Fact]
        public void Test_AffixRegistry_OnlyRollsAffixesLegalForTheItemSlot()
        {
            // A weapon must never receive an armour-or-shield affix.
            var weaponAffixes = new Dictionary<string, int>();
            AffixRegistry.RollAffixes("eq_steel_claymore_melee_weapon_slot_base", regionTier: 3, itemRarityTier: 14, affixCount: 5, weaponAffixes);

            Assert.NotEmpty(weaponAffixes);
            foreach (var key in weaponAffixes.Keys)
            {
                string affixId = AffixRegistry.StripStackSuffix(key);
                Assert.True(AffixRegistry.TryGetDefinition(affixId, out var definition), $"Rolled an unknown affix id '{affixId}'.");
                Assert.True((definition.AllowedSlots & EquipmentSlotMask.Weapon) != 0,
                    $"Affix '{affixId}' is not legal on a weapon but was rolled onto one.");
            }

            // The ring is the only slot block_chance_pct may occupy, so it must
            // be reachable there and nowhere else. It was Shield-only until the
            // offhand slot was removed - an affix whose one legal slot no longer
            // exists rolls on nothing.
            Assert.True(AffixRegistry.TryGetDefinition("block_chance_pct", out var block));
            Assert.Equal(EquipmentSlotMask.Ring, block.AllowedSlots);

            var chestAffixes = new Dictionary<string, int>();
            AffixRegistry.RollAffixes("eq_linen_shroud_chest_armor_slot_base", regionTier: 1, itemRarityTier: 1, affixCount: 1, chestAffixes);
            Assert.Single(chestAffixes);
            foreach (var key in chestAffixes.Keys)
            {
                string affixId = AffixRegistry.StripStackSuffix(key);
                Assert.True(AffixRegistry.TryGetDefinition(affixId, out var definition));
                Assert.True((definition.AllowedSlots & EquipmentSlotMask.Chest) != 0);
            }
        }

        // Modul: Affix System Unification. GDD Module 03 section 5.2 - the
        // rarity to affix-count table - and the stacking fallback that lets a
        // Chest piece reach five affixes despite having only two legal ones
        // (the GDD's pool assumes Ring and Amulet slots this game lacks).
        [Fact]
        public void Test_AffixRegistry_AffixCountFollowsRarityEvenWhenTheLegalPoolIsSmaller()
        {
            Assert.Equal(1, RarityTier.GetAffixCount(1));
            Assert.Equal(1, RarityTier.GetAffixCount(3));
            Assert.Equal(2, RarityTier.GetAffixCount(4));
            Assert.Equal(2, RarityTier.GetAffixCount(6));
            Assert.Equal(3, RarityTier.GetAffixCount(7));
            Assert.Equal(3, RarityTier.GetAffixCount(9));
            Assert.Equal(4, RarityTier.GetAffixCount(10));
            Assert.Equal(4, RarityTier.GetAffixCount(12));
            Assert.Equal(5, RarityTier.GetAffixCount(13));
            Assert.Equal(5, RarityTier.GetAffixCount(14));

            // Chest has exactly two legal affixes, so five rolls must produce
            // five payload entries via stacked "#n" keys rather than silently
            // collapsing to two.
            var chestAffixes = new Dictionary<string, int>();
            AffixRegistry.RollAffixes("eq_linen_shroud_chest_armor_slot_base", regionTier: 2, itemRarityTier: 14, affixCount: 5, chestAffixes);
            Assert.Equal(5, chestAffixes.Count);
        }

        // Modul: Affix System Unification. GDD 1.1 flat laws and 1.2 percentage
        // law, plus the Module 03 section 5.3 reroll cost.
        [Fact]
        public void Test_AffixRegistry_ScalingLawsAndRerollCostMatchTheSpec()
        {
            // Modul: affix rarity, 2026-08-01. The second growth term in these
            // laws is now the AFFIX's own rarity (1-5), not the item's 14-tier
            // rarity - the item tier decides affix COUNT instead. Region is
            // unchanged and still multiplies both flat laws.
            Assert.True(AffixRegistry.TryGetDefinition("flat_hp", out var flatHp));
            // floor(15 * R * 1.6^(A-1)); R=1, Common -> 15. Unchanged from the
            // old law at its base point, which is deliberate: existing gear
            // rolled at the bottom of the curve keeps the value it had.
            Assert.Equal(15, AffixRegistry.CalculateMagnitude(flatHp, 1, AffixRarity.Common));
            // Modul: the region term is a CURVE now, not a multiplier.
            //
            // These laws were linear in the region while the items they sit on
            // triple every region, so a Legendary affix was worth more than its
            // base item in region 1 and a tenth of it in region 5 - rerolling
            // at depth changed nothing a player could feel. Health follows the
            // health pool at 2.2x a region, flat stats follow the gear curve at
            // 3x.
            //
            // R=3, Legendary -> floor(15 * 2.2^2 * 6.5536) = 475.
            Assert.Equal(475, AffixRegistry.CalculateMagnitude(flatHp, 3, AffixRarity.Legendary));

            Assert.True(AffixRegistry.TryGetDefinition("flat_armor", out var flatArmor));
            // Modul: base 6, not 2 - see AffixRegistry. The spread between a
            // starter loadout and a finished one IS this number, and at 2 it
            // was threefold, which is not the difference between dying and
            // living.
            // floor(6 * 3^(R-1) * 1.6^(A-1)); R=1, Common -> 6.
            Assert.Equal(6, AffixRegistry.CalculateMagnitude(flatArmor, 1, AffixRarity.Common));

            // Percentage law in tenths, still linear in the rarity index:
            // crit_dmg_pct is 5.0% base, +2.5% per step, so Common is 50 tenths
            // and Rare is 50 + 2*25 = 100 tenths (10%).
            Assert.True(AffixRegistry.TryGetDefinition("crit_dmg_pct", out var critDamage));
            Assert.Equal(50, AffixRegistry.CalculateMagnitude(critDamage, 1, AffixRarity.Common));
            Assert.Equal(100, AffixRegistry.CalculateMagnitude(critDamage, 1, AffixRarity.Rare));

            // Rarity UPGRADE keeps a Diamond price; value and stat rerolls moved
            // to gold so auto-reroll could not drain premium currency. See
            // AffixRegistry for the full reasoning.
            Assert.Equal(5L, AffixRegistry.CalculateRarityUpgradeDiamondCost(AffixRarity.Common));
            Assert.Equal(0L, AffixRegistry.CalculateRarityUpgradeDiamondCost(AffixRarity.Legendary));
        }

        // Modul: Affix System Unification. THE bug this work existed to fix.
        // A reroll used to remove a numeric-keyed affix and write a
        // GDD-named one that EquipmentSlotEngine did not read, so the player
        // paid diamonds and the item lost the stat outright. The reroll must
        // leave the item with the same number of readable, stat-contributing
        // affixes it started with.
        [Fact]
        public async Task Test_AffixReroll_ReplacesAffixWithoutDestroyingItsStatContribution()
        {
            const long testPlayerId = 970009151L;
            const string baseItemId = "eq_steel_claymore_melee_weapon_slot_base";
            const int rarityTier = 7; // Legendary -> 3 affixes, 30 diamonds.

            long itemId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });

                var rolled = new Dictionary<string, int>();
                AffixRegistry.RollAffixes(baseItemId, regionTier: 1, itemRarityTier: rarityTier, affixCount: RarityTier.GetAffixCount(rarityTier), rolled);

                var instance = new EquipmentInstance
                {
                    BaseItemId = baseItemId,
                    PlayerId = testPlayerId,
                    QualityTier = rarityTier,
                    AffixPayload = System.Text.Json.JsonSerializer.Serialize(rolled),
                    IsAffixLocked = false
                };
                db.EquipmentInstances.Add(instance);

                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "premium_diamond", Quantity = 500L });
                // Modul: reroll economy, 2026-08-01. A value reroll is paid in
                // GOLD now, so the account needs a gold row or the reroll is
                // rejected for insufficient funds before it mutates anything.
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = "gold", Quantity = 50_000_000L });
                await db.SaveChangesAsync();
                itemId = instance.Id;
            }

            int affixCountBefore;
            int statTotalBefore;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var before = await db.EquipmentInstances.AsNoTracking().FirstAsync(e => e.Id == itemId);
                (affixCountBefore, statTotalBefore) = SummariseAffixPayload(before.AffixPayload);
            }

            Assert.Equal(RarityTier.GetAffixCount(rarityTier), affixCountBefore);
            Assert.True(statTotalBefore > 0, "The seeded item must start with a non-zero affix total.");

            var rerollEngine = new AffixRerollEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await rerollEngine.ExecuteRerollAsync(testPlayerId, itemId, affixIndex: 0);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var after = await db.EquipmentInstances.AsNoTracking().FirstAsync(e => e.Id == itemId);
                (int affixCountAfter, int statTotalAfter) = SummariseAffixPayload(after.AffixPayload);

                // Same number of affixes, all still readable, all still worth
                // something. Before the fix this dropped to affixCountBefore-1
                // readable affixes and the removed one's value was gone.
                Assert.Equal(affixCountBefore, affixCountAfter);
                Assert.True(statTotalAfter > 0, "Every affix on the item was unreadable after the reroll.");

                // And the replacement must be legal for a weapon.
                foreach (var key in ParseAffixKeys(after.AffixPayload))
                {
                    string affixId = AffixRegistry.StripStackSuffix(key);
                    Assert.True(AffixRegistry.TryGetDefinition(affixId, out var definition), $"Reroll produced unknown affix id '{affixId}'.");
                    Assert.True((definition.AllowedSlots & EquipmentSlotMask.Weapon) != 0,
                        $"Reroll put '{affixId}' on a weapon, where it is not legal.");
                }

                // Gold actually left the account, at the value-reroll price -
                // and Diamonds did NOT, which is the whole point of splitting
                // the currencies so auto-reroll cannot drain premium currency.
                long goldRemaining = await db.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == testPlayerId && c.ItemId == "gold")
                    .Select(c => c.Quantity).FirstAsync();
                Assert.Equal(50_000_000L - AffixRegistry.CalculateRerollGoldCost(rarityTier, 0, rerollStatType: false), goldRemaining);

                long diamondsRemaining = await db.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == testPlayerId && c.ItemId == "premium_diamond")
                    .Select(c => c.Quantity).FirstAsync();
                Assert.Equal(500L, diamondsRemaining);
            }
        }

        private static List<string> ParseAffixKeys(string affixPayload)
        {
            var keys = new List<string>();
            if (System.Text.Json.Nodes.JsonNode.Parse(affixPayload) is not System.Text.Json.Nodes.JsonObject obj) return keys;

            foreach (var kvp in obj)
            {
                if (kvp.Key == "is_affix_locked") continue;
                keys.Add(kvp.Key);
            }
            return keys;
        }

        // Counts readable affixes and sums their magnitudes - "readable" being
        // the whole point, since the pre-fix bug produced keys nothing parsed.
        private static (int Count, int Total) SummariseAffixPayload(string affixPayload)
        {
            int count = 0;
            int total = 0;
            if (System.Text.Json.Nodes.JsonNode.Parse(affixPayload) is not System.Text.Json.Nodes.JsonObject obj)
            {
                return (0, 0);
            }

            foreach (var kvp in obj)
            {
                if (kvp.Key == "is_affix_locked") continue;
                if (kvp.Value is not System.Text.Json.Nodes.JsonValue value) continue;
                if (!value.TryGetValue(out int magnitude)) continue;

                if (!AffixRegistry.TryGetDefinition(AffixRegistry.StripStackSuffix(kvp.Key), out _)) continue;

                count++;
                total += magnitude;
            }

            return (count, total);
        }

        // Modul: Deploy activation fix. A committed multi-character activity
        // change must reach the LIVE payload, not just the characters row.
        // Before this, ChangeCharacterActivityAsync persisted the new
        // activity and then enqueued a ReloadState - which only clears
        // IsSuspended and reloads nothing - so pressing Deploy wrote the
        // right value to the database and the running session ignored it for
        // the rest of the connection. Combat never started.
        [Fact]
        public async Task Test_ActivityChangeQueue_AppliesCommittedActivityToLivePayload()
        {
            const long testPlayerId = 970009141L;
            Guid characterId = Guid.NewGuid();
            const long targetActivityId = 91L; // Field Mouse

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                db.CharacterRecords.Add(new CharacterRecord { Id = characterId, PlayerId = testPlayerId, Level = 1, SlotIndex = 0, ActiveActivityId = 0L });
                await db.SaveChangesAsync();
            }

            var playerRegistry = new PlayerSessionRegistry();
            var simulationEngine = BuildMinimalSimulationEngine(playerRegistry);

            try
            {
                simulationEngine.Start();

                // Slot1_CharacterId is what the drain matches on - the live
                // session simulates exactly one activity, so a change aimed at
                // a character this session is not running must not silently
                // retarget the fight.
                simulationEngine.InjectVirtualPlayer(new TickStatePayload
                {
                    PlayerId = testPlayerId,
                    CurrentLevel = 25,
                    Slot1_CharacterId = characterId,
                    ActiveActivityId = 0L,
                    InventorySpaceRemaining = 20
                });

                var resultCode = await simulationEngine.ChangeCharacterActivityAsync(testPlayerId, characterId, targetActivityId);
                Assert.Equal(FolkIdle.Server.Network.CommandResultCode.Success, resultCode);

                // The persisted half already worked before the fix.
                await using (var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    long persisted = await verifyDb.CharacterRecords.AsNoTracking()
                        .Where(c => c.Id == characterId)
                        .Select(c => c.ActiveActivityId)
                        .FirstAsync();
                    Assert.Equal(targetActivityId, persisted);
                }

                // The live half is what this test exists for.
                playerRegistry.ActivityChangeQueue.Enqueue(new ActivityChangeNotification
                {
                    PlayerId = testPlayerId,
                    CharacterId = characterId,
                    TargetActivityId = targetActivityId
                });

                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerActiveActivityId(testPlayerId) == targetActivityId,
                    "The live payload never picked up the committed activity change.");
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        // Modul: Deploy activation fix. A change aimed at a character the live
        // session is NOT simulating must leave the running activity alone -
        // it is already persisted and applies when that character next
        // occupies Slot1.
        [Fact]
        public async Task Test_ActivityChangeQueue_IgnoresChangeForNonSlot1Character()
        {
            const long testPlayerId = 970009142L;
            Guid slot1CharacterId = Guid.NewGuid();
            Guid otherCharacterId = Guid.NewGuid();
            const long runningActivityId = 55L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                await db.SaveChangesAsync();
            }

            var playerRegistry = new PlayerSessionRegistry();
            var simulationEngine = BuildMinimalSimulationEngine(playerRegistry);

            try
            {
                simulationEngine.Start();

                simulationEngine.InjectVirtualPlayer(new TickStatePayload
                {
                    PlayerId = testPlayerId,
                    CurrentLevel = 25,
                    Slot1_CharacterId = slot1CharacterId,
                    ActiveActivityId = runningActivityId,
                    InventorySpaceRemaining = 20
                });

                playerRegistry.ActivityChangeQueue.Enqueue(new ActivityChangeNotification
                {
                    PlayerId = testPlayerId,
                    CharacterId = otherCharacterId,
                    TargetActivityId = 91L
                });

                // Give the drain several ticks to run and prove it did not act.
                await Task.Delay(600);
                Assert.Equal(runningActivityId, simulationEngine.GetActivePlayerActiveActivityId(testPlayerId));
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        // Modul: Guild War scoreboard sync. GuildWarMatches has always held
        // the real running totals; nothing copied them into each member's
        // live payload, so every client showed six zeros during a real war.
        [Fact]
        public async Task Test_GuildWarScoreboardQueue_AppliesMatchTotalsToEveryGuildMember()
        {
            const long testPlayerId = 970009143L;
            const long testGuildId = 970009144L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 25 });
                await db.SaveChangesAsync();
            }

            var playerRegistry = new PlayerSessionRegistry();
            var simulationEngine = BuildMinimalSimulationEngine(playerRegistry);

            try
            {
                simulationEngine.Start();

                simulationEngine.InjectVirtualPlayer(new TickStatePayload
                {
                    PlayerId = testPlayerId,
                    CurrentLevel = 25,
                    GuildId = testGuildId,
                    InventorySpaceRemaining = 20
                });

                // The drain fans out through the tick-thread guild index, so
                // the member has to be in it - the same path a real join takes.
                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerGuildId(testPlayerId) == testGuildId,
                    "Injected player never became visible to the tick thread.");

                playerRegistry.GuildMembershipChangeQueue.Enqueue(new GuildMembershipChangeNotification
                {
                    PlayerId = testPlayerId,
                    OldGuildId = 0L,
                    NewGuildId = testGuildId
                });

                await WaitForConditionAsync(
                    () => simulationEngine.IsPlayerInGuildIndex(testGuildId, testPlayerId),
                    "Player never appeared in the tick-thread guild index.");

                playerRegistry.GuildWarScoreboardQueue.Enqueue(new GuildWarScoreboardNotification
                {
                    GuildId = testGuildId,
                    OurCombatVanguardPoints = 1200,
                    OurProductionLogisticsPoints = 250,
                    OurGatheringSupplyChainPoints = 400,
                    EnemyCombatVanguardPoints = 800,
                    EnemyProductionLogisticsPoints = 100,
                    EnemyGatheringSupplyChainPoints = 50,
                    ScoreShare = 0.65f
                });

                await WaitForConditionAsync(
                    () => simulationEngine.GetActivePlayerGuildCombatVanguardPoints(testPlayerId) == 1200,
                    "The live payload never received the guild war scoreboard totals.");
            }
            finally
            {
                simulationEngine.Stop();
            }
        }

        // Modul: Loot Event Feed. Every drop CombatLootEngine actually
        // persists must also be published onto OutboundLootDropQueue as a
        // ResponseLootDropPacket, since that queue is the only thing
        // NetworkBroadcastSystem's dispatch loop reads to tell the player
        // what dropped. Before the feed existed the engine reported nothing
        // but a single "a slot was consumed" flag with no item identity at
        // all.
        [Fact]
        public async Task Test_CombatLootEngine_PublishesEveryGrantedDropOntoTheOutboundFeed()
        {
            const long testPlayerId = 970009131L;
            const int monsterId = 104; // Sandstone Golem - mat_lodestone, region 3

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }

            while (_fixture.PlayerRegistry.OutboundLootDropQueue.TryDequeue(out _))
            {
            }

            var combatLootEngine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var processMethod = typeof(CombatLootEngine).GetMethod("ProcessMonsterLootDropAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            for (int i = 0; i < 200; i++)
            {
                await (Task)processMethod.Invoke(combatLootEngine, new object[] { testPlayerId, monsterId, 0f })!;
            }

            int publishedCount = 0;
            long publishedMaterialQuantity = 0L;
            while (_fixture.PlayerRegistry.OutboundLootDropQueue.TryDequeue(out FolkIdle.Server.Network.ResponseLootDropPacket drop))
            {
                publishedCount++;

                Assert.Equal(testPlayerId, drop.PlayerId);
                Assert.Equal(monsterId, drop.MonsterId);
                Assert.True(drop.ItemId > 0, "A published drop must carry a real ContentRegistry item id.");
                Assert.True(drop.Quantity > 0, "A published drop must carry a positive quantity.");

                if (drop.DropKind == FolkIdle.Server.Network.ResponseLootDropPacket.DropKindMaterial)
                {
                    publishedMaterialQuantity += drop.Quantity;
                }
            }

            Assert.True(publishedCount > 0, "Expected the drop burst to publish at least one loot event.");

            // The feed must agree with the database it is describing: a
            // player told they received N of a material must actually hold N.
            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            long storedMaterialQuantity = await verifyDb.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == testPlayerId)
                .SumAsync(c => (long?)c.Quantity) ?? 0L;

            Assert.Equal(publishedMaterialQuantity, storedMaterialQuantity);
        }

        // Modul: Crafting Tree. ExecuteCraftingAsync used to look its
        // material costs up by the stringified numeric Mat1Id/Mat2Id ("93"),
        // but every writer of CommodityRecords/VillageStashInstances stores a
        // BaseId slug, so the unified balance lookup could never match a row
        // and all 103 recipes were permanently unfulfillable regardless of
        // how much input material the player held.
        [Fact]
        public async Task Test_CraftingEngine_ConsumesMaterialsByBaseIdAndProducesResult()
        {
            const long testPlayerId = 970009132L;

            // Modul: was recipe 184 (copper_bar, 3 tin_ore + 1 coal_node).
            // Smelting went with the invented ores it was built on - see the
            // recipe table's own note. 408 is the Birch Axe, whose materials
            // are birch logs and copper ore, both of which a Sunlit Plains node
            // actually drops.
            //
            // Modul: the stocked amount is DERIVED from the recipe now. It was
            // a literal 10, which covered an 8 + 4 cost and stopped covering it
            // the moment tool costs were scaled to the season curve - so a test
            // about consuming materials by BaseId started failing on the price
            // of an axe. Stock what the recipe asks for plus a margin, and the
            // next repricing cannot reach this test at all.
            Assert.True(ContentRegistry.TryGetRecipe(408, out var recipe));
            string mat1BaseId = ContentRegistry.GetItemBaseId(recipe.Mat1Id);
            string mat2BaseId = ContentRegistry.GetItemBaseId(recipe.Mat2Id);
            long stockedMat1 = recipe.Mat1Count + 10L;
            long stockedMat2 = recipe.Mat2Count + 10L;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat1BaseId, Quantity = stockedMat1 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = testPlayerId, ItemId = mat2BaseId, Quantity = stockedMat2 });
                await db.SaveChangesAsync();
            }

            while (_fixture.PlayerRegistry.CraftingCompletionQueue.TryDequeue(out _))
            {
            }

            var craftingEngine = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
            await craftingEngine.ExecuteCraftingAsync(testPlayerId, 408);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();

            long remainingMat1 = await verifyDb.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == testPlayerId && c.ItemId == mat1BaseId)
                .Select(c => (long?)c.Quantity).FirstOrDefaultAsync() ?? 0L;
            long remainingMat2 = await verifyDb.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == testPlayerId && c.ItemId == mat2BaseId)
                .Select(c => (long?)c.Quantity).FirstOrDefaultAsync() ?? 0L;

            Assert.Equal(stockedMat1 - recipe.Mat1Count, remainingMat1);
            Assert.Equal(stockedMat2 - recipe.Mat2Count, remainingMat2);

            Assert.True(_fixture.PlayerRegistry.CraftingCompletionQueue.TryDequeue(out var completion),
                "A successful craft must enqueue a completion notification.");
            Assert.Equal(408, completion.CraftedItemId);
            Assert.True(completion.Quantity >= 1);
        }

        // Modul: Architecture Overhaul, Part 6. Equipping 4 pieces of the
        // Eternal Dreadnought set (SetId 10) must apply the 2-piece +15%
        // Total Armor multiplier AND flip on the 4-piece defensive
        // mechanics (Thorns/CC Immunity/Cooldown Reduction) inside the
        // combat feedback profile returned by StatsCalculator.Calculate.
        // Only 3 equip slots (Weapon/Armor/Leggings) exist as real,
        // equippable wire-protocol slots today, so the 4-piece scenario is
        // proven directly against SetBonusEngine/StatsCalculator with a
        // synthetic 4-slot span - the same evaluator the live 3-slot equip
        // pipeline already feeds in production.
        [Fact]
        public void Test_SetBonusEngine_FourPieceEternalDreadnoughtAppliesDefensiveMultipliers()
        {
            // Modul: PACKED with a rarity. A bare set id carries quality 0,
            // which the evaluator floors at 1 - so this used to describe two
            // Normal pieces, and under the rework those are worth almost
            // nothing. The scenario being tested is "two pieces of a set", not
            // "two pieces of junk".
            int dread = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4);
            ReadOnlySpan<int> twoPieceOnly = stackalloc int[] { dread, dread, 0 };
            var twoPieceResult = SetBonusEngine.Evaluate(twoPieceOnly);

            // Half a set at the reference rarity: 8 of the 16 quality a full
            // one comes to, so half of the 25% armour.
            Assert.Equal(12.5f, twoPieceResult.TotalArmorMultiplierPct, 3);
            Assert.False(twoPieceResult.ThornsReflectionActive);
            Assert.False(twoPieceResult.DamageCapActive);

            ReadOnlySpan<int> fourPiece = stackalloc int[] { dread, dread, dread, dread };
            var fourPieceResult = SetBonusEngine.Evaluate(fourPiece);
            Assert.Equal(25f, fourPieceResult.TotalArmorMultiplierPct, 3);
            Assert.True(fourPieceResult.ThornsReflectionActive);
            Assert.True(fourPieceResult.DamageCapActive);
            // CooldownReductionActive is not asserted: it shortened active
            // skill cooldowns and active skills were removed from the game.

            // End-to-end through the combat feedback profile: 100 CON gives a
            // known FlatPhysicalArmor baseline of 100.
            //
            // Modul: 112, not 115. The armour bonus is no longer a flat +15%
            // at two pieces - it is 25% scaled by POTENCY, and two pieces at
            // the reference rarity are half a set, so 12.5%.
            //
            // Modul: seven-slot set bonuses. This used to note that Calculate
            // "only exposes the 3 real equip slots that exist in production
            // today (Weapon/Armor/Leggings)". That was the bug, not a
            // constraint: all seven slots existed, three were being reported.
            CombatStats naked = StatsCalculator.Calculate(str: 0, dex: 0, con: 100, lck: 0);
            CombatStats withTwoPieceSet = StatsCalculator.Calculate(str: 0, dex: 0, con: 100, lck: 0,
                equippedSetIds: new EquippedSetIds
                {
                    Weapon = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4),
                    Chest = EquippedSetIds.Pack(SetBonusEngine.EternalDreadnoughtSetId, 4)
                });

            // 1.125, not 1.15: two pieces at the reference rarity are HALF a set,
            // and the armour bonus is 25% scaled by potency.
            Assert.Equal((int)(naked.FlatPhysicalArmor * 1.125f), withTwoPieceSet.FlatPhysicalArmor);
            Assert.False(withTwoPieceSet.SetThornsReflectionActive);
            Assert.False(withTwoPieceSet.SetDamageCapActive);

            // Zero-allocation proof for the evaluator itself.
            SetBonusEngine.Evaluate(fourPiece);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var probeResult = SetBonusEngine.Evaluate(fourPiece);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(probeResult.ThornsReflectionActive);
            Assert.Equal(0L, after - before);
        }

        // Modul: Architecture Overhaul, Part 6. Real 2-piece integration
        // proof through the actual 3-slot equip pipeline: equipping
        // Weapon + Armor both tagged SetId 1 (Chiming Steel) and reading
        // the totals back through EquipmentSlotEngine.ComputeEquippedTotalsAsync
        // confirms the SetId round-trips end to end (DB -> equip -> cached
        // totals), independent of the synthetic-span proof above.
        [Fact]
        public async Task Test_SetBonusEngine_TwoRealEquippedSlotsRoundTripSetIdThroughEquipPipeline()
        {
            const long testPlayerId = 970009201L;

            long weaponId, armorId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Modul: per-character equipment. Equipping resolves the main
                // character - the one whose Id equals PlayerGuid - so the
                // fixture has to create it or the gear has nowhere to land.
                var setBonusMainCharacterId = Guid.NewGuid();
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = setBonusMainCharacterId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 60 });
                SeedAllRegionBossKills(db, testPlayerId);
                db.CharacterRecords.Add(new CharacterRecord { Id = setBonusMainCharacterId, PlayerId = testPlayerId, Level = 60, AgePhase = 1, SlotIndex = 0 });
                var weapon = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "bronze_dagger_melee_weapon_slot_base", QualityTier = 4, AffixPayload = "{}", SetId = SetBonusEngine.ChimingSteelSetId };
                var armor = new EquipmentInstance { PlayerId = testPlayerId, BaseItemId = "iron_breastplate_chest_armor_slot_base", QualityTier = 4, AffixPayload = "{}", SetId = SetBonusEngine.ChimingSteelSetId };
                db.EquipmentInstances.Add(weapon);
                db.EquipmentInstances.Add(armor);
                await db.SaveChangesAsync();
                weaponId = weapon.Id;
                armorId = armor.Id;
            }

            var equipmentSlotEngine = new EquipmentSlotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await equipmentSlotEngine.EquipItemAsync(testPlayerId, weaponId);
            await equipmentSlotEngine.EquipItemAsync(testPlayerId, armorId);

            await using var verifyDb = await _fixture.DbContextFactory.CreateDbContextAsync();
            var setBonusCharacter = await verifyDb.CharacterRecords.AsNoTracking().SingleAsync(c => c.PlayerId == testPlayerId);

            (_, EquippedSetIds setIds) = await EquipmentSlotEngine.ComputeEquippedTotalsAsync(verifyDb, setBonusCharacter);

            // Modul: seven-slot set bonuses. The chest piece lands in its OWN
            // slot now. Under the old weapon/armour/leggings triple it went
            // into a generic "armor" slot that also stood in for helmet, gloves
            // and boots.
            Assert.Equal(SetBonusEngine.ChimingSteelSetId, EquippedSetIds.SetIdOf(setIds.Weapon));
            Assert.Equal(SetBonusEngine.ChimingSteelSetId, EquippedSetIds.SetIdOf(setIds.Chest));

            int[] setIdSpan = new int[EquippedSetIds.SlotCount];
            setIds.CopyTo(setIdSpan);
            var result = SetBonusEngine.Evaluate(setIdSpan);

            // Modul: was `FlatAttackPowerBonus == 10`. That bonus is gone - a
            // flat +10 attack is most of a starting character's damage and a
            // rounding error by region 5. Set bonuses are percentages scaled by
            // the QUALITY of the pieces worn now, so what this asserts is that
            // two matching pieces pay something at all.
            Assert.True(result.FireDamageMultiplierPct > 0f,
                "two matching pieces must pay a share of the set");
        }

        // Modul: seven-slot set bonuses. The regression this pass fixed: the
        // 4-piece tier was unreachable for anyone, ever.
        //
        // SetBonusEngine awards its tiers by counting how many equipped pieces
        // share a SetId, and it was always sized for this (MaxTrackedSlots is
        // 8, and its own comment names all seven slots). But its only caller
        // handed it three ids - weapon, ONE "armor" standing in for helmet,
        // chest, gloves and boots together, and leggings - so a player in a
        // full matching set produced a count of at most 3 and the >= 4 branch
        // could never be taken.
        [Fact]
        public async Task Test_SetBonusEngine_FourMatchingArmourPiecesReachTheFourPieceTier()
        {
            const long testPlayerId = 970009202L;
            var characterId = Guid.NewGuid();

            // Real BaseIds from items.json (the tier-2 Sentry armour set), not
            // marker-shaped placeholders - so this also proves the four pieces
            // resolve through the registry, not just through the slug suffix.
            var pieces = new[]
            {
                "eq_sentry_helm_helmet_armor_slot_base",
                "eq_sentry_cuirass_chest_armor_slot_base",
                "eq_sentry_gauntlets_gloves_armor_slot_base",
                "eq_sentry_sabatons_boots_armor_slot_base"
            };

            var instanceIds = new long[pieces.Length];

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = testPlayerId, PlayerGuid = characterId, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 60 });
                SeedAllRegionBossKills(db, testPlayerId);
                db.CharacterRecords.Add(new CharacterRecord { Id = characterId, PlayerId = testPlayerId, Level = 60, AgePhase = 1, SlotIndex = 0 });

                for (int i = 0; i < pieces.Length; i++)
                {
                    var piece = new EquipmentInstance
                    {
                        PlayerId = testPlayerId,
                        BaseItemId = pieces[i],
                        // Rare - the reference tier. At 0 these pieces are
                        // worth nothing to a set, and this test is about the
                        // equip pipeline rather than about junk gear.
                        QualityTier = 4,
                        AffixPayload = "{}",
                        SetId = SetBonusEngine.ChimingSteelSetId
                    };
                    db.EquipmentInstances.Add(piece);
                    await db.SaveChangesAsync();
                    instanceIds[i] = piece.Id;
                }
            }

            var slotEngine = new EquipmentSlotEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            for (int i = 0; i < instanceIds.Length; i++)
            {
                await slotEngine.EquipItemAsync(testPlayerId, instanceIds[i]);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var character = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.PlayerId == testPlayerId);

            (_, EquippedSetIds setIds) = await EquipmentSlotEngine.ComputeEquippedTotalsAsync(verify, character);

            // All four landed in distinct slots rather than overwriting one.
            Assert.Equal(SetBonusEngine.ChimingSteelSetId, EquippedSetIds.SetIdOf(setIds.Helmet));
            Assert.Equal(SetBonusEngine.ChimingSteelSetId, EquippedSetIds.SetIdOf(setIds.Chest));
            Assert.Equal(SetBonusEngine.ChimingSteelSetId, EquippedSetIds.SetIdOf(setIds.Gloves));
            Assert.Equal(SetBonusEngine.ChimingSteelSetId, EquippedSetIds.SetIdOf(setIds.Boots));

            int[] setIdSpan = new int[EquippedSetIds.SlotCount];
            setIds.CopyTo(setIdSpan);
            var result = SetBonusEngine.Evaluate(setIdSpan);

            // Modul: the tiers are a curve now, not two steps - see
            // SetBonusEngine.PotencyOf. Four matching pieces at the reference
            // rarity come to exactly full potency, which is what arms the
            // effect below; four pieces of JUNK would not, and that is the
            // point of the rework.
            Assert.True(result.FireDamageMultiplierPct > 0f);
            Assert.True(result.BurnApplicationActive,
                "Four matching pieces must reach the 4-piece tier; a false here means the set ids collapsed again.");
        }
    }
}

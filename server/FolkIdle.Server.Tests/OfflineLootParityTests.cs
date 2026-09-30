using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Owner rule, 2026-09-30: offline and online give the SAME results per
    /// hour, drops and loot included. Offline is an expected-value model of the
    /// live paths, and every difference is a bug.
    ///
    /// Each test runs the REAL live path (SimulationEngine.ProcessSubTick, the
    /// loot engine's kill loop, CraftingEngine's job completion, the passive
    /// village tick) and the offline model (OfflineSimulationEngine) for the
    /// same activity, and asserts per-stream rates against a band derived from
    /// the sampling error, stated beside each assertion. The offline KILL COUNT
    /// is taken as given - how fast a character kills while away is combat
    /// math, pinned elsewhere; this file is about what each kill, harvest or
    /// craft pays. See docs/architecture/offline_parity.md for the audit.
    ///
    /// In the Postgres collection because the loot and gathering queues are
    /// STATIC (1ef5318): a worker started by another test in parallel would
    /// drain what this file measures. Nothing here starts a worker - the loot
    /// engine's kill loop is invoked directly.
    /// </summary>
    [Collection("Postgres collection")]
    public class OfflineLootParityTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        // How many standard deviations a band allows. At 5 sigma a correct
        // engine fails about once in 1.7 million runs per assertion, and every
        // defect this file was written against is far outside it.
        private const double Sigmas = 5.0;

        public OfflineLootParityTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
            ContentRegistry.Initialize();
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private async Task<long> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"parity_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        // A level-97 fighter that kills an early monster in a tick or two -
        // BossDiamondTests' fixture - with the Plenty bough (so the material
        // quantity bonus is in play) and a codex yield of 2.0, which a live
        // kill's materials do NOT read and the old offline copy multiplied by.
        private static TickStatePayload StrongFighter(long playerId, int monsterId)
        {
            byte mask = ContentRegistry.IsRegionalBoss(monsterId) ? BossFirstClearRules.MarkDefeated(0, monsterId) : (byte)0;
            var payload = new TickStatePayload
            {
                PlayerId = playerId,
                CurrentLevel = 97,
                SelectedLineageId = 1,
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = monsterId,
                CurrentMonsterId = monsterId,
                DefeatedRegionBossMask = mask,
                CurrentMonsterHp = BossFirstClearRules.MaxHpFor(mask, monsterId) * 1000L,
                InventorySpaceRemaining = int.MaxValue,
                PlayerHp = 100_000_000,
                TownHallLevel = 1,
                Skill_Plenty = 3,
                CachedCodexYieldMultiplier = 2.0f,
                CachedCodexDamageMultiplier = 1.0f,
                Food1_ItemId = ContentRegistry.RawFishItemIds.First(),
                Food1_Count = 1_000_000,
            };
            RaceAttributeGrowth.ApplyLevelUpGrowth(ref payload, activeRaceId: 1, levelsGained: 96);
            payload.CachedAffixTotals.FlatAttack = 50_000;
            payload.CachedAffixTotals.FlatDefense = 50_000;
            payload.SetGold(0);
            payload.SetPremiumCurrency(0);
            return payload;
        }

        // The static queues are shared by the whole test run: take this
        // player's items and put everyone else's back.
        private static List<T> Take<T>(ConcurrentQueue<T> queue, Func<T, bool> mine)
        {
            var taken = new List<T>();
            var others = new List<T>();
            while (queue.TryDequeue(out T? item))
            {
                if (mine(item)) taken.Add(item); else others.Add(item);
            }
            foreach (T other in others) queue.Enqueue(other);
            return taken;
        }

        private static readonly MethodInfo ProcessLootMethod = typeof(CombatLootEngine).GetMethod(
            "ProcessMonsterLootDropAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CombatLootEngine.ProcessMonsterLootDropAsync moved");

        // Runs a request through the loot engine's own kill loop, awaited, on a
        // worker that is never started - so nothing drains another test's queue.
        private async Task RollThroughLootEngineAsync(CombatLootDropRequest request, long asPlayerId)
        {
            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var task = (Task)ProcessLootMethod.Invoke(engine, new object[]
            {
                asPlayerId, request.MonsterId, request.LootLuckPct, request.MaterialQuantityPct,
                request.BonusRarityTiers, request.Kills <= 0 ? 1 : request.Kills, request.SkipMaterialRoll,
                request.AutoSalvageBelowTier, request.RarityElevationPct, request.RecordedSource,
            })!;
            await task;
        }

        private async Task<(Dictionary<string, long> Materials, List<int> Tiers)> ReadLootAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var materials = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId != "gold")
                .ToDictionaryAsync(c => c.ItemId, c => c.Quantity);
            var tiers = await db.EquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == playerId)
                .Select(e => (int)e.QualityTier)
                .ToListAsync();
            return (materials, tiers);
        }

        // E[units] and E[units^2] of ONE kill's material roll, from the table
        // and the request's Plenty - the test's own oracle, written out from
        // the rule rather than asked of the code under test.
        private static (double Mean, double Variance) MaterialUnitsPerKill(int monsterId, float materialQuantityPct)
        {
            var table = ContentRegistry.GetLootTable(ContentRegistry.Monsters[monsterId - 1].LootTableId).ToArray();
            double totalWeight = table.Sum(e => (double)e.Weight);
            double eq = 0, eq2 = 0;
            foreach (var entry in table)
            {
                double share = entry.Weight / totalWeight;
                int lo = entry.MaxQuantity > 0 ? Math.Max(1, entry.MinQuantity) : 1;
                int hi = entry.MaxQuantity > 0 ? entry.MaxQuantity : 1;
                for (int q = lo; q <= hi; q++)
                {
                    double units = materialQuantityPct > 0f ? Math.Ceiling(q * (1f + materialQuantityPct / 100f)) : q;
                    double p = share / (hi - lo + 1);
                    eq += p * units;
                    eq2 += p * units * units;
                }
            }
            double c = CombatLootEngine.MaterialDropChance;
            double mean = c * eq;
            return (mean, c * eq2 - mean * mean);
        }

        // ------------------------------------------------------------------
        // Combat: a regular and a boss
        // ------------------------------------------------------------------

        [Fact]
        public Task CombatLoot_ARegular_PaysTheSamePerKill_OnlineAndOffline()
            => AssertCombatLootParityAsync(ContentRegistry.FirstCanonicalMonsterId, liveKills: 3000);

        [Fact]
        public Task CombatLoot_ABoss_PaysTheSamePerKill_OnlineAndOffline()
            => AssertCombatLootParityAsync(RaceUnlockRegistry.GetRegionBossMonsterId(1), liveKills: 300);

        private async Task AssertCombatLootParityAsync(int monsterId, int liveKills)
        {
            Assert.Equal(100, GlobalEngineState.GlobalDropMultiplier);
            Assert.Equal(100, GlobalEngineState.GlobalXpMultiplier);
            bool isBoss = ContentRegistry.IsRegionalBoss(monsterId);

            long livePlayer = await CreatePlayerAsync();
            long offlinePlayer = await CreatePlayerAsync();

            // ---- LIVE: the 10 Hz tick, kill by kill ----------------------
            var live = StrongFighter(livePlayer, monsterId);
            var warQueue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();
            var liveRequests = new List<CombatLootDropRequest>();
            var liveKillEvents = new List<KillEvent>();
            for (int tick = 0; tick < 2_000_000 && liveKillEvents.Count < liveKills; tick++)
            {
                SimulationEngine.ProcessSubTick(ref live, 100, 100, warQueue, contexts);
                if ((tick & 63) == 0 || liveKillEvents.Count + 2 >= liveKills)
                {
                    liveKillEvents.AddRange(Take(CodexEngine.KillEventQueue, k => k.PlayerId == livePlayer));
                    liveRequests.AddRange(Take(CombatLootEngine.DropRequestQueue, r => r.PlayerId == livePlayer));
                }
            }
            liveKillEvents.AddRange(Take(CodexEngine.KillEventQueue, k => k.PlayerId == livePlayer));
            liveRequests.AddRange(Take(CombatLootEngine.DropRequestQueue, r => r.PlayerId == livePlayer));
            Take(CosmeticGrantEngine.Queue, _ => true);
            while (warQueue.TryDequeue(out _)) { }

            int killsLive = liveKillEvents.Count;
            Assert.True(killsLive >= liveKills, $"the live fighter made only {killsLive} kills");
            Assert.Equal(killsLive, liveRequests.Count);
            Assert.All(liveKillEvents, k => Assert.True(k.Kills <= 1));
            Assert.All(liveRequests, r => Assert.True(r.Kills <= 1 && !r.SkipMaterialRoll));

            // ---- OFFLINE: one hour away, same character -------------------
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var offline = StrongFighter(offlinePlayer, monsterId);
            offline.LastLogoutTimestamp = now - 3600;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                offline = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, offline, now);
            }
            var offlineKillEvents = Take(CodexEngine.KillEventQueue, k => k.PlayerId == offlinePlayer);
            var offlineRequests = Take(CombatLootEngine.DropRequestQueue, r => r.PlayerId == offlinePlayer);

            // CODEX: one event carrying the window, and every kill in it. The
            // offline kill count is whatever the combat projection made it.
            var windowEvent = Assert.Single(offlineKillEvents);
            long killsOffline = windowEvent.Kills;
            Assert.True(killsOffline > 100, $"the offline window made only {killsOffline} kills");
            Assert.Equal(monsterId, windowEvent.MonsterId);
            Assert.Equal(killsOffline, offlineRequests.Sum(r => (long)r.Kills));
            // The live kill's race-mastery XP, per kill, times the kills.
            Assert.Equal(liveKillEvents[0].GainedXp * killsOffline, windowEvent.GainedXp);

            // EQUIPMENT ODDS: every request the window sent carries the live
            // request's luck, elevation, Plenty and salvage threshold, so the
            // loot engine rolls the same rarity ladder for it. Materials are
            // rolled at login (SkipMaterialRoll) - measured below.
            var liveOdds = liveRequests[0];
            Assert.Equal(0, liveOdds.AutoSalvageBelowTier);
            Assert.All(offlineRequests, r =>
            {
                Assert.Equal(liveOdds.LootLuckPct, r.LootLuckPct);
                Assert.Equal(liveOdds.RarityElevationPct, r.RarityElevationPct);
                Assert.Equal(liveOdds.MaterialQuantityPct, r.MaterialQuantityPct);
                Assert.Equal(liveOdds.AutoSalvageBelowTier, r.AutoSalvageBelowTier);
                Assert.True(r.SkipMaterialRoll);
                Assert.Equal(DropSource.Offline, r.RecordedSource);
            });

            // Offline materials are already in the chest; read them before the
            // loot engine adds equipment rows for the same player.
            var (offlineMaterials, _) = await ReadLootAsync(offlinePlayer);

            // The live requests are one-kill requests; the loot engine walks
            // its kill loop once per kill either way, so they are rolled as
            // one request of killsLive kills per bonus tier - the same loop,
            // one transaction instead of thousands.
            foreach (var group in liveRequests.GroupBy(r => r.BonusRarityTiers))
            {
                var aggregate = group.First();
                aggregate.Kills = group.Count();
                await RollThroughLootEngineAsync(aggregate, livePlayer);
            }
            foreach (var request in offlineRequests)
            {
                await RollThroughLootEngineAsync(request, offlinePlayer);
            }

            var (liveMaterials, liveTiers) = await ReadLootAsync(livePlayer);
            var (_, offlineTiers) = await ReadLootAsync(offlinePlayer);

            // MATERIALS per kill. Units in n kills are a sum of n iid per-kill
            // amounts (a 35% gate, then a quantity), so the total has mean n*mu
            // and variance n*sigma^2 (MaterialUnitsPerKill). Each side must sit
            // within 5 sigma of n*mu. The old offline path paid 0 (every
            // monster material resolved to "unknown"), and before that
            // 2 x kills x 1 unit at this codex yield - both far outside.
            var (mu, variance) = MaterialUnitsPerKill(monsterId, liveOdds.MaterialQuantityPct);
            long liveUnits = liveMaterials.Values.Sum();
            long offlineUnits = offlineMaterials.Values.Sum();
            AssertWithinBand("live materials", liveUnits, killsLive * mu, Math.Sqrt(killsLive * variance));
            AssertWithinBand("offline materials", offlineUnits, killsOffline * mu, Math.Sqrt(killsOffline * variance));
            Assert.Equal(liveMaterials.Keys.OrderBy(k => k), offlineMaterials.Keys.OrderBy(k => k));

            // EQUIPMENT per kill: Binomial(n, 15%) pieces, plus exactly one per
            // kill for a regional boss (the guarantee).
            double p = CombatLootEngine.EquipmentDropChance;
            double guaranteed = isBoss ? 1.0 : 0.0;
            AssertWithinBand("live equipment", liveTiers.Count, killsLive * (p + guaranteed), Math.Sqrt(killsLive * p * (1 - p)));
            AssertWithinBand("offline equipment", offlineTiers.Count, killsOffline * (p + guaranteed), Math.Sqrt(killsOffline * p * (1 - p)));

            // RARITY: the mean tier of the two samples, Welch's difference of
            // means - sd of the difference = sqrt(s1^2/n1 + s2^2/n2).
            double meanLive = liveTiers.Average(), meanOffline = offlineTiers.Average();
            double varLive = liveTiers.Sum(t => (t - meanLive) * (t - meanLive)) / Math.Max(1, liveTiers.Count - 1);
            double varOffline = offlineTiers.Sum(t => (t - meanOffline) * (t - meanOffline)) / Math.Max(1, offlineTiers.Count - 1);
            double sdDiff = Math.Sqrt(varLive / liveTiers.Count + varOffline / offlineTiers.Count);
            _output.WriteLine($"kills live {killsLive}, offline {killsOffline}; mean tier live {meanLive:F3} ({liveTiers.Count} pieces), offline {meanOffline:F3} ({offlineTiers.Count})");
            Assert.True(Math.Abs(meanLive - meanOffline) <= Sigmas * sdDiff + 1e-9,
                $"mean rarity tier live {meanLive:F3} vs offline {meanOffline:F3}, allowed {Sigmas * sdDiff:F3}");

            // DIAMONDS: a boss pays none either way. (The ordinary-kill rate is
            // 0.01%, too rare to measure over these kills - see
            // OrdinaryKillDiamonds_OfflineDrawsTheLiveRate.)
            if (isBoss)
            {
                Assert.Equal(0, live.PremiumCurrency);
                Assert.Equal(0, offline.PremiumCurrency);
            }
        }

        private void AssertWithinBand(string what, double observed, double expected, double sigma)
        {
            double allowed = Sigmas * Math.Max(sigma, 1.0);
            _output.WriteLine($"{what}: {observed} (expected {expected:F1}, allowed +/- {allowed:F1})");
            Assert.True(Math.Abs(observed - expected) <= allowed,
                $"{what}: {observed} against an expected {expected:F1} (allowed +/- {allowed:F1})");
        }

        [Fact]
        public void OrdinaryKillDiamonds_OfflineDrawsTheLiveRate()
        {
            // Two million kills each way. The count is Binomial(2e6, 1e-4):
            // mean 200, sd sqrt(200 x 0.9999) = 14.1, so 5 sigma is +/- 71 -
            // and the old offline path paid 0, 14 sigma out.
            const int kills = 2_000_000;
            double p = SimulationEngine.OrdinaryKillDiamondChance;
            double sigma = Math.Sqrt(kills * p * (1 - p));

            var rng = new Random(20260930);
            int liveDiamonds = 0;
            for (int i = 0; i < kills; i++)
            {
                if (SimulationEngine.OrdinaryKillPaysDiamond(rng.NextDouble())) liveDiamonds++;
            }

            // Offline: a hundred windows of 20,000 kills through the projection
            // itself.
            int regular = ContentRegistry.FirstCanonicalMonsterId;
            var payload = new TickStatePayload { PlayerId = -9_300_001L };
            payload.SetPremiumCurrency(0);
            var stats = StatsCalculator.Calculate(10, 10, 10, 10, 0, 0, 1, 0, 0, 0, 0, 0);
            for (int window = 0; window < 100; window++)
            {
                OfflineSimulationEngine.ProjectCombatLoot(ref payload, in stats, regular, kills / 100, rng);
            }
            Take(CodexEngine.KillEventQueue, k => k.PlayerId == payload.PlayerId);
            Take(CombatLootEngine.DropRequestQueue, r => r.PlayerId == payload.PlayerId);

            AssertWithinBand("live diamonds", liveDiamonds, kills * p, sigma);
            AssertWithinBand("offline diamonds", payload.PremiumCurrency, kills * p, sigma);

            // A regional boss pays none, offline as live (BossDiamondTests).
            var bossPayload = new TickStatePayload { PlayerId = -9_300_002L };
            bossPayload.SetPremiumCurrency(0);
            OfflineSimulationEngine.ProjectCombatLoot(ref bossPayload, in stats, RaceUnlockRegistry.GetRegionBossMonsterId(1), 200_000, rng);
            Take(CodexEngine.KillEventQueue, k => k.PlayerId == bossPayload.PlayerId);
            Take(CombatLootEngine.DropRequestQueue, r => r.PlayerId == bossPayload.PlayerId);
            Assert.Equal(0, bossPayload.PremiumCurrency);
        }

        // ------------------------------------------------------------------
        // Gathering: codex yield above 1, a monolith, luck, and rising mastery
        // ------------------------------------------------------------------

        [Fact]
        public void Gathering_ANodeWithCodexYieldAndMastery_YieldsTheSamePerHour_OnlineAndOffline()
        {
            Assert.Equal(100, GlobalEngineState.GlobalDropMultiplier);
            const long seconds = 4 * 3600L;
            Assert.True(ContentRegistry.TryGetGatheringNode(ActivityIdBands.WoodcuttingBand + 1, out GatheringNodeDefinition node));

            TickStatePayload Build(long playerId) => new TickStatePayload
            {
                PlayerId = playerId,
                ActiveActivityId = node.ActivityId,
                WoodcuttingMasteryLevel = 3,
                CachedCodexYieldMultiplier = 1.7f,
                // +30 roll-percent the old offline copy did not know about.
                CachedWoodcuttingMonolithLevel = 30,
                STR = 50, DEX = 50, CON = 50, LCK = 400,
                InventorySpaceRemaining = int.MaxValue,
            };

            // LIVE: the tick itself, for four hours of ticks.
            var live = Build(-9_300_101L);
            var yieldAtStart = SimulationEngine.GatheringYieldFor(ref live, in node, 100);
            Assert.True(yieldAtStart.MultiplierPct > 200, "the fixture has no codex/monolith yield to test");
            Assert.True(yieldAtStart.LuckWeightBonus > 0, "the fixture has no luck to test");
            var warQueue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();
            var grants = new List<GatheredMaterialGrant>();
            for (long tick = 0; tick < seconds * 10L; tick++)
            {
                SimulationEngine.ProcessSubTick(ref live, 100, 100, warQueue, contexts);
                if ((tick & 1023) == 0) grants.AddRange(Take(CombatLootEngine.GatheringGrantQueue, g => g.PlayerId == live.PlayerId));
            }
            grants.AddRange(Take(CombatLootEngine.GatheringGrantQueue, g => g.PlayerId == live.PlayerId));
            var liveByItem = grants
                .GroupBy(g => ContentRegistry.GetItemBaseId(g.ItemId))
                .ToDictionary(g => g.Key, g => g.Sum(x => (long)x.Quantity));

            // OFFLINE: the projection the login runs, for the same four hours.
            var offline = Build(-9_300_102L);
            var projection = OfflineSimulationEngine.CalculateGatheringProjection(ref offline, node, seconds, new Random(20260930));

            // SPEED AND MASTERY are deterministic on both sides: the same
            // harvests (to within the one partial harvest at the end of each
            // mastery level) and the same mastery reached. Mastery DOES rise
            // over four hours here, so a projection frozen at the logout level
            // would fall short.
            _output.WriteLine($"harvests live {live.HarvestLoopCount}, offline {projection.Actions}; mastery live {live.WoodcuttingMasteryLevel}, offline {offline.WoodcuttingMasteryLevel}; roll-percent {yieldAtStart.MultiplierPct}, luck weight {yieldAtStart.LuckWeightBonus}");
            Assert.True(live.WoodcuttingMasteryLevel > 3, "mastery never rose - the fixture does not test the speed-up");
            Assert.Equal(live.WoodcuttingMasteryLevel, offline.WoodcuttingMasteryLevel);
            long levelSteps = live.WoodcuttingMasteryLevel - 3 + 1;
            Assert.InRange(projection.Actions, (long)live.HarvestLoopCount - levelSteps, (long)live.HarvestLoopCount + levelSteps);

            // YIELD per item. With H harvests at m roll-percent, R rolls are
            // H x floor(m/100) + Binomial(H, frac); an entry's count is
            // Binomial(R, w_i / W) with luck in the weights; every entry of this
            // table grants one unit. Mean E_i = H x m/100 x share_i; variance
            // is at most E_i (the multinomial cell) + E_i x share_i (the roll
            // count's own spread) <= 2 E_i. Each side within 5 sigma of E_i.
            var table = ContentRegistry.GetLootTable(node.ActivityId).ToArray();
            Assert.All(table, e => Assert.True(e.MaxQuantity <= e.MinQuantity, "the band below assumes single-unit entries"));
            double weight = table.Sum(e => (double)(e.Weight + yieldAtStart.LuckWeightBonus));
            foreach (var entry in table)
            {
                string baseId = ContentRegistry.GetItemBaseId(entry.ItemId);
                double expectedLive = live.HarvestLoopCount * yieldAtStart.MultiplierPct / 100.0 * (entry.Weight + yieldAtStart.LuckWeightBonus) / weight;
                double expectedOffline = projection.Actions * yieldAtStart.MultiplierPct / 100.0 * (entry.Weight + yieldAtStart.LuckWeightBonus) / weight;
                AssertWithinBand($"live {baseId}", liveByItem.GetValueOrDefault(baseId), expectedLive, Math.Sqrt(2 * expectedLive));
                AssertWithinBand($"offline {baseId}", projection.MaterialDeltas.GetValueOrDefault(baseId), expectedOffline, Math.Sqrt(2 * expectedOffline));
            }
            Assert.Equal(liveByItem.Keys.OrderBy(k => k), projection.MaterialDeltas.Keys.OrderBy(k => k));
        }

        // ------------------------------------------------------------------
        // Crafting: the job, not monster 1
        // ------------------------------------------------------------------

        [Fact]
        public async Task Crafting_AJobLeftRunning_CraftsWhatTheLiveJobCrafts_AndFightsNothing()
        {
            Assert.True(ContentRegistry.TryGetRecipeByActivityId(ActivityIdBands.CraftingBand, out var recipe));
            string mat1 = ContentRegistry.GetItemBaseId(recipe.Mat1Id);
            string mat2 = ContentRegistry.GetItemBaseId(recipe.Mat2Id);
            string result = ContentRegistry.GetItemBaseId(recipe.ResultItemId);
            const int affordable = 3;

            // Ten minutes of job; materials for exactly three crafts.
            const long seconds = 600L;
            long attempts = seconds * 10L / SimulationEngine.CraftTicksFor(in recipe);
            Assert.True(attempts > affordable * 10, "the window is not long enough to run the materials out");

            async Task<long> SeedAsync()
            {
                long id = await CreatePlayerAsync();
                await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = id, ItemId = mat1, Quantity = recipe.Mat1Count * affordable + recipe.Mat1Count / 2 });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = id, ItemId = mat2, Quantity = recipe.Mat2Count * affordable });
                await db.SaveChangesAsync();
                return id;
            }

            // LIVE: every completion the job's tick would enqueue, handed to
            // CraftingEngine exactly as CraftingTickCoordinator hands it.
            long livePlayer = await SeedAsync();
            var crafting = new CraftingEngine(_fixture.DbContextFactory, _fixture.PlayerRegistry, _fixture.RetryingOptions);
            for (long i = 0; i < attempts; i++)
            {
                await crafting.ExecuteCraftingAsync(livePlayer, recipe.ResultItemId);
            }
            while (_fixture.PlayerRegistry.CraftingCompletionQueue.TryDequeue(out _)) { }

            // OFFLINE: the same job, away for the same ten minutes.
            long offlinePlayer = await SeedAsync();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new TickStatePayload
            {
                PlayerId = offlinePlayer,
                LastLogoutTimestamp = now - seconds,
                ActiveActivityId = ActivityIdBands.CraftingBand,
                CurrentLevel = 1,
                InventorySpaceRemaining = 1000,
            };
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                payload = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, payload, now);
            }

            // It fought nothing: no kills went to the codex or the loot engine,
            // and no combat XP or gold came back (monster 1, the old fallback).
            Assert.Empty(Take(CodexEngine.KillEventQueue, k => k.PlayerId == offlinePlayer));
            Assert.Empty(Take(CombatLootEngine.DropRequestQueue, r => r.PlayerId == offlinePlayer));
            Assert.Equal(0L, payload.OfflineXpEarned);
            Assert.Equal(0L, payload.OfflineGoldEarned);
            Assert.Equal((long)affordable, payload.LifetimeItemsCrafted);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            foreach (long player in new[] { livePlayer, offlinePlayer })
            {
                int crafted = await verify.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == player && e.BaseItemId == result);
                long left1 = await verify.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == player && c.ItemId == mat1).SumAsync(c => c.Quantity);
                long left2 = await verify.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == player && c.ItemId == mat2).SumAsync(c => c.Quantity);
                long counted = await verify.PlayerRecords.AsNoTracking().Where(p => p.Id == player).Select(p => p.TotalItemsCrafted).SingleAsync();

                Assert.Equal(affordable, crafted);
                Assert.Equal((long)(recipe.Mat1Count / 2), left1);
                Assert.Equal(0L, left2);
                Assert.Equal((long)affordable, counted);
            }
        }

        // ------------------------------------------------------------------
        // Village production
        // ------------------------------------------------------------------

        // Modul: GOLD ONLY, deliberately. The Town Hall's gold is one rate on
        // both paths and is asserted here to the coin. The Lumberjack and Mine
        // are NOT: the live tick produces the legacy "wood" / "iron_ore" rows at
        // 0.1 and 0.05 a second per level, and the offline window produces the
        // region's catalogued logs and ores (plus a 10% rare share) at
        // (level + 1) x 100 an hour. Which of the two is the game is a design
        // decision, recorded in docs/architecture/offline_parity.md, and a test
        // that pinned either would be pinning a guess.
        [Fact]
        public async Task VillageProduction_TownHallGold_IsTheSamePerHour_OnlineAndOffline()
        {
            const int townHallLevel = 3;
            const long seconds = 3600L;

            var live = new TickStatePayload { PlayerId = -9_300_201L, TownHallLevel = townHallLevel };
            live.SetGold(0);
            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (long tick = 0; tick < seconds * 10L; tick++)
            {
                SimulationEngine.ProcessPassiveVillageTick(ref live, 0.1, nowEpoch);
            }

            long offlinePlayer = await CreatePlayerAsync();
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await OfflineSimulationEngine.GrantVillagePassiveProductionAsync(
                    db, offlinePlayer, lumberjackLevel: 0, mineLevel: 0, warehouseLevel: 0, townHallLevel: townHallLevel, elapsedSeconds: seconds);
            }
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            long offlineGold = await verify.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == offlinePlayer && c.ItemId == "gold").SumAsync(c => c.Quantity);

            long perHour = VillageManagementEngine.GetTownHallGoldRatePerHour(townHallLevel);
            Assert.Equal(perHour, live.CurrentGold);
            Assert.Equal(perHour, offlineGold);
        }
    }
}

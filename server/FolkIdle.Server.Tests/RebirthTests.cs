using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 88: rebirth on demand. The season rollover, for one player.
    ///
    /// On the SHARED collection on purpose, unlike the global rollover tests,
    /// which need their own container because they truncate whole tables. A
    /// rebirth must touch exactly one player, and a neighbour seeded beside
    /// the reborn player - with every table filled in - is how that is proved
    /// rather than assumed.
    /// </summary>
    [Collection("Postgres collection")]
    public class RebirthTests
    {
        private readonly PostgresTestFixture _fixture;

        public RebirthTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private sealed record Seeded(long PlayerId, Guid Main, Guid Kept, List<Guid> Benched);

        /// <summary>
        /// A player deep into a run: every table the rollover resets and every
        /// table it must leave standing has a row.
        /// </summary>
        private async Task<Seeded> SeedAsync(long playerId, int level, int hallMembers = 12)
        {
            var main = Guid.NewGuid();
            var kept = Guid.NewGuid();
            var benched = new List<Guid>();

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var sword = new EquipmentInstance { PlayerId = playerId, BaseItemId = "test_sword", QualityTier = 3, AffixPayload = "{}" };
            var axe = new EquipmentInstance { PlayerId = playerId, BaseItemId = "test_axe", QualityTier = 2, AffixPayload = "{}" };
            db.EquipmentInstances.AddRange(sword, axe);
            await db.SaveChangesAsync();

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = main,
                AuthenticatorToken = Guid.NewGuid(),
                CurrentLevel = level,
                CurrentXp = 987_654,
                BaseStrength = 300,
                BaseDexterity = 120,
                BaseConstitution = 90,
                BaseLuck = 40,
                UnspentAttributePoints = 13,
                AvailableSkillPoints = 7,
                SealsEarnedMask = 0b101, // two Seals
                PremiumDiamonds = 4_321,
                LegacyPerks = LegacyPerkResolver.SetPerkRank(0, LegacyPerkResolver.XpMultiplierBitOffset, 9),
                AncestorSlotsPurchased = 0,
                LastVillagerArrivalEpoch = 1_700_000_000L,
                VillagerRecruitmentsThisSeason = 3,
                ActiveOffensivePotionId = 5,
                OffensivePotionDurationMs = 60_000,
                FreeRespecUsed = true,
                PaidRespecGrants = 2,
                WoodcuttingMasteryLevel = 17,
                LogicEpochCounter = 40,
            });

            // The Hall: the main character wearing the sword and the axe (a
            // tool slot - the list that used to stop at eight), one member
            // marked Keep, and the rest ordinary so the cull has work to do.
            db.CharacterRecords.Add(new CharacterRecord
            {
                Id = main, PlayerId = playerId, Name = "Founder", SlotIndex = 0, AgePhase = 2, AgeTicks = 9_999_999,
                ActiveActivityId = 23, EquippedWeaponId = sword.Id, EquippedAxeId = axe.Id,
            });
            var mainLineage = new CharacterLineageRegistry { CharacterId = main, GenerationIndex = 1 };
            mainLineage.SetAptitudeVector(new[] { 5, 5, 5, 5 });
            db.CharacterLineages.Add(mainLineage);

            db.CharacterRecords.Add(new CharacterRecord { Id = kept, PlayerId = playerId, Name = "Keeper", SlotIndex = 1 });
            var keptLineage = new CharacterLineageRegistry { CharacterId = kept, GenerationIndex = 4, IsKeptAtRollover = true, IsEpicMutation = true };
            // The WEAKEST blood in the Hall: only the mark can save it.
            keptLineage.SetAptitudeVector(new[] { 1, 1, 1, 1 });
            db.CharacterLineages.Add(keptLineage);

            for (int i = 2; i < hallMembers; i++)
            {
                var id = Guid.NewGuid();
                benched.Add(id);
                db.CharacterRecords.Add(new CharacterRecord { Id = id, PlayerId = playerId, Name = $"Child {i}", SlotIndex = i });
                var lineage = new CharacterLineageRegistry { CharacterId = id, GenerationIndex = 2 };
                lineage.SetAptitudeVector(new[] { 2 + i, 3, 3, 3 });
                db.CharacterLineages.Add(lineage);
            }

            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000L });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "iron_ore", Quantity = 900L });
            db.MarketEquipmentInstances.Add(new MarketEquipmentInstance { PlayerId = playerId, BaseItemId = "test_listing", QualityTier = 4 });
            db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = playerId, BranchId = 0, Level = 6 });

            var newcomer = new VillageNewcomer { PlayerId = playerId, RaceId = RaceIds.Human, IsFemale = true, ArrivedAtEpoch = 1_700_000_000L };
            newcomer.SetAptitudeVector(new[] { 9, 9, 9, 9 });
            db.VillageNewcomers.Add(newcomer);
            db.PlayerChroniclePasses.Add(new PlayerChroniclePass { PlayerId = playerId, PassLevel = 12, AccumulatedXp = 3_000 });

            // What carries.
            db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = playerId, BuildingId = 9, CurrentLevel = 7 });
            db.PlayerRaceMasteries.Add(new PlayerRaceMastery { PlayerId = playerId, RaceId = 1, MasteryLevel = 5, CumulativeXp = 40_000L });
            db.PlayerInheritanceStats.Add(new PlayerInheritanceStat { PlayerId = playerId, StatId = InheritanceRegistry.StatDamage, Level = 6 });

            await db.SaveChangesAsync();
            return new Seeded(playerId, main, kept, benched);
        }

        private async Task<int> ActiveEraAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var era = await db.SeasonalEraRecords.Where(e => e.IsActive).OrderBy(e => e.EndTimestamp).FirstOrDefaultAsync();
            if (era != null) return era.EraId;
            era = new SeasonalEraRecord { IsActive = true, IsRolloverPaused = true, EndTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 };
            db.SeasonalEraRecords.Add(era);
            await db.SaveChangesAsync();
            return era.EraId;
        }

        [Fact]
        public async Task ARebirthKeepsExactlyTheCarryList_ResetsTheRest_AndTouchesNobodyElse()
        {
            var me = await SeedAsync(950_088_001L, level: 72);
            var neighbour = await SeedAsync(950_088_002L, level: 72, hallMembers: 11);
            int eraId = await ActiveEraAsync();

            var engine = new RebirthEngine(_fixture.ServiceProvider);
            var preview = await engine.PreviewAsync(me.PlayerId);
            Assert.NotNull(preview);
            Assert.True(preview!.Renowned);
            Assert.Equal(0, preview.RebirthCount);
            Assert.Equal(RebirthRules.DamageBonusPct(1), preview.DamageBonusPctAfter);
            Assert.Equal(2, preview.HallLetGo.Count); // 12 members, cap 10
            Assert.Equal(1_000_000L, preview.Gold);
            Assert.Equal(2, preview.EquipmentPieces);
            Assert.Equal(6, preview.SkillTreeLevels);
            Assert.Equal(300 + 120 + 90 + 40 + 13, preview.AttributePoints);

            var outcome = await engine.RebirthAsync(me.PlayerId, expectedRebirthCount: 0);

            Assert.Equal(RebirthResult.Ok, outcome.Result);
            Assert.Equal(1, outcome.RebirthCount);
            Assert.Equal(1, outcome.RenownedRebirths);
            Assert.True(outcome.Renowned);
            Assert.Equal(RebirthRules.DamageBonusPct(1), outcome.DamageBonusPct);
            // What the preview promised is what was paid.
            Assert.Equal(preview.ShardsEarned, outcome.ShardsEarned);
            Assert.True(outcome.ShardsEarned > 0);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == me.PlayerId);

            // RESETS.
            Assert.Equal(1, player.CurrentLevel);
            Assert.Equal(0L, player.CurrentXp);
            Assert.Equal(AttributeRegistry.StartingValue(AttributeRegistry.Might), player.BaseStrength);
            Assert.Equal(AttributeRegistry.StartingValue(AttributeRegistry.Finesse), player.BaseDexterity);
            Assert.Equal(AttributeRegistry.StartingValue(AttributeRegistry.Vigour), player.BaseConstitution);
            Assert.Equal(AttributeRegistry.StartingValue(AttributeRegistry.Fortune), player.BaseLuck);
            Assert.Equal(0, player.UnspentAttributePoints);
            Assert.Equal(0, player.ActiveOffensivePotionId);
            Assert.False(player.FreeRespecUsed);
            Assert.Equal(0L, player.LastVillagerArrivalEpoch);
            Assert.Equal(0, player.VillagerRecruitmentsThisSeason);
            Assert.Equal(0L, (await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == me.PlayerId && c.ItemId == "gold")).Quantity);
            Assert.False(await verify.CommodityRecords.AnyAsync(c => c.PlayerId == me.PlayerId && c.ItemId != "gold"));
            Assert.False(await verify.EquipmentInstances.AnyAsync(e => e.PlayerId == me.PlayerId));
            Assert.False(await verify.MarketEquipmentInstances.AnyAsync(e => e.PlayerId == me.PlayerId));
            Assert.False(await verify.PlayerSkillTreeNodes.AnyAsync(n => n.PlayerId == me.PlayerId));
            Assert.False(await verify.VillageNewcomers.AnyAsync(v => v.PlayerId == me.PlayerId));
            Assert.Equal(0, (await verify.PlayerChroniclePasses.AsNoTracking().SingleAsync(c => c.PlayerId == me.PlayerId)).PassLevel);

            var founder = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == me.Main);
            Assert.Null(founder.EquippedWeaponId);
            Assert.Null(founder.EquippedAxeId); // slot 8 - the list that used to stop at eight
            Assert.Equal(0L, founder.ActiveActivityId);
            Assert.Equal(AgePhaseCurve.Adult, founder.AgePhase);
            Assert.Equal(AgePhaseCurve.ChildEndTicks, founder.AgeTicks);

            // The Hall is culled to its cap: the founder and the marked member
            // stay, and the marked one is the weakest blood in the Hall.
            var hall = await verify.CharacterRecords.AsNoTracking().Where(c => c.PlayerId == me.PlayerId).ToListAsync();
            Assert.Equal(HallOfAncestorsRules.CapFor(0), hall.Count);
            Assert.Contains(hall, c => c.Id == me.Kept);
            Assert.Equal(0, hall.Single(c => c.Id == me.Main).SlotIndex);
            Assert.Equal(preview.HallLetGo.OrderBy(n => n),
                (await SeededNamesAsync(me)).Except(hall.Select(c => c.Name)).OrderBy(n => n));

            // CARRIES.
            Assert.Equal(2 * DeedRegistry.SkillPointsPerSeal, player.AvailableSkillPoints);
            Assert.Equal(0b101, player.SealsEarnedMask);
            Assert.Equal(4_321, player.PremiumDiamonds);
            Assert.Equal(9, LegacyPerkResolver.GetXpBonusPct(player.LegacyPerks));
            Assert.Equal(2, player.PaidRespecGrants);
            Assert.Equal(17, player.WoodcuttingMasteryLevel);
            Assert.Equal(7, (await verify.VillageInfrastructures.AsNoTracking().SingleAsync(v => v.PlayerId == me.PlayerId)).CurrentLevel);
            Assert.Equal(5, (await verify.PlayerRaceMasteries.AsNoTracking().SingleAsync(m => m.PlayerId == me.PlayerId)).MasteryLevel);
            Assert.Equal(6, (await verify.PlayerInheritanceStats.AsNoTracking().SingleAsync(s => s.PlayerId == me.PlayerId)).Level);
            var keptLineage = await verify.CharacterLineages.AsNoTracking().SingleAsync(l => l.CharacterId == me.Kept);
            Assert.True(keptLineage.IsEpicMutation);
            Assert.Equal(4, keptLineage.GenerationIndex);
            Assert.Equal(new[] { 1, 1, 1, 1 }, keptLineage.AptitudeVector());
            var ledger = await verify.PlayerLegacyLedgers.AsNoTracking().SingleAsync(l => l.PlayerId == me.PlayerId && l.EraId == eraId);
            Assert.Equal(outcome.ShardsEarned, ledger.LegacyShardBalance);

            // THE COUNTERS, and the epoch fence past every stale snapshot.
            Assert.Equal(1, player.RebirthCount);
            Assert.Equal(1, player.RenownedRebirths);
            Assert.Equal(40 + RebirthRules.EpochFence, player.LogicEpochCounter);

            // AND NOBODY ELSE. The neighbour keeps every row it had.
            var other = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == neighbour.PlayerId);
            Assert.Equal(72, other.CurrentLevel);
            Assert.Equal(300, other.BaseStrength);
            Assert.Equal(0, other.RebirthCount);
            Assert.Equal(1_000_000L, (await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == neighbour.PlayerId && c.ItemId == "gold")).Quantity);
            Assert.Equal(2, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == neighbour.PlayerId));
            Assert.Equal(1, await verify.MarketEquipmentInstances.CountAsync(e => e.PlayerId == neighbour.PlayerId));
            Assert.Equal(11, await verify.CharacterRecords.CountAsync(c => c.PlayerId == neighbour.PlayerId));
            Assert.Equal(1, await verify.PlayerSkillTreeNodes.CountAsync(n => n.PlayerId == neighbour.PlayerId));
            Assert.Equal(1, await verify.VillageNewcomers.CountAsync(v => v.PlayerId == neighbour.PlayerId));
            Assert.Equal(12, (await verify.PlayerChroniclePasses.AsNoTracking().SingleAsync(c => c.PlayerId == neighbour.PlayerId)).PassLevel);
            var neighbourFounder = await verify.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == neighbour.Main);
            Assert.NotNull(neighbourFounder.EquippedWeaponId);
            Assert.Equal(23L, neighbourFounder.ActiveActivityId);
        }

        private async Task<List<string>> SeededNamesAsync(Seeded s)
        {
            var names = new List<string> { "Founder", "Keeper" };
            for (int i = 2; i < 2 + s.Benched.Count; i++) names.Add($"Child {i}");
            await Task.CompletedTask;
            return names;
        }

        [Fact]
        public async Task ADoubleSubmitRebirthsOnce_AndTheBonusIsPaidOnce()
        {
            var me = await SeedAsync(950_088_011L, level: 60, hallMembers: 3);
            await ActiveEraAsync();
            var engine = new RebirthEngine(_fixture.ServiceProvider);

            // Two submits of the same preview at the same moment - a double
            // tap that got past the client. The row lock serialises them and
            // the count token refuses the second.
            var results = await Task.WhenAll(
                engine.RebirthAsync(me.PlayerId, 0),
                engine.RebirthAsync(me.PlayerId, 0));
            Assert.Equal(1, results.Count(r => r.Result == RebirthResult.Ok));
            Assert.Equal(1, results.Count(r => r.Result == RebirthResult.AlreadyReborn));

            // Something earned after the rebirth must survive a late retry.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var gold = await db.CommodityRecords.SingleAsync(c => c.PlayerId == me.PlayerId && c.ItemId == "gold");
                gold.Quantity = 555;
                var p = await db.PlayerRecords.SingleAsync(x => x.Id == me.PlayerId);
                p.CurrentLevel = 8;
                await db.SaveChangesAsync();
            }

            var late = await engine.RebirthAsync(me.PlayerId, 0);
            Assert.Equal(RebirthResult.AlreadyReborn, late.Result);
            Assert.Equal(1, late.RebirthCount);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(x => x.Id == me.PlayerId);
            Assert.Equal(1, player.RebirthCount);
            Assert.Equal(1, player.RenownedRebirths);
            Assert.Equal(8, player.CurrentLevel);
            Assert.Equal(555L, (await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == me.PlayerId && c.ItemId == "gold")).Quantity);
        }

        [Fact]
        public async Task ARebirthBelowTheRenownLevelIsAllowed_ButPaysNoRenown()
        {
            var me = await SeedAsync(950_088_021L, level: RebirthRules.RenownLevel - 1, hallMembers: 2);
            await ActiveEraAsync();
            var engine = new RebirthEngine(_fixture.ServiceProvider);

            var preview = await engine.PreviewAsync(me.PlayerId);
            Assert.False(preview!.Renowned);
            Assert.Equal(0, preview.DamageBonusPctAfter);

            var outcome = await engine.RebirthAsync(me.PlayerId, 0);
            Assert.Equal(RebirthResult.Ok, outcome.Result);
            Assert.False(outcome.Renowned);
            Assert.Equal(1, outcome.RebirthCount);
            Assert.Equal(0, outcome.RenownedRebirths);
            Assert.Equal(0, outcome.DamageBonusPct);
        }

        [Fact]
        public async Task AnOnlineRebirthFlushesFirst_ReloadsTheResetPayload_AndRefusesTheOldLife()
        {
            var me = await SeedAsync(950_088_031L, level: 66, hallMembers: 2);
            await ActiveEraAsync();

            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);
            var registry = new PlayerSessionRegistry();

            // The player is online and has earned something the database has
            // not seen yet: a Seal (+2 skill points forever) and diamonds.
            var live = await checkpoints.LoadPlayerState(me.PlayerId);
            Assert.Equal(66, live.CurrentLevel);
            live.PremiumCurrency += 100;
            live.ActiveActivityId = 23;
            live.IsDirty = true;
            var oldLife = live;

            var activePlayers = new Dictionary<long, TickStatePayload> { [me.PlayerId] = live };
            var request = new RebirthRequest
            {
                PlayerId = me.PlayerId,
                ExpectedRebirthCount = 0,
                Engine = new RebirthEngine(_fixture.ServiceProvider),
            };
            registry.RebirthRequestQueue.Enqueue(request);

            RebirthTickCoordinator.Drain(registry, activePlayers, checkpoints);
            Assert.True(activePlayers[me.PlayerId].IsSuspended);
            Assert.True(activePlayers[me.PlayerId].RebirthPending);

            var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(outcome);
            Assert.Equal(RebirthResult.Ok, outcome!.Value.Result);

            // The flush ran BEFORE the reset: the diamonds the live session
            // held reached the row and survived the rebirth.
            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var row = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == me.PlayerId);
                Assert.Equal(4_321 + 100, row.PremiumDiamonds);
                Assert.Equal(1, row.CurrentLevel);
            }

            // The reborn payload lands through the reload drain, and the old
            // life's fight does not come with it.
            Assert.True(registry.StateReloadQueue.TryDequeue(out var reloaded));
            var livePayload = activePlayers[me.PlayerId];
            StateReloadMerge.CarryLiveOnlyFields(in livePayload, ref reloaded);
            Assert.Equal(1, reloaded.CurrentLevel);
            Assert.False(reloaded.IsSuspended);
            Assert.False(reloaded.RebirthPending);
            Assert.Equal(1, reloaded.RenownedRebirths);
            Assert.Equal(0L, reloaded.ActiveActivityId);
            Assert.Equal(0L, reloaded.CurrentGold);

            // A stale snapshot of the old life (a flush queued behind the
            // rebirth) is refused by the epoch fence, not written over it.
            oldLife.LogicEpochCounter += 1;
            Assert.False(await checkpoints.FlushState(oldLife));
            await using (var verify = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(1, (await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == me.PlayerId)).CurrentLevel);
            }
            await checkpoints.LastSplitBrainCompensation;
            checkpoints.DrainWriter(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ARebirthPurgesTheRedisFrameThatWouldWriteTheOldLevelBack()
        {
            var me = await SeedAsync(950_088_041L, level: 55, hallMembers: 1);
            await ActiveEraAsync();
            var redis = _fixture.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
            await redis.HashSetAsync(RedisSessionCache.SessionStateKey(me.PlayerId), new[] { new HashEntry("current_level", 55) });
            await redis.HashSetAsync(RedisSessionCache.GoldBufferKey(me.PlayerId), new[] { new HashEntry("delta", 9_999) });

            var outcome = await new RebirthEngine(_fixture.ServiceProvider).RebirthAsync(me.PlayerId, 0);
            Assert.Equal(RebirthResult.Ok, outcome.Result);

            Assert.False(await redis.KeyExistsAsync(RedisSessionCache.SessionStateKey(me.PlayerId)));
            Assert.False(await redis.KeyExistsAsync(RedisSessionCache.GoldBufferKey(me.PlayerId)));
        }

        [Fact]
        public void RenownIsACurveUnderItsCeiling()
        {
            Assert.Equal(0, RebirthRules.DamageBonusPct(0));
            Assert.Equal(3, RebirthRules.DamageBonusPct(1));
            Assert.Equal(5, RebirthRules.DamageBonusPct(2));
            int previous = 0;
            for (int n = 1; n <= 10_000; n++)
            {
                int pct = RebirthRules.DamageBonusPct(n);
                Assert.True(pct >= previous, $"renown fell at {n}");
                Assert.True(pct < RebirthRules.MaxDamageBonusPct, $"renown reached its ceiling at {n}");
                previous = pct;
            }
            Assert.False(RebirthRules.IsRenowned(RebirthRules.RenownLevel - 1));
            Assert.True(RebirthRules.IsRenowned(RebirthRules.RenownLevel));
        }

        [Fact]
        public void TheReloadAfterARebirthCarriesNoGoldAndNoFight()
        {
            var live = new TickStatePayload
            {
                RebirthPending = true,
                FlushesInFlight = 1,
                LogicEpochCounter = 90,
                RedisPendingGoldDelta = 500,
                ActiveActivityId = 23,
                CurrentMonsterId = 7,
            };
            var reloaded = new TickStatePayload { LogicEpochCounter = 44 };
            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(1, reloaded.FlushesInFlight);
            Assert.Equal(90, reloaded.LogicEpochCounter);
            Assert.Equal(0L, reloaded.RedisPendingGoldDelta);
            Assert.Equal(0L, reloaded.ActiveActivityId);
            Assert.Equal(0, reloaded.CurrentMonsterId);
        }
    }
}

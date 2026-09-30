using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    /// Task 83: Workshop commissions. The rules (floor, duration, price, the
    /// piece list, the chosen affix) and the round trip through a real
    /// database: place, refuse what must be refused, collect - including
    /// collecting after the player was away the whole time.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorkshopCommissionTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _o;

        public WorkshopCommissionTests(PostgresTestFixture fixture, ITestOutputHelper o)
        {
            _fixture = fixture;
            _o = o;
            ContentRegistry.Initialize();
        }

        // ---------------------------------------------------------------
        // The rules
        // ---------------------------------------------------------------

        [Fact]
        public void TheWorkshopLevelSetsAFloorFromCommonToEpic()
        {
            // Owner decision 2026-09-30: T2-T6 by Workshop level; an unbuilt
            // Workshop commissions nothing.
            Assert.Equal(new[] { 0, 2, 3, 4, 5, 6 }, Enumerable.Range(0, 6).Select(WorkshopCommissionRules.WorkshopFloor));
            Assert.Equal(6, WorkshopCommissionRules.WorkshopFloor(VillageManagementEngine.MaxStructuralBuildingLevel + 3));

            // Region 1's usual gear is Rare (its boss wall asks for 4), so its
            // floor stops at Common; regions 2-5 take the whole Epic.
            Assert.Equal(RarityTier.Common, WorkshopCommissionRules.FloorTierFor(5, 1));
            for (int region = 2; region <= 5; region++)
            {
                Assert.Equal(RarityTier.Epic, WorkshopCommissionRules.FloorTierFor(5, region));
                Assert.Equal(RarityTier.Common, WorkshopCommissionRules.FloorTierFor(1, region));
            }
            Assert.Equal(0, WorkshopCommissionRules.FloorTierFor(0, 3));
        }

        [Fact]
        public void ACommissionTakesOneToEightHours_LongerForABetterFloor()
        {
            long previous = 0;
            for (int floor = RarityTier.Common; floor <= RarityTier.Epic; floor++)
            {
                long seconds = WorkshopCommissionRules.DurationSecondsFor(floor);
                Assert.InRange(seconds, 3600L, 8L * 3600L);
                Assert.True(seconds > previous, $"floor {floor} is not longer than the one below it");
                previous = seconds;
            }
            Assert.Equal(3600L, WorkshopCommissionRules.DurationSecondsFor(RarityTier.Common));
            Assert.Equal(8L * 3600L, WorkshopCommissionRules.DurationSecondsFor(RarityTier.Epic));
        }

        [Fact]
        public void ThePriceIsTheRegionsOwnCataloguedWoodAndOre_InTheTensOfThousands()
        {
            _o.WriteLine("region  floor        hours   price lines");
            for (int region = 1; region <= 5; region++)
            {
                var mats = VillageManagementEngine.GetTierMaterials((region - 1) * 5);
                long previousTotal = 0;
                for (int floor = RarityTier.Common; floor <= RarityTier.Epic; floor++)
                {
                    var lines = WorkshopCommissionRules.Quote(region, floor);
                    Assert.Equal(new[] { mats.Log, mats.Ore, mats.RareLog, mats.RareOre }, lines.Select(l => l.ItemId));

                    // The two-namespace trap: every line must be a CATALOGUED
                    // item, because TryConsumeUnifiedAsync spends by BaseId and
                    // only catalogued materials are what gathering pays out.
                    foreach (var line in lines)
                    {
                        Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId(line.ItemId, out var def), $"{line.ItemId} is not in items.json");
                        Assert.Equal(region, def.RegionTier);
                        Assert.True(line.Quantity > 0);
                    }

                    long total = lines.Sum(l => l.Quantity);
                    Assert.True(total > previousTotal, $"region {region} floor {floor} is not dearer than the floor below");
                    previousTotal = total;

                    if (floor == WorkshopCommissionRules.FloorTierFor(5, region))
                    {
                        _o.WriteLine($"  {region}     {RarityTier.GetName(floor),-10} {WorkshopCommissionRules.DurationSecondsFor(floor) / 3600,4}   "
                            + string.Join(", ", lines.Select(l => $"{l.Quantity:N0} {l.ItemId}")) + $"  = {total:N0}");
                    }
                }
            }

            // "Tens of thousands" at every region's best floor from region 2 on,
            // and never a quarter-million. Region 1's only floor is Common, an
            // hour's work, and costs thousands - it is the new player's region.
            for (int region = 2; region <= 5; region++)
            {
                long top = WorkshopCommissionRules.Quote(region, WorkshopCommissionRules.FloorTierFor(5, region)).Sum(l => l.Quantity);
                Assert.InRange(top, 20_000L, 250_000L);
            }
            Assert.InRange(WorkshopCommissionRules.Quote(1, RarityTier.Common).Sum(l => l.Quantity), 2_000L, 20_000L);
        }

        [Fact]
        public void EveryCanonicalDropIsCommissionable_AndNoToolIs()
        {
            var ids = WorkshopCommissionRules.CommissionableItemIds();

            // 15 pieces per location, five locations - the canonical catalogue.
            Assert.Equal(75, ids.Count);
            for (int region = 1; region <= 5; region++)
            {
                Assert.Equal(15, ids.Count(id => ContentRegistry.ItemDefinitions[id - 1].RegionTier == region));
            }

            foreach (int id in ids)
            {
                string baseId = ContentRegistry.GetItemBaseId(id);
                Assert.True(ContentRegistry.GetToolKind(baseId) < 0, $"{baseId} is a tool");
                var affixes = WorkshopCommissionRules.LegalAffixIds(baseId);
                Assert.NotEmpty(affixes);
                Assert.DoesNotContain("gather_speed_pct", affixes);
            }
        }

        [Fact]
        public void AChosenAffixMustBeLegalForTheSlot()
        {
            int weapon = WorkshopCommissionRules.CommissionableItemIds()
                .First(id => AffixRegistry.ResolveSlot(ContentRegistry.GetItemBaseId(id)) == EquipmentSlotKind.Weapon);
            int helmet = WorkshopCommissionRules.CommissionableItemIds()
                .First(id => AffixRegistry.ResolveSlot(ContentRegistry.GetItemBaseId(id)) == EquipmentSlotKind.Helmet);

            string weaponId = ContentRegistry.GetItemBaseId(weapon);
            string helmetId = ContentRegistry.GetItemBaseId(helmet);

            Assert.True(WorkshopCommissionRules.IsLegalAffix(weaponId, "melee_dmg_pct"));
            Assert.False(WorkshopCommissionRules.IsLegalAffix(helmetId, "melee_dmg_pct"));   // weapon-only
            Assert.False(WorkshopCommissionRules.IsLegalAffix(weaponId, "block_chance_pct")); // ring-only
            Assert.False(WorkshopCommissionRules.IsLegalAffix(weaponId, "gather_yield_pct")); // tool-only
            Assert.False(WorkshopCommissionRules.IsLegalAffix(weaponId, "melee_dmg_pct@1"));  // a payload key is not an id
            Assert.False(WorkshopCommissionRules.IsLegalAffix(weaponId, null));
        }

        [Fact]
        public void TheFinishedPieceCarriesTheChosenAffixAtCommon_AndTheTiersAffixCount()
        {
            int weapon = WorkshopCommissionRules.CommissionableItemIds()
                .First(id => AffixRegistry.ResolveSlot(ContentRegistry.GetItemBaseId(id)) == EquipmentSlotKind.Weapon
                             && ContentRegistry.ItemDefinitions[id - 1].RegionTier == 3);
            string baseId = ContentRegistry.GetItemBaseId(weapon);
            AffixRegistry.TryGetDefinition("crit_chance_pct", out var crit);
            var (min, max) = AffixRegistry.CalculateMagnitudeRange(crit, 3, AffixRarity.Common);

            for (int tier = RarityTier.Common; tier <= RarityTier.Transcendent; tier++)
            {
                var affixes = WorkshopCommissionRules.BuildAffixes(baseId, 3, tier, "crit_chance_pct");

                Assert.Equal(RarityTier.GetAffixCount(tier), affixes.Count);
                Assert.True(affixes.TryGetValue("crit_chance_pct@1", out int magnitude), "the chosen affix is missing or not at Common");
                Assert.InRange(magnitude, min, max);

                // Every key canonical, so every affix is rerollable (the
                // fusion's old "_xxxx" keys were not).
                foreach (string key in affixes.Keys)
                {
                    Assert.True(AffixRegistry.TryGetDefinition(AffixRegistry.StripStackSuffix(key), out var def), $"{key} does not resolve");
                    Assert.True((def.AllowedSlots & EquipmentSlotMask.Weapon) != 0, $"{key} is not a weapon affix");
                    Assert.Contains(AffixRegistry.RaritySeparator, key);
                }
            }
        }

        [Fact]
        public void TheFloorIsAFloor_AndItsMedianResultIsTheFloor()
        {
            var rng = new Random(83);
            for (int floor = RarityTier.Common; floor <= RarityTier.Epic; floor++)
            {
                var results = new int[20_001];
                for (int i = 0; i < results.Length; i++)
                {
                    results[i] = WorkshopCommissionRules.ResolveTier(floor, rng);
                    Assert.True(results[i] >= floor);
                }
                Array.Sort(results);
                Assert.Equal(floor, results[results.Length / 2]);
            }
        }

        // ---------------------------------------------------------------
        // The round trip
        // ---------------------------------------------------------------

        private static long _nextPlayerId = 983_000_000L;

        private async Task<long> SeedAsync(int workshopLevel, int bossesDefeated, long materialEach)
        {
            long playerId = Interlocked.Increment(ref _nextPlayerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
            if (workshopLevel > 0)
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = playerId,
                    BuildingId = VillageManagementEngine.CraftingWorkshopBuildingId,
                    CurrentLevel = workshopLevel,
                });
            }
            for (int region = 1; region <= bossesDefeated; region++)
            {
                db.MonsterCodexEntries.Add(new MonsterCodexEntry
                {
                    PlayerId = playerId, MonsterId = RaceUnlockRegistry.GetRegionBossMonsterId(region), KillCount = 1,
                });
            }
            for (int region = 1; region <= 5; region++)
            {
                var m = VillageManagementEngine.GetTierMaterials((region - 1) * 5);
                foreach (string item in new[] { m.Log, m.Ore, m.RareLog, m.RareOre })
                {
                    db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = item, Quantity = materialEach });
                }
            }
            await db.SaveChangesAsync();
            return playerId;
        }

        private static int PieceOf(int region, EquipmentSlotKind slot)
            => WorkshopCommissionRules.CommissionableItemIds().First(id =>
                ContentRegistry.ItemDefinitions[id - 1].RegionTier == region
                && AffixRegistry.ResolveSlot(ContentRegistry.GetItemBaseId(id)) == slot);

        private async Task<long> HeldAsync(long playerId, string item)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == item).SumAsync(c => c.Quantity);
        }

        [Fact]
        public async Task PlaceChargeWaitCollect_AndItFinishesWhileThePlayerIsAway()
        {
            const long start = 1_800_000_000L;
            long playerId = await SeedAsync(workshopLevel: 5, bossesDefeated: 2, materialEach: 1_000_000L);
            int piece = PieceOf(3, EquipmentSlotKind.Weapon);
            var quote = WorkshopCommissionRules.Quote(3, RarityTier.Epic);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(CommissionResult.Ok, await WorkshopCommissionEngine.StartAsync(db, playerId, piece, "crit_dmg_pct", start));
            }

            // Charged exactly the quote, every line.
            foreach (var line in quote)
            {
                Assert.Equal(1_000_000L - line.Quantity, await HeldAsync(playerId, line.ItemId));
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // One at a time.
                Assert.Equal(CommissionResult.Busy, await WorkshopCommissionEngine.StartAsync(db, playerId, piece, "crit_dmg_pct", start + 1));
                var view = await WorkshopCommissionEngine.ViewAsync(db, playerId, start + 60);
                Assert.NotNull(view!.Commission);
                Assert.Equal(RarityTier.Epic, view.Commission!.FloorTier);
                Assert.Equal(8L * 3600L - 60L, view.Commission.SecondsRemaining);
                Assert.False(view.Commission.Ready);

                var (early, none) = await WorkshopCommissionEngine.CollectAsync(db, playerId, start + 8L * 3600L - 1L, new Random(1));
                Assert.Equal(CommissionResult.NotReady, early);
                Assert.Null(none);
            }

            // THE OFFLINE CASE: no session, no tick, no worker - a fresh context
            // a day later, which is exactly what the next login is.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (result, collected) = await WorkshopCommissionEngine.CollectAsync(db, playerId, start + 24L * 3600L, new Random(2));
                Assert.Equal(CommissionResult.Ok, result);
                Assert.NotNull(collected);
                Assert.True(collected!.QualityTier >= RarityTier.Epic);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var item = await db.EquipmentInstances.AsNoTracking().SingleAsync(e => e.PlayerId == playerId);
                Assert.Equal(ContentRegistry.GetItemBaseId(piece), item.BaseItemId);
                Assert.True(item.QualityTier >= RarityTier.Epic);
                var affixes = JsonSerializer.Deserialize<Dictionary<string, int>>(item.AffixPayload)!;
                Assert.Contains("crit_dmg_pct@1", affixes.Keys);
                Assert.Equal(RarityTier.GetAffixCount(item.QualityTier), affixes.Count);

                Assert.False(await db.PlayerCraftingSlots.AnyAsync(s => s.PlayerId == playerId));
                var (again, _) = await WorkshopCommissionEngine.CollectAsync(db, playerId, start + 25L * 3600L, new Random(3));
                Assert.Equal(CommissionResult.NothingToCollect, again);

                // The drop record counts it, as a craft at its real tier.
                Assert.True(await db.Database.SqlQuery<int>($"SELECT COALESCE(SUM(\"Count\"), 0)::int AS \"Value\" FROM loot_tier_daily_counts WHERE \"PlayerId\" = {playerId} AND \"Source\" = {(short)DropSource.Craft}").SingleAsync() > 0);
            }
        }

        [Fact]
        public async Task EveryRefusalIsAnsweredAndChargesNothing()
        {
            const long now = 1_800_000_000L;
            int regionOneHelmet = PieceOf(1, EquipmentSlotKind.Helmet);
            int regionTwoHelmet = PieceOf(2, EquipmentSlotKind.Helmet);

            long unbuilt = await SeedAsync(workshopLevel: 0, bossesDefeated: 0, materialEach: 1_000_000L);
            long poor = await SeedAsync(workshopLevel: 3, bossesDefeated: 0, materialEach: 10L);
            long early = await SeedAsync(workshopLevel: 3, bossesDefeated: 0, materialEach: 1_000_000L);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(CommissionResult.WorkshopNotBuilt, await WorkshopCommissionEngine.StartAsync(db, unbuilt, regionOneHelmet, "flat_hp", now));
            Assert.Equal(CommissionResult.NotEnoughMaterials, await WorkshopCommissionEngine.StartAsync(db, poor, regionOneHelmet, "flat_hp", now));
            db.ChangeTracker.Clear();
            Assert.Equal(CommissionResult.RegionLocked, await WorkshopCommissionEngine.StartAsync(db, early, regionTwoHelmet, "flat_hp", now));
            Assert.Equal(CommissionResult.IllegalAffix, await WorkshopCommissionEngine.StartAsync(db, early, regionOneHelmet, "melee_dmg_pct", now));
            Assert.Equal(CommissionResult.UnknownPiece, await WorkshopCommissionEngine.StartAsync(db, early, 0, "flat_hp", now));

            // A tool is the recipe tree's, never a commission.
            int tool = Enumerable.Range(1, ContentRegistry.ItemDefinitions.Length)
                .First(id => ContentRegistry.GetToolKind(ContentRegistry.GetItemBaseId(id)) >= 0);
            Assert.Equal(CommissionResult.UnknownPiece, await WorkshopCommissionEngine.StartAsync(db, early, tool, "gather_speed_pct", now));

            var m = VillageManagementEngine.GetTierMaterials(0);
            Assert.Equal(10L, await HeldAsync(poor, m.Log));
            Assert.Equal(1_000_000L, await HeldAsync(early, m.Log));
            Assert.False(await db.PlayerCraftingSlots.AnyAsync(s => s.PlayerId == poor || s.PlayerId == early || s.PlayerId == unbuilt));
        }

        [Fact]
        public async Task AFinishedWorkshopUpgradeCountsBeforeAnythingWritesIt()
        {
            const long now = 1_800_000_000L;
            long playerId = await SeedAsync(workshopLevel: 1, bossesDefeated: 1, materialEach: 1_000_000L);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var row = await db.VillageInfrastructures.SingleAsync(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.CraftingWorkshopBuildingId);
                row.UpgradeTargetLevel = 2;
                row.UpgradeCompletesAtEpoch = now - 5;
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(2, await WorkshopCommissionEngine.ReadWorkshopLevelAsync(read, playerId, now));
            Assert.Equal(1, await WorkshopCommissionEngine.ReadWorkshopLevelAsync(read, playerId, now - 10));

            Assert.Equal(CommissionResult.Ok, await WorkshopCommissionEngine.StartAsync(read, playerId, PieceOf(2, EquipmentSlotKind.Boots), "dodge_chance_pct", now));
            read.ChangeTracker.Clear();
            var slot = await read.PlayerCraftingSlots.AsNoTracking().SingleAsync(s => s.PlayerId == playerId);
            Assert.Equal(RarityTier.Uncommon, slot.FloorTier);
        }

        [Fact]
        public async Task ARebirthTakesTheCommissionInProgressWithTheGear()
        {
            const long now = 1_800_000_000L;
            long playerId = await SeedAsync(workshopLevel: 5, bossesDefeated: 4, materialEach: 1_000_000L);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(CommissionResult.Ok, await WorkshopCommissionEngine.StartAsync(db, playerId, PieceOf(5, EquipmentSlotKind.Chest), "flat_armor", now));
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await SeasonalRotationEngine.ResetPlayersAsync(db, playerId, CancellationToken.None);
                await tx.CommitAsync();
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await verify.PlayerCraftingSlots.AnyAsync(s => s.PlayerId == playerId));
        }

        [Fact]
        public async Task TheDevFinishRoundTripsTheFixture()
        {
            const long now = 1_800_000_000L;
            long playerId = await SeedAsync(workshopLevel: 5, bossesDefeated: 0, materialEach: 100_000L);
            int piece = PieceOf(1, EquipmentSlotKind.Ring);
            var m = VillageManagementEngine.GetTierMaterials(0);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(CommissionResult.Ok, await WorkshopCommissionEngine.StartAsync(db, playerId, piece, "block_chance_pct", now));
            db.ChangeTracker.Clear();
            Assert.True(await HeldAsync(playerId, m.Log) < 100_000L);

            Assert.Equal(CommissionResult.Ok, await WorkshopCommissionEngine.DevFinishAsync(db, playerId, now + 1, refund: true));
            db.ChangeTracker.Clear();
            Assert.Equal(100_000L, await HeldAsync(playerId, m.Log));

            var (result, collected) = await WorkshopCommissionEngine.CollectAsync(db, playerId, now + 1, new Random(4));
            Assert.Equal(CommissionResult.Ok, result);
            Assert.Equal(RarityTier.Common, WorkshopCommissionRules.FloorTierFor(5, 1));
            Assert.True(collected!.QualityTier >= RarityTier.Common);
        }
    }
}

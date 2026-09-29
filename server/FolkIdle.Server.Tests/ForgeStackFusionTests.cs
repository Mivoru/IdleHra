using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 69: a whole stack fused in one action. The planner is pure and the
    /// engine carries out exactly the plan, with the single fusion's own rules
    /// (fee, Forge ceiling, locked pieces untouched).
    /// </summary>
    [Collection("Postgres collection")]
    public class ForgeStackFusionTests
    {
        private const string Helm = "eq_sentry_helm_helmet_armor_slot_base";
        private readonly PostgresTestFixture _fixture;

        public ForgeStackFusionTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private static int[] Counts(params (int Tier, int Count)[] entries)
        {
            var counts = new int[ForgeSplicingEngine.MaxQualityTier + 1];
            foreach (var (tier, count) in entries) counts[tier] = count;
            return counts;
        }

        [Fact]
        public void ThePlan_FusesTriplesTierByTier_AndProductsFuseAgain()
        {
            var plan = ForgeSplicingEngine.PlanStack(Counts((1, 30)), 1, 4, 14, 0.0, long.MaxValue);

            Assert.Equal(10, plan.FusionsByTier[1]);
            Assert.Equal(3, plan.FusionsByTier[2]);
            Assert.Equal(1, plan.FusionsByTier[3]);
            Assert.Equal(14, plan.TotalFusions);
            Assert.Equal(0, plan.CountsAfter[1]);
            Assert.Equal(1, plan.CountsAfter[2]);
            Assert.Equal(0, plan.CountsAfter[3]);
            Assert.Equal(1, plan.CountsAfter[4]);
            Assert.Equal(
                10 * ForgeSplicingEngine.FusionFee(1, 0) + 3 * ForgeSplicingEngine.FusionFee(2, 0) + ForgeSplicingEngine.FusionFee(3, 0),
                plan.GoldCost);
        }

        [Fact]
        public void ThePlan_StopsAtTheForgeLevel_AndAtTheGold()
        {
            var capped = ForgeSplicingEngine.PlanStack(Counts((1, 30)), 1, 14, 2, 0.0, long.MaxValue);
            Assert.Equal(2, capped.CeilingTier);
            Assert.Equal(10, capped.TotalFusions);
            Assert.Equal(10, capped.CountsAfter[2]);

            long fee = ForgeSplicingEngine.FusionFee(1, 0);
            var poor = ForgeSplicingEngine.PlanStack(Counts((1, 30)), 1, 14, 14, 0.0, fee * 4 + fee / 2);
            Assert.Equal(4, poor.TotalFusions);
            Assert.True(poor.StoppedByGold);
            Assert.Equal(fee * 4, poor.GoldCost);

            var bounded = ForgeSplicingEngine.PlanStack(Counts((1, 30)), 1, 14, 14, 0.0, long.MaxValue, maxFusions: 7);
            Assert.Equal(7, bounded.TotalFusions);
            Assert.True(bounded.StoppedByCap);
        }

        [Fact]
        public async Task TheEngine_FusesTheStack_ChargesThePlan_AndLeavesALockedPiece()
        {
            long player = DbSeeder.PlayerHighId;
            long sampleId, lockedId;
            long goldBefore;
            long fusionsBefore;
            double discount;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.EquipmentInstances.Where(e => e.PlayerId == player && e.BaseItemId == Helm).ExecuteDeleteAsync();

                var forge = await db.VillageInfrastructures.SingleOrDefaultAsync(
                    v => v.PlayerId == player && v.BuildingId == VillageManagementEngine.ForgeBuildingId);
                if (forge is null)
                {
                    db.VillageInfrastructures.Add(new VillageInfrastructure
                    {
                        PlayerId = player,
                        BuildingId = VillageManagementEngine.ForgeBuildingId,
                        CurrentLevel = ForgeSplicingEngine.MaxQualityTier,
                    });
                }
                else if (forge.CurrentLevel < ForgeSplicingEngine.MaxQualityTier)
                {
                    forge.CurrentLevel = ForgeSplicingEngine.MaxQualityTier;
                }

                var gold = await db.CommodityRecords.SingleOrDefaultAsync(c => c.PlayerId == player && c.ItemId == "gold");
                if (gold is null)
                {
                    gold = new CommodityRecord { PlayerId = player, ItemId = "gold", Quantity = 10_000_000 };
                    db.CommodityRecords.Add(gold);
                }
                else if (gold.Quantity < 10_000_000)
                {
                    gold.Quantity = 10_000_000;
                }

                var pieces = Enumerable.Range(0, 30)
                    .Select(_ => new EquipmentInstance { PlayerId = player, BaseItemId = Helm, QualityTier = 1 })
                    .ToList();
                var locked = new EquipmentInstance { PlayerId = player, BaseItemId = Helm, QualityTier = 1, IsAffixLocked = true };
                db.EquipmentInstances.AddRange(pieces);
                db.EquipmentInstances.Add(locked);
                await db.SaveChangesAsync();

                sampleId = pieces[0].Id;
                lockedId = locked.Id;
                goldBefore = gold.Quantity;
                var record = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == player);
                fusionsBefore = record.ForgeFusionsCompleted;
                discount = ForgeSplicingEngine.FeeDiscountFor(record.BaseLuck);
            }

            var plan = await new ForgeSplicingEngine(_fixture.ServiceProvider)
                .ExecuteStackFusionAsync(player, sampleId, 3);

            Assert.NotNull(plan);
            Assert.Equal(13, plan!.TotalFusions);
            long expectedCost = 10 * ForgeSplicingEngine.FusionFee(1, discount) + 3 * ForgeSplicingEngine.FusionFee(2, discount);
            Assert.Equal(expectedCost, plan.GoldCost);

            await using (var check = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var left = await check.EquipmentInstances.AsNoTracking()
                    .Where(e => e.PlayerId == player && e.BaseItemId == Helm)
                    .ToListAsync();

                // 30 Normals -> 10 Commons -> 3 Uncommons + 1 Common left, and
                // the locked Normal was never touched.
                Assert.Equal(3, left.Count(e => e.QualityTier == 3));
                Assert.Equal(1, left.Count(e => e.QualityTier == 2));
                Assert.Single(left.Where(e => e.QualityTier == 1));
                Assert.Equal(lockedId, left.Single(e => e.QualityTier == 1).Id);

                long goldAfter = await check.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == player && c.ItemId == "gold").Select(c => c.Quantity).SingleAsync();
                Assert.Equal(goldBefore - expectedCost, goldAfter);

                var record = await check.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == player);
                Assert.Equal(fusionsBefore + 13, record.ForgeFusionsCompleted);

                // Each fused piece gained one affix per step it took.
                foreach (var piece in left.Where(e => e.QualityTier == 3))
                {
                    var payload = System.Text.Json.Nodes.JsonNode.Parse(piece.AffixPayload!)!.AsObject();
                    Assert.True(payload.Count >= 2, $"piece {piece.Id} should carry two fusion affixes, has {payload.Count}");
                }

                await check.EquipmentInstances.Where(e => e.PlayerId == player && e.BaseItemId == Helm).ExecuteDeleteAsync();
            }
        }

        [Fact]
        public async Task TheEngine_RefusesAStackThatIsNotYours()
        {
            var plan = await new ForgeSplicingEngine(_fixture.ServiceProvider)
                .ExecuteStackFusionAsync(DbSeeder.PlayerHighId, long.MaxValue - 7, 3);
            Assert.Null(plan);
        }
    }
}

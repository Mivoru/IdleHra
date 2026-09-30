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
    /// Task 85, rule 3: "fuse stacks up to tier N" fuses through the Forge's own
    /// stack fusion - same plan, same fee, same refusal of a locked piece - and
    /// the loot worker runs it for the stacks a request's drops landed in.
    /// </summary>
    [Collection("Postgres collection")]
    public class AutomationAutoFuseTests
    {
        private const string Helm = "eq_sentry_helm_helmet_armor_slot_base";
        private readonly PostgresTestFixture _fixture;

        public AutomationAutoFuseTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task<long> CreatePlayerAsync(long gold)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"autofuse_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            db.VillageInfrastructures.Add(new VillageInfrastructure
            {
                PlayerId = player.Id,
                BuildingId = VillageManagementEngine.ForgeBuildingId,
                CurrentLevel = ForgeSplicingEngine.MaxQualityTier,
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = gold });
            await db.SaveChangesAsync();
            return player.Id;
        }

        [Fact]
        public async Task TheRuleFusesThroughTheStackFusion_AtTheButtonsPrice()
        {
            long player = await CreatePlayerAsync(10_000_000);
            long lockedId;
            double discount;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.EquipmentInstances.AddRange(Enumerable.Range(0, 10)
                    .Select(_ => new EquipmentInstance { PlayerId = player, BaseItemId = Helm, QualityTier = 1 }));
                var locked = new EquipmentInstance { PlayerId = player, BaseItemId = Helm, QualityTier = 1, IsAffixLocked = true };
                db.EquipmentInstances.Add(locked);
                await db.SaveChangesAsync();
                lockedId = locked.Id;
                discount = ForgeSplicingEngine.FeeDiscountFor((await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == player)).BaseLuck);
            }

            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            engine.NoteAutoFuse(player, 3, new[] { Helm });
            await engine.RunAutoFusionsAsync();

            // 10 Normals -> 3 Commons + 1 Normal -> 1 Uncommon; the locked one untouched.
            long expected = 3 * ForgeSplicingEngine.FusionFee(1, discount) + ForgeSplicingEngine.FusionFee(2, discount);
            await using (var check = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var left = await check.EquipmentInstances.AsNoTracking().Where(e => e.PlayerId == player).ToListAsync();
                Assert.Single(left.Where(e => e.QualityTier == 3));
                Assert.Empty(left.Where(e => e.QualityTier == 2));
                Assert.Equal(2, left.Count(e => e.QualityTier == 1));
                Assert.Contains(left, e => e.Id == lockedId && e.QualityTier == 1);

                long gold = await check.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == player && c.ItemId == "gold").Select(c => c.Quantity).SingleAsync();
                Assert.Equal(10_000_000 - expected, gold);
                Assert.Equal(4, (await check.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == player)).ForgeFusionsCompleted);
            }

            // The live balance follows the row - display only, never banked.
            var notes = _fixture.PlayerRegistry.ChestSaleGoldQueue.ToArray().Where(n => n.PlayerId == player).ToList();
            Assert.Single(notes);
            Assert.Equal(-expected, notes[0].GoldGained);
        }

        [Fact]
        public async Task NoGold_NoFusion_AndNothingSpent()
        {
            long player = await CreatePlayerAsync(0);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.EquipmentInstances.AddRange(Enumerable.Range(0, 3)
                    .Select(_ => new EquipmentInstance { PlayerId = player, BaseItemId = Helm, QualityTier = 1 }));
                await db.SaveChangesAsync();
            }

            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            engine.NoteAutoFuse(player, 5, new[] { Helm });
            await engine.RunAutoFusionsAsync();

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(3, await check.EquipmentInstances.CountAsync(e => e.PlayerId == player && e.QualityTier == 1));
        }

        [Fact]
        public async Task TheWorker_FusesTheStacksARequestsDropsLandedIn()
        {
            long player = await CreatePlayerAsync(50_000_000);
            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            try
            {
                // A regional boss guarantees a piece a kill, so 150 kills put
                // plenty of Normals of each of its pieces into the chest.
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = player,
                    MonsterId = RaceUnlockRegistry.GetRegionBossMonsterId(1),
                    Kills = 150,
                    SkipMaterialRoll = true,
                    Source = DropSource.Offline,
                    AutoFuseToTier = 2,
                });
                engine.StartCron();

                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (true)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the worker fused nothing within 60 seconds");
                    await Task.Delay(500);
                    await using var poll = await _fixture.DbContextFactory.CreateDbContextAsync();
                    if ((await poll.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == player)).ForgeFusionsCompleted > 0) break;
                }
            }
            finally
            {
                engine.StopCron();
            }

            // Up to tier 2 means no stack is left holding three Normals.
            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var normals = await check.EquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == player && e.QualityTier == 1)
                .GroupBy(e => e.BaseItemId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync();
            Assert.All(normals, n => Assert.True(n.Count < 3, $"{n.Key} still holds {n.Count} Normals"));
            Assert.True(await check.EquipmentInstances.AnyAsync(e => e.PlayerId == player && e.QualityTier == 2));
        }
    }
}

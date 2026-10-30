using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The Hearth half (roadmap 7.3, step 4.3): its shape, its caps (the old
    /// Insight limb's size, not bigger), and that each wired node moves the
    /// number the simulation uses.
    /// </summary>
    public class HearthTreeRegistryTests
    {
        public HearthTreeRegistryTests()
        {
            ContentRegistry.Initialize();
        }

        [Fact]
        public void ThirtyTwoNodesAtIds56To87_InsideTheBuffer_ApartFromWar()
        {
            var nodes = HearthTreeRegistry.All().ToList();
            Assert.Equal(HearthTreeRegistry.Count, nodes.Count);
            Assert.All(nodes, n => Assert.InRange(n.Id, HearthTreeRegistry.FirstId, HearthTreeRegistry.FirstId + HearthTreeRegistry.Count - 1));
            Assert.All(nodes, n => Assert.True(n.Id < SkillRankBuffer.Length));
            Assert.All(nodes, n => Assert.Null(WarTreeRegistry.Get(n.Id)));
        }

        [Fact]
        public void EveryColumnHasTheWarShape_And37PointsWithOneForkSide()
        {
            for (int column = 0; column < HearthTreeRegistry.ColumnCount; column++)
            {
                var col = HearthTreeRegistry.All().Where(n => n.Column == column).ToList();
                Assert.Equal(8, col.Count);
                Assert.Equal(3, col.Count(n => n.Kind == "Minor"));
                Assert.Equal(2, col.Count(n => n.Kind == "Notable"));
                Assert.Equal(2, col.Count(n => n.Kind == "Fork"));
                Assert.Single(col, n => n.Kind == "Keystone");

                var forks = col.Where(n => n.Kind == "Fork").ToList();
                Assert.Equal(forks[0].Id, forks[1].Fork);
                Assert.Equal(forks[1].Id, forks[0].Fork);

                int oneSide = col.Where(n => n.Kind != "Fork").Sum(n => n.MaxLevel * n.CostPerRank)
                    + forks[0].MaxLevel * forks[0].CostPerRank;
                Assert.Equal(37, oneSide);
            }
        }

        private static TickStatePayload Maxed(params int[] ids)
        {
            var p = new TickStatePayload();
            foreach (int id in ids) p.SkillRanks[id] = (byte)HearthTreeRegistry.Get(id)!.MaxLevel;
            return p;
        }

        [Fact]
        public void SpeedTopsOutAtTheOldHarvestsSixteenPercent_PerProfession()
        {
            var wood = Maxed(HearthTreeRegistry.SharpAxe, HearthTreeRegistry.SteadySwing);
            var herb = Maxed(HearthTreeRegistry.GreenThumb, HearthTreeRegistry.StillWater);
            Assert.Equal(160, HearthTreeRegistry.Resolve(in wood).SpeedTenthsPctFor(0));
            Assert.Equal(60, HearthTreeRegistry.Resolve(in wood).SpeedTenthsPctFor(1)); // Steady Swing only
            Assert.Equal(0, HearthTreeRegistry.Resolve(in wood).SpeedTenthsPctFor(2));
            Assert.Equal(160, HearthTreeRegistry.Resolve(in herb).SpeedTenthsPctFor(3));
        }

        [Fact]
        public void TheRewardRateIsTheOldScholarsTwentyFivePercent_AndThriftyTheOldCraftsTen()
        {
            Assert.Equal(250, ScholarRate.MaxPermille);
            Assert.Equal(1.25, ScholarRate.MaxMultiplier, 6);
            var kin = Maxed(HearthTreeRegistry.Homebody, HearthTreeRegistry.LongAbsence);
            Assert.Equal(250, ScholarRate.BonusPermille(in kin));
            Assert.Equal(100, HearthTreeRegistry.KeepMaterialsTenthsPct(5));
            Assert.Equal(100, HearthTreeRegistry.KeepMaterialsTenthsPct(99));
        }

        [Fact]
        public void OnlyTheNodesWithAReaderAreBuyable()
        {
            int[] wired =
            {
                HearthTreeRegistry.SharpAxe, HearthTreeRegistry.HardPick, HearthTreeRegistry.SeamSense, HearthTreeRegistry.SteadySwing,
                HearthTreeRegistry.Clearcut, HearthTreeRegistry.Prospector, HearthTreeRegistry.DoubleHaul,
                HearthTreeRegistry.PatientLine, HearthTreeRegistry.GreenThumb, HearthTreeRegistry.GoodWaters, HearthTreeRegistry.StillWater,
                HearthTreeRegistry.KeenAngler, HearthTreeRegistry.Netmaker, HearthTreeRegistry.FullNet,
                HearthTreeRegistry.Thrifty, HearthTreeRegistry.Studious, HearthTreeRegistry.Homebody, HearthTreeRegistry.LongAbsence,
                HearthTreeRegistry.DeepVein, HearthTreeRegistry.MoonlitCatch,
                HearthTreeRegistry.QuickHands, HearthTreeRegistry.BatchWork,
            };
            foreach (var n in HearthTreeRegistry.All())
                Assert.Equal(!wired.Contains(n.Id), HearthTreeRegistry.IsPending(n.Id));
        }

        [Fact]
        public void TheGatheringFormulasReadTheHearthNodes_LiveAndOfflineShareThem()
        {
            Assert.True(ContentRegistry.TryGetGatheringNode(ActivityIdBands.WoodcuttingBand + 1, out GatheringNodeDefinition node));
            var bare = new TickStatePayload { WoodcuttingMasteryLevel = 10, CachedCodexYieldMultiplier = 1f };
            var built = bare;
            foreach (int id in new[] { HearthTreeRegistry.SharpAxe, HearthTreeRegistry.SteadySwing, HearthTreeRegistry.SeamSense, HearthTreeRegistry.DoubleHaul, HearthTreeRegistry.Prospector })
                built.SkillRanks[id] = (byte)HearthTreeRegistry.Get(id)!.MaxLevel;

            // Speed: RequiredGatherTicks and the offline projection both read GatherSpeedTermsFor.
            Assert.Equal(16, SimulationEngine.GatherSpeedTermsFor(ref built, 0).ExtraPct - SimulationEngine.GatherSpeedTermsFor(ref bare, 0).ExtraPct);

            // Yield and rare find: GatheringYieldFor is the one composition both paths call.
            var yBare = SimulationEngine.GatheringYieldFor(ref bare, in node, 100);
            var yBuilt = SimulationEngine.GatheringYieldFor(ref built, in node, 100);
            Assert.Equal(6 + 9, yBuilt.MultiplierPct - yBare.MultiplierPct);
            Assert.True(yBuilt.LuckWeightBonus > yBare.LuckWeightBonus);
        }

        [Fact]
        public void TheKeystonesCountEveryTwentiethHarvest_AndRollTheRarestEntry()
        {
            Assert.Equal(1, SimulationEngine.RareKeystoneRolls(19, 20));
            Assert.Equal(0, SimulationEngine.RareKeystoneRolls(20, 39));
            Assert.Equal(5, SimulationEngine.RareKeystoneRolls(0, 100));
            Assert.Equal(5, SimulationEngine.RareKeystoneRolls(7, 107));

            Assert.True(ContentRegistry.TryGetGatheringNode(ActivityIdBands.WoodcuttingBand + 1, out GatheringNodeDefinition node));
            var table = ContentRegistry.GetLootTable(node.ActivityId);
            int rarest = SimulationEngine.RarestEntryIndex(table);
            Assert.True(rarest >= 0);
            for (int i = 0; i < table.Length; i++)
                if (table[i].Weight > 0) Assert.True(table[rarest].Weight <= table[i].Weight);

            var vein = Maxed(HearthTreeRegistry.DeepVein);
            Assert.True(HearthTreeRegistry.Resolve(in vein).RareRollFor(0));
            Assert.True(HearthTreeRegistry.Resolve(in vein).RareRollFor(1));
            Assert.False(HearthTreeRegistry.Resolve(in vein).RareRollFor(2));
            var moon = Maxed(HearthTreeRegistry.MoonlitCatch);
            Assert.True(HearthTreeRegistry.Resolve(in moon).RareRollFor(2));
            Assert.False(HearthTreeRegistry.Resolve(in moon).RareRollFor(0));
        }

        [Fact]
        public void QuickHandsAndBatchWorkCutCraftTimeBySixteenPercent_OnTheOneFormulaBothPathsAsk()
        {
            var recipe = ContentRegistry.Recipes.ToArray().OrderByDescending(r => r.CraftingTimeMs).First();
            var bare = new TickStatePayload();
            var built = Maxed(HearthTreeRegistry.QuickHands, HearthTreeRegistry.BatchWork);
            Assert.Equal(160, HearthTreeRegistry.Resolve(in built).CraftTimeTenthsPct);
            int before = SimulationEngine.CraftTicksFor(in bare, in recipe);
            int after = SimulationEngine.CraftTicksFor(in built, in recipe);
            Assert.Equal(SimulationEngine.CraftTicksFor(in recipe), before);
            Assert.Equal((int)((long)before * 840 / 1000), after);
        }

        [Fact]
        public void StudiousRaisesTheXpMultiplier()
        {
            var bare = new TickStatePayload();
            var built = Maxed(HearthTreeRegistry.Studious);
            Assert.Equal(50, HearthTreeRegistry.Resolve(in built).XpTenthsPct);
            Assert.Equal(0, HearthTreeRegistry.Resolve(in bare).XpTenthsPct);
        }
    }

    /// <summary>The War half's respec and commit never touch a Hearth row.</summary>
    [Collection("Postgres collection")]
    public class HearthRowsSurviveWarTests
    {
        private readonly PostgresTestFixture _fixture;

        public HearthRowsSurviveWarTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task AWarRespecAndAWarCommitLeaveTheHearthRows()
        {
            long playerId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = new PlayerRecord
                {
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    Username = $"hearth_{Guid.NewGuid():N}".Substring(0, 20),
                    SelectedLineageId = 1,
                    AvailableSkillPoints = 10,
                    CurrentLevel = 40,
                };
                db.PlayerRecords.Add(player);
                await db.SaveChangesAsync();
                playerId = player.Id;
                db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = playerId, BranchId = HearthTreeRegistry.Thrifty, Level = 3 });
                await db.SaveChangesAsync();
            }

            var engine = new SkillTreeEngine(_fixture.ServiceProvider);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var target = await SkillTreeEngine.LoadRanksAsync(db, playerId);
                Assert.Equal(3, target[HearthTreeRegistry.Thrifty]);
                target[WarTreeRegistry.Edge] = 2;
                var commit = await engine.CommitAsync(playerId, target);
                Assert.Null(commit.Reason);
            }

            var respec = await engine.RespecAsync(playerId);
            Assert.Null(respec.Reason);
            Assert.Equal(2, respec.Refunded);

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await check.PlayerSkillTreeNodes.AsNoTracking().Where(n => n.PlayerId == playerId).ToListAsync();
            var thrifty = Assert.Single(rows);
            Assert.Equal(HearthTreeRegistry.Thrifty, thrifty.BranchId);
            Assert.Equal(3, thrifty.Level);
            Assert.Equal(10, (await check.PlayerRecords.SingleAsync(p => p.Id == playerId)).AvailableSkillPoints);
        }
    }
}

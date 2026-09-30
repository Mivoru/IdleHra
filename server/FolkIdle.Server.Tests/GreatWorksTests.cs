using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 84: the Great Works - five solo monuments that eat a region's
    /// materials in five stages and pay a small permanent, capped bonus.
    /// The pure rules first (no database), then the deposit transaction, then the
    /// two readers of the bonus: the gathering yield and the offline cap.
    /// </summary>
    [Collection("Postgres collection")]
    public class GreatWorksTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _o;

        public GreatWorksTests(PostgresTestFixture fixture, ITestOutputHelper o)
        {
            _fixture = fixture;
            _o = o;
            ContentRegistry.Initialize();
        }

        // ---- the table ------------------------------------------------------

        [Fact]
        public void FiveMonumentsOfFiveStages_PricedInTheOwnersRange()
        {
            Assert.Equal(5, GreatWorksRegistry.Monuments.Length);
            Assert.Equal(5, GreatWorksRegistry.StageCosts.Length);
            Assert.Equal(5, GreatWorksRegistry.StageNames.Length);
            Assert.Equal(50_000L, GreatWorksRegistry.StageCosts.First());
            Assert.Equal(2_000_000L, GreatWorksRegistry.StageCosts.Last());
            for (int i = 1; i < 5; i++) Assert.True(GreatWorksRegistry.StageCosts[i] > GreatWorksRegistry.StageCosts[i - 1]);
            for (int i = 0; i < 5; i++) Assert.Equal(i + 1, GreatWorksRegistry.Monuments[i].Region);
        }

        [Fact]
        public void EveryStageOfEveryMonumentPaysSomething_AndTheMaterialsAreCatalogued()
        {
            foreach (var m in GreatWorksRegistry.Monuments)
            {
                Assert.True(m.YieldPctPerStage > 0 || m.OfflineMinutesPerStage > 0, $"{m.Name} pays nothing per stage");
                Assert.False(string.IsNullOrEmpty(GreatWorksRegistry.DescribeStageBonus(m.Region)));
                foreach (int kind in new[] { GreatWorksRegistry.MaterialLog, GreatWorksRegistry.MaterialOre })
                {
                    string item = GreatWorksRegistry.MaterialFor(m.Region, kind);
                    // Two material namespaces exist: a monument must ask for a CATALOGUED one.
                    Assert.True(ContentRegistry.TryGetItemDefinitionByBaseId(item, out _), $"{item} is not in items.json");
                }
                Assert.NotEqual(
                    GreatWorksRegistry.MaterialFor(m.Region, GreatWorksRegistry.MaterialLog),
                    GreatWorksRegistry.MaterialFor(m.Region, GreatWorksRegistry.MaterialOre));
            }
        }

        [Fact]
        public void ThePackedCacheRoundTripsAndTheBonusesAreDerivedFromIt()
        {
            int packed = 0;
            Assert.Equal(0, GreatWorksRegistry.YieldPct(packed));
            Assert.Equal(0, GreatWorksRegistry.OfflineMinutes(packed));

            packed = GreatWorksRegistry.WithStage(packed, 1, 3);
            packed = GreatWorksRegistry.WithStage(packed, 5, 5);
            packed = GreatWorksRegistry.WithStage(packed, 2, 2);
            Assert.Equal(3, GreatWorksRegistry.StageOf(packed, 1));
            Assert.Equal(2, GreatWorksRegistry.StageOf(packed, 2));
            Assert.Equal(0, GreatWorksRegistry.StageOf(packed, 3));
            Assert.Equal(5, GreatWorksRegistry.StageOf(packed, 5));
            Assert.Equal(3 + 5, GreatWorksRegistry.YieldPct(packed));
            Assert.Equal(15 * 2 + 15 * 5, GreatWorksRegistry.OfflineMinutes(packed));

            // Lowering, and out-of-range input, never bleed into a neighbour.
            packed = GreatWorksRegistry.WithStage(packed, 1, 1);
            Assert.Equal(1, GreatWorksRegistry.StageOf(packed, 1));
            Assert.Equal(2, GreatWorksRegistry.StageOf(packed, 2));
            Assert.Equal(packed, GreatWorksRegistry.WithStage(packed, 9, 3));
            Assert.Equal(5, GreatWorksRegistry.StageOf(GreatWorksRegistry.WithStage(0, 1, 99), 1));
        }

        [Fact]
        public void TheCapsAreTheSumOfEveryMonumentComplete()
        {
            int all = 0;
            for (int r = 1; r <= 5; r++) all = GreatWorksRegistry.WithStage(all, r, 5);
            Assert.Equal(GreatWorksRegistry.MaxYieldPct, GreatWorksRegistry.YieldPct(all));
            Assert.Equal(GreatWorksRegistry.MaxOfflineMinutes, GreatWorksRegistry.OfflineMinutes(all));
            Assert.Equal(15, GreatWorksRegistry.MaxYieldPct);
            Assert.Equal(225, GreatWorksRegistry.MaxOfflineMinutes);
        }

        // ---- the deposit ----------------------------------------------------

        private async Task SeedAsync(long playerId, string item, long quantity)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            if (!await db.PlayerRecords.AnyAsync(p => p.Id == playerId))
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(),
                    Username = $"gw{playerId}",
                });
            }
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = item, Quantity = quantity });
            await db.SaveChangesAsync();
        }

        private async Task<long> HeldAsync(long playerId, string item)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == playerId && c.ItemId == item).SumAsync(c => c.Quantity);
        }

        [Fact]
        public async Task ADepositSpendsExactlyWhatItAddsAndAStageCompletesOnItsCost()
        {
            const long playerId = 984_000_001L;
            string log = GreatWorksRegistry.MaterialFor(2, GreatWorksRegistry.MaterialLog);
            await SeedAsync(playerId, log, 200_000);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var first = await GreatWorksEngine.DepositCoreAsync(db, playerId, 2, GreatWorksRegistry.MaterialLog, 10_000, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.GreatWorkDeposited, first.Result);
            Assert.Equal(190_000, await HeldAsync(playerId, log));
            var view = (await GreatWorksEngine.ViewAsync(db, playerId)).Works.Single(w => w.Region == 2);
            Assert.Equal(0, view.Stage);
            Assert.Equal(10_000, view.Progress);
            Assert.Equal(50_000, view.NextCost);
            Assert.Equal(190_000, view.HeldLog);

            // 0 = "everything I hold, up to what the stage needs": it finishes stage 1 and no more.
            var finish = await GreatWorksEngine.DepositCoreAsync(db, playerId, 2, GreatWorksRegistry.MaterialLog, 0, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.GreatWorkStageBuilt, finish.Result);
            Assert.Equal(150_000, await HeldAsync(playerId, log));
            Assert.Equal(1, GreatWorksRegistry.StageOf(finish.Packed, 2));
            view = (await GreatWorksEngine.ViewAsync(db, playerId)).Works.Single(w => w.Region == 2);
            Assert.Equal(1, view.Stage);
            Assert.Equal(0, view.Progress);
            Assert.Equal(150_000, view.NextCost);

            // An over-large request is CLAMPED to what stage 2 needs, never refused and never lost.
            var clamped = await GreatWorksEngine.DepositCoreAsync(db, playerId, 2, GreatWorksRegistry.MaterialLog, 999_999_999, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.GreatWorkStageBuilt, clamped.Result);
            Assert.Equal(0, await HeldAsync(playerId, log));
            Assert.Equal(2, GreatWorksRegistry.StageOf(clamped.Packed, 2));

            // Nothing left to spend: answered, and nothing moved.
            var dry = await GreatWorksEngine.DepositCoreAsync(db, playerId, 2, GreatWorksRegistry.MaterialLog, 5, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.InsufficientMaterials, dry.Result);
            var dryAll = await GreatWorksEngine.DepositCoreAsync(db, playerId, 2, GreatWorksRegistry.MaterialLog, 0, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.InsufficientMaterials, dryAll.Result);
        }

        [Fact]
        public async Task TheOreCountsTowardTheSameStage_AndAFinishedMonumentIsAnswered()
        {
            const long playerId = 984_000_002L;
            string ore = GreatWorksRegistry.MaterialFor(1, GreatWorksRegistry.MaterialOre);
            await SeedAsync(playerId, ore, 60_000);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var result = await GreatWorksEngine.DepositCoreAsync(db, playerId, 1, GreatWorksRegistry.MaterialOre, 0, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.GreatWorkStageBuilt, result.Result);
            Assert.Equal(10_000, await HeldAsync(playerId, ore));

            // A finished monument answers GreatWorkComplete and spends nothing.
            await GreatWorksEngine.DevRestoreAsync(db, playerId, 1, 5, 0, GreatWorksRegistry.MaterialOre, 0);
            var done = await GreatWorksEngine.DepositCoreAsync(db, playerId, 1, GreatWorksRegistry.MaterialOre, 0, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.GreatWorkComplete, done.Result);
            Assert.Equal(10_000, await HeldAsync(playerId, ore));
            var view = (await GreatWorksEngine.ViewAsync(db, playerId)).Works.Single(w => w.Region == 1);
            Assert.Equal(5, view.Stage);
            Assert.Equal(0, view.NextCost);
        }

        [Fact]
        public async Task TheDevRestorePutsAMonumentAndItsStockBackExactly()
        {
            const long playerId = 984_000_003L;
            string log = GreatWorksRegistry.MaterialFor(3, GreatWorksRegistry.MaterialLog);
            await SeedAsync(playerId, log, 100);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            await GreatWorksEngine.DevRestoreAsync(db, playerId, 3, 0, 0, GreatWorksRegistry.MaterialLog, 2_000);
            Assert.Equal(2_100, await HeldAsync(playerId, log));
            var result = await GreatWorksEngine.DepositCoreAsync(db, playerId, 3, GreatWorksRegistry.MaterialLog, 1_500, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.GreatWorkDeposited, result.Result);
            Assert.Equal(1_500, (await GreatWorksEngine.ViewAsync(db, playerId)).Works.Single(w => w.Region == 3).Progress);

            await GreatWorksEngine.DevRestoreAsync(db, playerId, 3, 0, 0, GreatWorksRegistry.MaterialLog, -600);
            Assert.Equal(0, await db.GreatWorkProgress.AsNoTracking().CountAsync(g => g.PlayerId == playerId));
            Assert.Equal(0, (await GreatWorksEngine.ViewAsync(db, playerId)).Works.Single(w => w.Region == 3).Progress);
            Assert.Equal(0, await GreatWorksEngine.LoadPackedAsync(db, playerId));
        }

        [Fact]
        public async Task ABuiltMonumentSurvivesARebirth()
        {
            const long playerId = 984_000_004L;
            string log = GreatWorksRegistry.MaterialFor(1, GreatWorksRegistry.MaterialLog);
            await SeedAsync(playerId, log, 80_000);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var built = await GreatWorksEngine.DepositCoreAsync(db, playerId, 1, GreatWorksRegistry.MaterialLog, 0, DateTime.UtcNow);
                Assert.Equal(CommandResultCode.GreatWorkStageBuilt, built.Result);
                await GreatWorksEngine.DepositCoreAsync(db, playerId, 1, GreatWorksRegistry.MaterialLog, 10_000, DateTime.UtcNow);
                db.SeasonalEraRecords.Add(new SeasonalEraRecord { IsActive = true, IsRolloverPaused = true, EndTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600 });
                await db.SaveChangesAsync();
            }

            var outcome = await new RebirthEngine(_fixture.ServiceProvider).RebirthAsync(playerId, expectedRebirthCount: 0);
            Assert.Equal(RebirthResult.Ok, outcome.Result);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            // The materials went with the rebirth (every stack resets) - the stage and the progress did not.
            Assert.Equal(0, await HeldAsync(playerId, log));
            var row = await verify.GreatWorkProgress.AsNoTracking().SingleAsync(g => g.PlayerId == playerId && g.Region == 1);
            Assert.Equal(1, row.Stage);
            Assert.Equal(10_000, row.Progress);
            Assert.Equal(1, GreatWorksRegistry.StageOf(await GreatWorksEngine.LoadPackedAsync(verify, playerId), 1));
        }

        // ---- the readers: online = offline ----------------------------------

        [Fact]
        public void TheGatheringYieldPaysTheBonus_OnTheOneCompositionBothPathsCall()
        {
            Assert.True(ContentRegistry.TryGetGatheringNode(ActivityIdBands.WoodcuttingBand + 1, out GatheringNodeDefinition node));

            TickStatePayload Build(int stages) => new TickStatePayload
            {
                PlayerId = -9_840_001L,
                CachedCodexYieldMultiplier = 1.0f,
                STR = 50, DEX = 50, CON = 50, LCK = 25,
                GreatWorksStagesPacked = stages,
            };

            int all = 0;
            for (int r = 1; r <= 5; r++) all = GreatWorksRegistry.WithStage(all, r, 5);

            var bare = Build(0);
            var maxed = Build(all);
            int before = SimulationEngine.GatheringYieldFor(ref bare, in node, 100).MultiplierPct;
            int after = SimulationEngine.GatheringYieldFor(ref maxed, in node, 100).MultiplierPct;
            _o.WriteLine($"roll-percent bare {before}, all five monuments {after}");
            Assert.Equal(before + GreatWorksRegistry.MaxYieldPct, after);

            // ONE composition: the offline projection asks the same function, so
            // the bonus cannot be paid live and forgotten away (offline = online).
            string offlineSource = File.ReadAllText(FindSource("Engine", "OfflineSimulationEngine.cs"));
            Assert.Contains("SimulationEngine.GatheringYieldFor(", offlineSource, StringComparison.Ordinal);
        }

        [Fact]
        public void TheOfflineCapIsOneRule_AndEverySiteThatNamesACapCallsIt()
        {
            int packed = GreatWorksRegistry.WithStage(0, 2, 4);            // 4 * 15 min
            Assert.Equal(43_200L, OfflineSimulationEngine.EffectiveOfflineCapSeconds(0, 0));
            Assert.Equal(43_200L + 3_600L, OfflineSimulationEngine.EffectiveOfflineCapSeconds(0, packed));
            // Vodnik's 18 h and the monuments STACK: neither is a competing cap.
            Assert.Equal(18 * 3_600L + 3_600L, OfflineSimulationEngine.EffectiveOfflineCapSeconds(25, packed));

            int all = 0;
            for (int r = 1; r <= 5; r++) all = GreatWorksRegistry.WithStage(all, r, 5);
            long maxCap = OfflineSimulationEngine.EffectiveOfflineCapSeconds(25, all);
            Assert.Equal(18 * 3_600L + GreatWorksRegistry.MaxOfflineMinutes * 60L, maxCap);
            Assert.True(maxCap <= 24 * 3_600L, "the offline cap must never reach a full day");

            // The three readers: the window itself, the wire's OfflineCapSeconds and the email.
            foreach (var (folder, file) in new[]
            {
                ("Engine", "OfflineSimulationEngine.cs"), ("Domain/Combat", "SimulationEngine.cs"), ("Engine", "OfflineCapNotifier.cs"),
            })
            {
                string text = File.ReadAllText(FindSource(folder, file));
                Assert.Contains("EffectiveOfflineCapSeconds(", text, StringComparison.Ordinal);
                Assert.DoesNotContain("GetVodnikExtendedOfflineSeconds(payload", text, StringComparison.Ordinal);
            }
        }

        // ---- a writer, not only a reader ------------------------------------

        [Fact]
        public void TheStagesAreWrittenByExactlyTheLoginAndTheDeposit()
        {
            // "Computed but never consumed" has an inverse: consumed everywhere and
            // written by nothing. Two writers put GreatWorksStagesPacked on a
            // payload - the login hydration and the tick's drain of a committed
            // deposit - and this fails if a third appears or either goes.
            string root = Path.GetDirectoryName(FindSource("Domain/Progression", "GreatWorksEngine.cs"))!;
            string server = Path.GetFullPath(Path.Combine(root, "..", ".."));
            var writers = Directory.GetFiles(server, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"GreatWorksStagesPacked\s*="))
                .Select(Path.GetFileName)
                .OrderBy(n => n)
                .ToArray();
            Assert.Equal(new[] { "GreatWorksTickCoordinator.cs", "StateCheckpointManager.cs" }, writers);
        }

        [Fact]
        public void TheOpcodesAndResultCodesAreTheAgreedBlock()
        {
            Assert.Equal(80, (int)CommandType.DepositGreatWork);
            Assert.Equal(60, (int)CommandResultCode.GreatWorkDeposited);
            Assert.Equal(61, (int)CommandResultCode.GreatWorkStageBuilt);
            Assert.Equal(62, (int)CommandResultCode.GreatWorkComplete);
        }

        private static string FindSource(string folder, string file)
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "FolkIdle.Server", folder, file);
                if (File.Exists(candidate)) return candidate;
                string alt = Path.Combine(dir, "server", "FolkIdle.Server", folder, file);
                if (File.Exists(alt)) return alt;
                dir = Path.GetDirectoryName(dir)!;
            }
            throw new FileNotFoundException($"{folder}/{file}");
        }
        // ---- completion rewards --------------------------------------------

        [Fact]
        public async Task CompletingAMonumentGrantsItsBoundFrameOnce()
        {
            const long playerId = 984_000_101L;
            string log = GreatWorksRegistry.MaterialFor(1, GreatWorksRegistry.MaterialLog);
            await SeedAsync(playerId, log, 3_000_000);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            string frame = GreatWorksRegistry.FrameId(1);

            // Stage 4 of 5 built: no frame yet.
            await GreatWorksEngine.DevRestoreAsync(db, playerId, 1, 4, 0, GreatWorksRegistry.MaterialLog, 0);
            Assert.False(await db.CosmeticItems.AsNoTracking().AnyAsync(c => c.PlayerId == playerId && c.DefinitionId == frame));
            Assert.False((await GreatWorksEngine.ViewAsync(db, playerId)).Works.Single(w => w.Region == 1).FrameOwned);

            // The fifth stage completes it, and the frame comes in the same commit.
            var built = await GreatWorksEngine.DepositCoreAsync(db, playerId, 1, GreatWorksRegistry.MaterialLog, 0, DateTime.UtcNow);
            Assert.Equal(CommandResultCode.GreatWorkStageBuilt, built.Result);
            var owned = await db.CosmeticItems.AsNoTracking().Where(c => c.PlayerId == playerId && c.DefinitionId == frame).ToListAsync();
            Assert.Single(owned);
            Assert.Equal((short)CosmeticSource.GreatWork, (short)owned[0].Source);
            Assert.True(CosmeticRegistry.Find(frame)!.Bound);
            Assert.True((await GreatWorksEngine.ViewAsync(db, playerId)).Works.Single(w => w.Region == 1).FrameOwned);

            // A repeat grant is a no-op, never a second frame.
            await GreatWorksEngine.GrantFrameAsync(db, playerId, 1, DateTime.UtcNow);
            Assert.Equal(1, await db.CosmeticItems.AsNoTracking().CountAsync(c => c.PlayerId == playerId && c.DefinitionId == frame));
        }

        [Fact]
        public async Task TheEbonCrownCompleteGivesOneHallSlotAboveTheDiamondCeiling()
        {
            const long playerId = 984_000_102L;
            string log = GreatWorksRegistry.MaterialFor(GreatWorksRegistry.HallSlotRegion, GreatWorksRegistry.MaterialLog);
            await SeedAsync(playerId, log, 1);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            Assert.Equal(0, await GreatWorksEngine.HallSlotsAsync(db, playerId));
            await GreatWorksEngine.DevRestoreAsync(db, playerId, GreatWorksRegistry.HallSlotRegion, GreatWorksRegistry.StageCount, 0, GreatWorksRegistry.MaterialLog, 0);
            int slots = await GreatWorksEngine.HallSlotsAsync(db, playerId);

            Assert.Equal(1, slots);
            Assert.Equal(1, (await GreatWorksEngine.ViewAsync(db, playerId)).HallSlots);
            Assert.Equal(HallOfAncestorsRules.BaseSlots + 1, HallOfAncestorsRules.CapFor(0, slots));
            Assert.Equal(HallOfAncestorsRules.MaxSlots + 1, HallOfAncestorsRules.CapFor(HallOfAncestorsRules.MaxPurchases, slots));
            Assert.Equal(HallOfAncestorsRules.MaxSlots + 1, HallOfAncestorsRules.MaxCapFor(slots));
            // Buying is unchanged: still four to buy, same prices.
            Assert.Equal(0L, HallOfAncestorsRules.NextSlotCostDiamonds(HallOfAncestorsRules.MaxPurchases));
        }
    }
}

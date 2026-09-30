using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 87: the Boss Ascension ladder - its table, its start rule, what the
    /// live tick does with an armed step, and that a clear pays cosmetics and
    /// titles exactly once. What each step COSTS is measured in
    /// BossChallengeCalibrationTests.
    /// </summary>
    [Collection("Postgres collection")]
    public class BossAscensionTests
    {
        private readonly PostgresTestFixture _fixture;

        public BossAscensionTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        // ---- the table ------------------------------------------------------

        [Fact]
        public void TheLadderIsTenStepsEachAddingOneModifier()
        {
            Assert.Equal(10, BossAscensionRegistry.MaxStep);
            Assert.Equal(10, BossAscensionRegistry.Steps.Count);
            for (int i = 0; i < 10; i++) Assert.Equal(i + 1, BossAscensionRegistry.Steps[i].Step);

            var before = BossAscensionRegistry.ModifiersFor(0);
            Assert.True(before.IsNone);
            for (int step = 1; step <= 10; step++)
            {
                var after = BossAscensionRegistry.ModifiersFor(step);
                // Exactly one of the three components moved, and never loosened.
                int moved = (after.AttackPct != before.AttackPct ? 1 : 0)
                    + (after.BossHpPct != before.BossHpPct ? 1 : 0)
                    + (after.TimeLimitPctOfSwift != before.TimeLimitPctOfSwift ? 1 : 0);
                Assert.Equal(1, moved);
                Assert.True(after.AttackPct >= before.AttackPct && after.BossHpPct >= before.BossHpPct);
                Assert.True(before.TimeLimitPctOfSwift == 0 || after.TimeLimitPctOfSwift <= before.TimeLimitPctOfSwift);
                before = after;
            }

            var top = BossAscensionRegistry.ModifiersFor(10);
            Assert.Equal(45, top.AttackPct);
            Assert.Equal(20, top.BossHpPct);
            Assert.Equal(120, top.TimeLimitPctOfSwift);
            Assert.Equal(top, BossAscensionRegistry.ModifiersFor(99));
        }

        [Fact]
        public void EveryStepHasATitleAndTheFrameStepsHaveABoundFrame_AndNothingElse()
        {
            for (int region = 1; region <= 5; region++)
            {
                for (int step = 1; step <= 10; step++)
                {
                    string slug = BossAscensionRegistry.TitleSlug(region, step);
                    var title = TitleRegistry.Find(slug);
                    Assert.NotNull(title);
                    Assert.Equal(BossAscensionRegistry.TitleName(region, step), title!.DisplayName);
                    Assert.Equal(0, title.DeepFloor);
                    Assert.True(slug.Length <= 32, slug);

                    string? frameId = BossAscensionRegistry.RewardFrameIdFor(region, step);
                    Assert.Equal(step == 5 || step == 10, frameId != null);
                    if (frameId != null)
                    {
                        var frame = CosmeticRegistry.Find(frameId);
                        Assert.NotNull(frame);
                        Assert.Equal(CosmeticKind.Frame, frame!.Kind);
                        Assert.True(frame.Bound);
                        Assert.True(frameId.Length <= 48);
                    }
                }
            }

            // Bound frames never come out of a chest, so they cannot be farmed.
            foreach (int rarity in new[] { CosmeticRegistry.Epic, CosmeticRegistry.Legendary })
            {
                Assert.DoesNotContain(CosmeticRegistry.ChestPool(rarity), d => d.Bound);
            }
            Assert.Equal(10, CosmeticRegistry.All.Count(d => d.Bound));

            // The Deep's titles are untouched by the ladder's.
            Assert.Empty(TitleRegistry.ForDeepFloor(0));
            Assert.Equal("deep_10", TitleRegistry.NextDeepTitle(0)!.Slug);
            Assert.Null(TitleRegistry.NextDeepTitle(50));
        }

        [Fact]
        public void ThePackedCacheRoundTripsAndOnlyRises()
        {
            int packed = 0;
            packed = BossAscensionRegistry.WithHighestStep(packed, 3, 7);
            packed = BossAscensionRegistry.WithHighestStep(packed, 5, 10);
            packed = BossAscensionRegistry.WithHighestStep(packed, 3, 4);
            Assert.Equal(7, BossAscensionRegistry.HighestStepOf(packed, 3));
            Assert.Equal(10, BossAscensionRegistry.HighestStepOf(packed, 5));
            Assert.Equal(0, BossAscensionRegistry.HighestStepOf(packed, 1));
            Assert.Equal(0, BossAscensionRegistry.HighestStepOf(packed, 9));
        }

        // ---- the start rule -------------------------------------------------

        private static TickStatePayload Fighter(int region, byte defeatedMask, int packed)
        {
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            var payload = new TickStatePayload
            {
                PlayerId = 1,
                CurrentLevel = 97,
                SelectedLineageId = 1,
                Slot1_CharacterId = Guid.NewGuid(),
                ActiveActivityId = 1,
                DefeatedRegionBossMask = defeatedMask,
                BossAscensionPacked = packed,
                InventorySpaceRemaining = int.MaxValue,
                PlayerHp = 100_000_000,
                TownHallLevel = 1,
            };
            RaceAttributeGrowth.ApplyLevelUpGrowth(ref payload, activeRaceId: 1, levelsGained: 96);
            payload.CachedAffixTotals.FlatAttack = 50_000;
            payload.CachedAffixTotals.FlatDefense = 50_000;
            payload.SetGold(0);
            return payload;
        }

        [Fact]
        public void AStepIsRefusedWithAResultCode_NeverSilently()
        {
            int boss1 = RaceUnlockRegistry.GetRegionBossMonsterId(1);
            byte beaten1 = BossFirstClearRules.MarkDefeated(0, boss1);

            // Never beaten: refused, however low the step.
            var fresh = Fighter(1, 0, 0);
            Assert.Equal(CommandResultCode.AscensionBossNotDefeated, BossAscensionTickCoordinator.Validate(in fresh, 1, 1));

            // Beaten, nothing cleared: step 1 only.
            var p = Fighter(1, beaten1, 0);
            Assert.Equal(CommandResultCode.Success, BossAscensionTickCoordinator.Validate(in p, 1, 1));
            Assert.Equal(CommandResultCode.AscensionStepLocked, BossAscensionTickCoordinator.Validate(in p, 1, 2));
            Assert.Equal(CommandResultCode.AscensionStepLocked, BossAscensionTickCoordinator.Validate(in p, 1, 10));

            // Cleared 3: 1-4 open (a cleared step may be replayed), 5 is not.
            p.BossAscensionPacked = BossAscensionRegistry.WithHighestStep(0, 1, 3);
            foreach (int step in new[] { 1, 2, 3, 4 }) Assert.Equal(CommandResultCode.Success, BossAscensionTickCoordinator.Validate(in p, 1, step));
            Assert.Equal(CommandResultCode.AscensionStepLocked, BossAscensionTickCoordinator.Validate(in p, 1, 5));

            // Each boss has its own ladder: region 1's steps open nothing in region 2.
            byte both = BossFirstClearRules.MarkDefeated(beaten1, RaceUnlockRegistry.GetRegionBossMonsterId(2));
            p.DefeatedRegionBossMask = both;
            Assert.Equal(CommandResultCode.Success, BossAscensionTickCoordinator.Validate(in p, 2, 1));
            Assert.Equal(CommandResultCode.AscensionStepLocked, BossAscensionTickCoordinator.Validate(in p, 2, 2));

            // A step or region that is not on the ladder is a validation failure, not a disconnect.
            Assert.Equal(CommandResultCode.GenericValidationFailure, BossAscensionTickCoordinator.Validate(in p, 1, 0));
            Assert.Equal(CommandResultCode.GenericValidationFailure, BossAscensionTickCoordinator.Validate(in p, 1, 11));
            Assert.Equal(CommandResultCode.GenericValidationFailure, BossAscensionTickCoordinator.Validate(in p, 0, 1));
            Assert.Equal(CommandResultCode.GenericValidationFailure, BossAscensionTickCoordinator.Validate(in p, 6, 1));
        }

        // ---- the live tick --------------------------------------------------

        private static void Arm(ref TickStatePayload p, int region, int step)
        {
            p.AscensionStep = (byte)step;
            p.AscensionRegion = (byte)region;
            p.AscensionCharacterId = p.Slot1_CharacterId;
            p.ActiveActivityId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
        }

        /// <summary>Ticks until the armed step has been judged (a kill), at most a few hundred.</summary>
        private static void TickUntilJudged(ref TickStatePayload p)
        {
            for (int i = 0; i < 2000 && p.AscensionPendingResult == 0; i++) Tick(ref p);
        }

        /// <summary>Ticks until the armed step is cleared (the tick disarms it and notes the clear).</summary>
        private static void TickUntilCleared(ref TickStatePayload p)
        {
            for (int i = 0; i < 2000 && p.AscensionStep != 0; i++) Tick(ref p);
        }

        private static void Tick(ref TickStatePayload p)
        {
            var queue = new ConcurrentQueue<GuildWarPointEvent>();
            var contexts = new ConcurrentDictionary<long, LiveSessionContext>();
            SimulationEngine.ProcessSubTick(ref p, 100, 100, queue, contexts);
            while (queue.TryDequeue(out _)) { }
            while (CodexEngine.KillEventQueue.TryDequeue(out _)) { }
            while (CombatLootEngine.DropRequestQueue.TryDequeue(out _)) { }
            while (CosmeticGrantEngine.Queue.TryDequeue(out _)) { }
            while (CosmeticGrantEngine.BossKills.TryDequeue(out _)) { }
        }

        private static void DrainClears()
        {
            while (BossAscensionEngine.Clears.TryDequeue(out _)) { }
        }

        /// <summary>A boss one hit from death, so the next swing kills it - at a chosen point in the fight's clock.</summary>
        private static TickStatePayload AboutToKill(int region, int step, int fightTicks)
        {
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            var p = Fighter(region, BossFirstClearRules.MarkDefeated(0, bossId), 0);
            Arm(ref p, region, step);
            p.CurrentMonsterId = bossId;
            p.CurrentMonsterHp = 1;
            p.CombatTargetTickAccumulator = fightTicks;
            return p;
        }

        [Fact]
        public void AnArmedBossSpawnsWithTheStepsHealth_AndAnUnarmedOneDoesNot()
        {
            const int region = 1;
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            byte beaten = BossFirstClearRules.MarkDefeated(0, bossId);
            long baseHp = BossFirstClearRules.MaxHpFor(beaten, bossId) * 1000L;

            var plain = Fighter(region, beaten, 0);
            plain.ActiveActivityId = bossId;
            Tick(ref plain);

            var armed = Fighter(region, beaten, 0);
            Arm(ref armed, region, 3); // step 3 is the first with +10% health
            Tick(ref armed);

            var mods = BossAscensionRegistry.ModifiersFor(3);
            Assert.Equal(10, mods.BossHpPct);
            Assert.True(plain.CurrentMonsterHp <= baseHp);
            Assert.True(armed.CurrentMonsterHp > baseHp, $"armed {armed.CurrentMonsterHp} vs base {baseHp}");
            Assert.Equal(BossAscensionRules.ScaleBossHp(baseHp, in mods), armed.CurrentMonsterHp);
        }

        [Fact]
        public void AKillInsideTheLimitClearsTheStep_AndNotesItOnce()
        {
            DrainClears();
            var p = AboutToKill(region: 1, step: 3, fightTicks: 5); // 160 s limit at step 3
            TickUntilCleared(ref p);

            Assert.Equal(0, p.AscensionPendingResult);
            Assert.Equal(0, p.AscensionStep);
            Assert.Equal(3, BossAscensionRegistry.HighestStepOf(p.BossAscensionPacked, 1));
            Assert.True(BossAscensionEngine.Clears.TryDequeue(out var note));
            Assert.Equal(new BossAscensionEngine.ClearNote(1, 1, 3, RaceUnlockRegistry.GetRegionBossMonsterId(1)), note);
            Assert.False(BossAscensionEngine.Clears.TryDequeue(out _));
        }

        [Fact]
        public void AKillPastTheLimitIsAnsweredAndLeavesTheAttemptArmed()
        {
            DrainClears();
            // Step 3 allows 160 s = 1600 ticks; this fight is 5000 ticks old.
            var p = AboutToKill(region: 1, step: 3, fightTicks: 5000);
            TickUntilJudged(ref p);

            Assert.Equal(2, p.AscensionPendingResult);
            Assert.Equal(3, p.AscensionStep);
            Assert.Equal(0, BossAscensionRegistry.HighestStepOf(p.BossAscensionPacked, 1));
            Assert.False(BossAscensionEngine.Clears.TryDequeue(out _));
        }

        [Fact]
        public void AnAttemptEndsWhenTheActivityChanges_AndAKillOutsideItIsOrdinary()
        {
            DrainClears();
            var p = AboutToKill(region: 1, step: 1, fightTicks: 5);
            SimulationEngine.ApplyActivityChangeToPayload(ref p, 1);
            Assert.Equal(0, p.AscensionStep);

            // Another character standing at the same boss is not the one that armed it.
            var q = AboutToKill(region: 1, step: 1, fightTicks: 5);
            q.AscensionCharacterId = Guid.NewGuid();
            Tick(ref q);
            Assert.Equal(0, q.AscensionPendingResult);
            Assert.False(BossAscensionEngine.Clears.TryDequeue(out _));
        }

        [Fact]
        public void ANoLimitStepClearsAtAnyPace()
        {
            DrainClears();
            var p = AboutToKill(region: 1, step: 1, fightTicks: 200_000);
            TickUntilCleared(ref p);
            Assert.Equal(0, p.AscensionStep);
            Assert.True(BossAscensionEngine.Clears.TryDequeue(out _));
            DrainClears();
        }

        // ---- the reward -----------------------------------------------------

        private async Task SeedAsync(long playerId, long guildId = 0)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            if (await db.PlayerRecords.AnyAsync(p => p.Id == playerId)) return;
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), GuildId = guildId,
                Username = $"asc{playerId}",
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000 });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task AClearPaysTitlesAndFramesOnce_AndNothingElse()
        {
            const long playerId = 957000001L;
            await SeedAsync(playerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var first = await BossAscensionEngine.RecordClearAsync(db, playerId, 2, 1, DateTime.UtcNow);
            Assert.Single(first);
            Assert.Equal("ascension_r2_s1", first[0].TitleSlug);
            Assert.Null(first[0].FrameId);

            // The same clear again - a replayed note, a second tab - pays nothing.
            Assert.Empty(await BossAscensionEngine.RecordClearAsync(db, playerId, 2, 1, DateTime.UtcNow));

            // Step 5 pays its title and its frame; a lost note for 2-4 is paid with it.
            var jump = await BossAscensionEngine.RecordClearAsync(db, playerId, 2, 5, DateTime.UtcNow);
            Assert.Equal(new[] { 2, 3, 4, 5 }, jump.Select(r => r.Step));
            Assert.Equal("frame_ascent_r2_s5", jump.Single(r => r.Step == 5).FrameId);
            Assert.Empty(await BossAscensionEngine.RecordClearAsync(db, playerId, 2, 4, DateTime.UtcNow));

            var titles = await TitleEngine.ListAsync(db, playerId);
            Assert.Equal(5, titles.Count);
            Assert.All(titles, t => Assert.StartsWith("ascension_r2_", t.Slug));

            var cosmetics = await db.CosmeticItems.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync();
            var frame = Assert.Single(cosmetics);
            Assert.Equal("frame_ascent_r2_s5", frame.DefinitionId);
            Assert.Equal((byte)CosmeticSource.Ascension, frame.Source);
            Assert.Equal((byte)CosmeticKind.Frame, frame.Kind);

            // No gold, no diamonds, no chests, no gear - the reward is cosmetic and title only.
            Assert.Equal(1_000, (await db.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold")).Quantity);
            Assert.Equal(0, (await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId)).PremiumDiamonds);
            Assert.DoesNotContain(cosmetics, c => c.Kind == (byte)CosmeticKind.Chest);

            // Persisted: the highest step per boss, only ever raised, and packed for the tick.
            Assert.Equal(5, (await db.BossAscensionProgress.AsNoTracking().SingleAsync(b => b.PlayerId == playerId && b.Region == 2)).HighestStep);
            int packed = await BossAscensionEngine.LoadPackedAsync(db, playerId);
            Assert.Equal(5, BossAscensionRegistry.HighestStepOf(packed, 2));
            Assert.Equal(0, BossAscensionRegistry.HighestStepOf(packed, 1));

            // Nonsense is ignored rather than written.
            Assert.Empty(await BossAscensionEngine.RecordClearAsync(db, playerId, 9, 1, DateTime.UtcNow));
            Assert.Empty(await BossAscensionEngine.RecordClearAsync(db, playerId, 2, 11, DateTime.UtcNow));
        }

        [Fact]
        public async Task TheDevRestorePutsALadderBackExactly()
        {
            const long playerId = 957000004L;
            await SeedAsync(playerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await BossAscensionEngine.RecordClearAsync(db, playerId, 1, 2, DateTime.UtcNow);
            await BossAscensionEngine.RecordClearAsync(db, playerId, 1, 6, DateTime.UtcNow);
            Assert.Single(await db.CosmeticItems.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync());

            await BossAscensionEngine.DevRestoreAsync(db, playerId, 1, 2);

            Assert.Equal(2, BossAscensionRegistry.HighestStepOf(await BossAscensionEngine.LoadPackedAsync(db, playerId), 1));
            Assert.Equal(new[] { "ascension_r1_s1", "ascension_r1_s2" },
                (await TitleEngine.ListAsync(db, playerId)).Select(t => t.Slug).OrderBy(s => s));
            Assert.Empty(await db.CosmeticItems.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync());

            // The boss can be marked beaten for a run and un-marked after it.
            int boss1 = RaceUnlockRegistry.GetRegionBossMonsterId(1);
            await BossAscensionEngine.DevRestoreAsync(db, playerId, 1, 2, bossDefeated: true);
            Assert.True((await BossAscensionEngine.ViewAsync(db, playerId)).Single(b => b.Region == 1).BossDefeated);
            await BossAscensionEngine.DevRestoreAsync(db, playerId, 1, 2, bossDefeated: false);
            Assert.False(await db.MonsterCodexEntries.AsNoTracking().AnyAsync(c => c.PlayerId == playerId && c.MonsterId == boss1));

            // Back to nothing, and the ladder pays again from the start.
            await BossAscensionEngine.DevRestoreAsync(db, playerId, 1, 0);
            Assert.Empty(await TitleEngine.ListAsync(db, playerId));
            Assert.Single(await BossAscensionEngine.RecordClearAsync(db, playerId, 1, 1, DateTime.UtcNow));
        }

        [Fact]
        public async Task TheLadderViewSaysWhatIsClearedAndWhatCanBeStarted()
        {
            const long playerId = 957000002L;
            await SeedAsync(playerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            // Beaten region 1's boss, never region 2's.
            db.MonsterCodexEntries.Add(new MonsterCodexEntry
            {
                PlayerId = playerId, MonsterId = RaceUnlockRegistry.GetRegionBossMonsterId(1), KillCount = 1,
            });
            await db.SaveChangesAsync();
            await BossAscensionEngine.RecordClearAsync(db, playerId, 1, 2, DateTime.UtcNow);

            var view = await BossAscensionEngine.ViewAsync(db, playerId);
            Assert.Equal(5, view.Count);
            var r1 = view.Single(b => b.Region == 1);
            Assert.True(r1.BossDefeated);
            Assert.Equal(2, r1.HighestStep);
            Assert.Equal(3, r1.NextStep);
            Assert.Equal(new[] { "Boss attack +15%", "Boss health +10%", "Kill it within 160 s" }, r1.Steps[2].Effects);
            Assert.Equal(new[] { true, true, false }, r1.Steps.Take(3).Select(s => s.Cleared));
            Assert.Equal(3, r1.Steps.Count(s => s.Startable));
            Assert.Equal("Wolfbane V", r1.Steps[4].RewardTitle);
            Assert.NotNull(r1.Steps[4].RewardFrame);
            Assert.Null(r1.Steps[3].RewardFrame);

            var r2 = view.Single(b => b.Region == 2);
            Assert.False(r2.BossDefeated);
            Assert.Empty(r2.Steps.Where(s => s.Startable));
        }

        [Fact]
        public async Task ABoundFrameCannotBeSold()
        {
            const long seller = 957000003L;
            await SeedAsync(seller, guildId: 9570);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await BossAscensionEngine.RecordClearAsync(db, seller, 1, 5, DateTime.UtcNow);
            long frameRow = (await db.CosmeticItems.AsNoTracking().SingleAsync(c => c.PlayerId == seller)).Id;

            Assert.Equal(CosmeticMarketResult.Bound, await CosmeticMarketEngine.ListAsync(db, seller, frameRow, 1_000_000, DateTime.UtcNow));
            Assert.False((await db.CosmeticItems.AsNoTracking().SingleAsync(c => c.Id == frameRow)).IsListed);

            // It is still theirs to wear.
            Assert.Equal(CosmeticResult.Ok, await CosmeticEngine.EquipAsync(db, seller, CosmeticKind.Frame, "frame_ascent_r1_s5"));
        }

        // ---- the wire -------------------------------------------------------

        [Fact]
        public void TheResultCodesAreStable()
        {
            Assert.Equal(46, (int)CommandResultCode.AscensionBossNotDefeated);
            Assert.Equal(47, (int)CommandResultCode.AscensionStepLocked);
            Assert.Equal(48, (int)CommandResultCode.AscensionStepCleared);
            Assert.Equal(49, (int)CommandResultCode.AscensionTooSlow);
            Assert.Equal(50, (int)CommandResultCode.AscensionRewardNotSaved);
            Assert.Equal(79, (int)CommandType.StartBossAscension);
        }
    }
}

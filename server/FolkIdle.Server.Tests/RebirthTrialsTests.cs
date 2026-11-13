using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>Rebirth Trials (2026-11-13): the rules, the shared hooks, and completion.</summary>
    public class RebirthTrialsRuleTests
    {
        private static TickStatePayload Payload(int active = 0, int completed = 0)
        {
            var p = new TickStatePayload { CurrentLevel = 30, STR = 10, DEX = 10, CON = 10, LCK = 10 };
            p.ActiveRebirthTrial = active;
            p.CompletedRebirthTrials = completed;
            return p;
        }

        [Fact]
        public void EveryTrialHasARuleARewardAndATitle_AndTheRewardsStayBounded()
        {
            Assert.Equal(4, RebirthTrials.All.Count);
            Assert.Equal(RebirthTrials.All.Count, RebirthTrials.All.Select(t => t.Id).Distinct().Count());
            foreach (var t in RebirthTrials.All)
            {
                Assert.NotNull(TitleRegistry.Find(t.TitleSlug));
                Assert.False(string.IsNullOrWhiteSpace(t.Rule));
                Assert.False(string.IsNullOrWhiteSpace(t.Reward));
            }
            int all = RebirthTrials.All.Aggregate(0, (m, t) => m | RebirthTrials.BitOf(t.Id));
            Assert.Equal(5, RebirthTrials.GoldPctReward(all));
            Assert.Equal(5, RebirthTrials.XpPctReward(all));
            Assert.Equal(5, RebirthTrials.MaxHpPctReward(all));
            Assert.Equal(2, RebirthTrials.DamagePctReward(all));
        }

        [Fact]
        public void ALifeRecordKeepsTheFastest_AndNeedsTheLevel()
        {
            Assert.Equal(500, RebirthRules.LifeRecord(0, 500, 50, 50));
            Assert.Equal(400, RebirthRules.LifeRecord(400, 500, 60, 50));
            Assert.Equal(300, RebirthRules.LifeRecord(400, 300, 60, 50));
            Assert.Equal(400, RebirthRules.LifeRecord(400, 300, 99, 100));
            Assert.Equal(0, RebirthRules.LifeRecord(0, 0, 60, 50));
        }

        [Fact]
        public void AFinishedTrialCannotBeChosenAgain()
        {
            int done = RebirthTrials.BitOf(RebirthTrials.Want);
            Assert.False(RebirthTrials.IsChoosable(RebirthTrials.Want, done));
            Assert.True(RebirthTrials.IsChoosable(RebirthTrials.SlowHand, done));
            Assert.False(RebirthTrials.IsChoosable(99, 0));
        }

        [Fact]
        public void TheSlowHandCutsTheWholeXpMultiplierByForty()
        {
            var plain = Payload();
            var trial = Payload(active: RebirthTrials.SlowHand);
            int without = SimulationEngine.LiveKillXpMultiplierPct(in plain, 100);
            int with = SimulationEngine.LiveKillXpMultiplierPct(in trial, 100);
            Assert.Equal(without * 60 / 100, with);

            var rewarded = Payload(completed: RebirthTrials.BitOf(RebirthTrials.SlowHand));
            Assert.Equal(without + 5, SimulationEngine.LiveKillXpMultiplierPct(in rewarded, 100));
        }

        [Fact]
        public void WantHalvesKillGold_AndItsRewardAddsFive()
        {
            var plain = Payload();
            var want = Payload(active: RebirthTrials.Want);
            var done = Payload(completed: RebirthTrials.BitOf(RebirthTrials.Want));
            Assert.Equal(1.0f, CombatGoldReward.FactorsFor(in plain, 0f).Trial);
            Assert.Equal(0.5f, CombatGoldReward.FactorsFor(in want, 0f).Trial, 3);
            Assert.Equal(1.05f, CombatGoldReward.FactorsFor(in done, 0f).Trial, 3);
        }

        [Fact]
        public void FrailtyCutsHealth_AndItsRewardRaisesIt()
        {
            var plain = Payload();
            var stats = SimulationEngine.LiveCombatStats(in plain);
            long baseHp = SimulationEngine.EffectiveMaxMilliHpFor(in plain, in stats);

            var frail = Payload(active: RebirthTrials.Frailty);
            var frailStats = SimulationEngine.LiveCombatStats(in frail);
            long frailHp = SimulationEngine.EffectiveMaxMilliHpFor(in frail, in frailStats);
            Assert.InRange(frailHp, baseHp * 74 / 100, baseHp * 76 / 100);

            var done = Payload(completed: RebirthTrials.BitOf(RebirthTrials.Frailty));
            var doneStats = SimulationEngine.LiveCombatStats(in done);
            long doneHp = SimulationEngine.EffectiveMaxMilliHpFor(in done, in doneStats);
            Assert.InRange(doneHp, baseHp * 104 / 100, baseHp * 106 / 100);
        }
    }

    [Collection("Postgres collection")]
    public class RebirthTrialsIntegrationTests
    {
        private readonly PostgresTestFixture _fixture;

        public RebirthTrialsIntegrationTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task SeedAsync(long id, int level)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var guid = Guid.NewGuid();
            db.PlayerRecords.Add(new PlayerRecord { Id = id, PlayerGuid = guid, AuthenticatorToken = Guid.NewGuid(), CurrentLevel = level });
            db.CharacterRecords.Add(new CharacterRecord { Id = guid, PlayerId = id, AgePhase = 1, AgeTicks = AgePhaseCurve.ChildEndTicks, SlotIndex = 0 });
            db.CharacterLineages.Add(new CharacterLineageRegistry { CharacterId = guid, GenerationIndex = 0, GeneticVector = RaceIds.Human });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = id, ItemId = "gold", Quantity = 1_000L });
            await db.SaveChangesAsync();
        }

        private async Task<PlayerRecord> ReadAsync(long id)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == id);
        }

        private async Task SetLevelAsync(long id, int level)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerRecords\" SET \"CurrentLevel\" = {1} WHERE \"Id\" = {0}", id, level);
        }

        [Fact]
        public async Task ATrialChosenAtRebirth_IsFinishedByTheNextOne()
        {
            const long id = 960_114_001L;
            await SeedAsync(id, 60);
            var engine = new RebirthEngine(_fixture.ServiceProvider);

            var first = await engine.RebirthAsync(id, 0, null, default, RebirthTrials.Want);
            Assert.Equal(RebirthResult.Ok, first.Result);
            var during = await ReadAsync(id);
            Assert.Equal(RebirthTrials.Want, during.ActiveRebirthTrial);
            Assert.Equal(0, during.CompletedRebirthTrials);

            Assert.True(during.LifeStartedAtEpoch > 0, "a rebirth starts the life clock");
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // An hour into this life.
                await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerRecords\" SET \"LifeStartedAtEpoch\" = \"LifeStartedAtEpoch\" - 3600 WHERE \"Id\" = {0}", id);
            }
            await SetLevelAsync(id, 55);
            var second = await engine.RebirthAsync(id, 1);
            Assert.Equal(RebirthResult.Ok, second.Result);
            var after = await ReadAsync(id);
            Assert.Equal(RebirthTrials.None, after.ActiveRebirthTrial);
            Assert.True(RebirthTrials.IsCompleted(after.CompletedRebirthTrials, RebirthTrials.Want));
            // The life record: about an hour to a level-55 rebirth, none to 100.
            Assert.InRange(after.BestLifeSecondsTo50, 3600, 3700);
            Assert.Equal(0, after.BestLifeSecondsTo100);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.True(await verify.PlayerTitles.AnyAsync(t => t.PlayerId == id && t.TitleSlug == "trial_want"));

            // And it cannot be taken again.
            await SetLevelAsync(id, 55);
            var again = await engine.RebirthAsync(id, 2, null, default, RebirthTrials.Want);
            Assert.Equal(RebirthResult.TrialUnavailable, again.Result);
        }

        [Fact]
        public async Task TheTrialOfIronRefusesAVault_BeforeAnythingIsReset()
        {
            const long id = 960_114_002L;
            await SeedAsync(id, 60);
            long pieceId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var piece = new EquipmentInstance { PlayerId = id, BaseItemId = "test_sword", QualityTier = 1, AffixPayload = "{}" };
                db.EquipmentInstances.Add(piece);
                await db.SaveChangesAsync();
                pieceId = piece.Id;
            }
            var engine = new RebirthEngine(_fixture.ServiceProvider);

            var outcome = await engine.RebirthAsync(id, 0, new[] { pieceId }, default, RebirthTrials.Iron);
            Assert.Equal(RebirthResult.TrialForbidsVault, outcome.Result);
            var player = await ReadAsync(id);
            Assert.Equal(0, player.RebirthCount);
            Assert.Equal(60, player.CurrentLevel);
        }
    }
}

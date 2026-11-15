using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Daily missions against Postgres: the day is drawn once and kept, acts
    /// reach the counters through the worker's upsert, claims bank XP exactly
    /// once, and the reroll is one a day. Progress and XP are read back from
    /// the tables rather than trusted from the result.
    /// </summary>
    [Collection("Postgres collection")]
    public class DailyMissionTests
    {
        private readonly PostgresTestFixture _fixture;
        public DailyMissionTests(PostgresTestFixture fixture) => _fixture = fixture;

        private static readonly DateTime Noon = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

        private static readonly MissionFacts Veteran = new()
        {
            Level = 100,
            Gold = 1_000_000_000,
            HasGuild = true,
            BreedingGroundsLevel = 5,
            HasExpeditionCandidate = true,
            FeastPossible = true,
            UnspentAttributePoints = 3,
            AvailableSkillPoints = 2,
            LootChests = 4,
            WardrobeChests = 1,
            DefeatedRegionBossMask = 0b11111,
            DungeonsOpen = true,
            ReachedDeep = true,
            DeepEnabled = true,
            WorldBossActive = true,
        };

        private async Task<long> SeedPlayerAsync()
        {
            string tag = Guid.NewGuid().ToString("N");
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Email = $"missions_{tag}@example.com",
                Username = "Mission" + tag[..8],
                DeviceId = "missions-device-" + tag,
            };
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        private static Func<Task<MissionFacts?>> Facts(MissionFacts f) => () => Task.FromResult<MissionFacts?>(f);

        /// <summary>Does what MissionProgressWorker does with a batch of acts.</summary>
        private async Task RecordAsync(long playerId, MissionAction action, int amount = 1, short detail = 0, DateTime? at = null)
        {
            int day = MissionEngine.DayKeyOf(at ?? Noon);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await MissionProgressWorker.UpsertAsync(db, new Dictionary<(long, int, short, short), long>
            {
                [(playerId, day, (short)action, detail)] = amount,
            });
        }

        private async Task<MissionEngine.DayView> ViewAsync(long playerId, MissionFacts facts, DateTime? at = null)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return (await MissionEngine.ViewAsync(db, playerId, Facts(facts), at ?? Noon))!;
        }

        [Fact]
        public async Task TheDayIsDrawnOnce_AndKeptWhenTheFactsChange()
        {
            long playerId = await SeedPlayerAsync();
            var first = await ViewAsync(playerId, Veteran);
            Assert.Equal(MissionRegistry.MissionsPerDay, first.Missions.Count);
            Assert.Contains(first.Missions, m => m.Action == nameof(MissionAction.WorldBossStrike));

            // The boss dies at 13:00. Today's missions do not move.
            var later = await ViewAsync(playerId, Veteran with { WorldBossActive = false }, Noon.AddHours(1));
            Assert.Equal(first.Missions.Select(m => m.Action), later.Missions.Select(m => m.Action));

            // Tomorrow draws again, from tomorrow's facts.
            var tomorrow = await ViewAsync(playerId, Veteran with { WorldBossActive = false }, Noon.AddDays(1));
            Assert.DoesNotContain(tomorrow.Missions, m => m.Action == nameof(MissionAction.WorldBossStrike));
        }

        [Fact]
        public async Task TheDungeonMissionCountsDistinctGates_NotRuns()
        {
            long playerId = await SeedPlayerAsync();
            await ViewAsync(playerId, Veteran);

            await RecordAsync(playerId, MissionAction.DungeonFight, 5, detail: 0);
            var oneGate = await ViewAsync(playerId, Veteran);
            var dungeon = oneGate.Missions.Single(m => m.Action == nameof(MissionAction.DungeonFight));
            Assert.Equal(1, dungeon.Progress);
            Assert.False(dungeon.Done);

            await RecordAsync(playerId, MissionAction.DungeonFight, 1, detail: 1);
            await RecordAsync(playerId, MissionAction.DungeonFight, 1, detail: 2);
            var allGates = await ViewAsync(playerId, Veteran);
            Assert.True(allGates.Missions.Single(m => m.Action == nameof(MissionAction.DungeonFight)).Done);
        }

        [Fact]
        public async Task AnActBeforeMidnightCountsForThatDay_NotTheNext()
        {
            long playerId = await SeedPlayerAsync();
            await ViewAsync(playerId, Veteran);
            await RecordAsync(playerId, MissionAction.ExpeditionSent, at: Noon.Date.AddDays(1).AddSeconds(-1));
            var today = await ViewAsync(playerId, Veteran);
            Assert.True(today.Missions.Single(m => m.Action == nameof(MissionAction.ExpeditionSent)).Done);
            var tomorrow = await ViewAsync(playerId, Veteran, Noon.AddDays(1));
            Assert.False(tomorrow.Missions.Single(m => m.Action == nameof(MissionAction.ExpeditionSent)).Done);
        }

        [Fact]
        public async Task ClaimBanksXpOnce_AndTheBonusOnlyWhenAllAreDone()
        {
            long playerId = await SeedPlayerAsync();
            var day = await ViewAsync(playerId, Veteran);

            // Nothing done: nothing to claim, and nothing banked.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, xp, _) = await MissionEngine.ClaimAsync(db, playerId, Facts(Veteran), Noon);
                Assert.Equal(MissionEngine.ClaimOutcome.NothingToClaim, outcome);
                Assert.Equal(0, xp);
            }

            // Finish two missions.
            await RecordAsync(playerId, MissionAction.ExpeditionSent);
            await RecordAsync(playerId, MissionAction.WorldBossStrike);
            Assert.Equal(2, (await ViewAsync(playerId, Veteran)).Claimable);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, xp, view) = await MissionEngine.ClaimAsync(db, playerId, Facts(Veteran), Noon);
                Assert.Equal(MissionEngine.ClaimOutcome.Ok, outcome);
                Assert.Equal(2 * MissionRegistry.XpPerMission, xp);
                Assert.Equal(0, view!.Claimable);
            }

            // A second claim pays nothing.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, xp, _) = await MissionEngine.ClaimAsync(db, playerId, Facts(Veteran), Noon);
                Assert.Equal(MissionEngine.ClaimOutcome.NothingToClaim, outcome);
                Assert.Equal(0, xp);
            }

            // Finish everything: the rest plus the bonus.
            foreach (var m in day.Missions)
            {
                var action = Enum.Parse<MissionAction>(m.Action);
                if (action == MissionAction.DungeonFight)
                {
                    for (short g = 0; g < 3; g++) await RecordAsync(playerId, action, detail: g);
                }
                else
                {
                    await RecordAsync(playerId, action, m.Target);
                }
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, xp, view) = await MissionEngine.ClaimAsync(db, playerId, Facts(Veteran), Noon);
                Assert.Equal(MissionEngine.ClaimOutcome.Ok, outcome);
                Assert.Equal(4 * MissionRegistry.XpPerMission + MissionRegistry.XpAllDoneBonus, xp);
                Assert.True(view!.BonusClaimed);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                int dayKey = MissionEngine.DayKeyOf(Noon);
                int banked = await MissionEngine.XpBetweenAsync(db, playerId, dayKey, dayKey);
                Assert.Equal(200, banked);
            }
        }

        [Fact]
        public async Task TheRerollSwapsOneRotatingMission_OnceADay()
        {
            long playerId = await SeedPlayerAsync();
            var day = await ViewAsync(playerId, Veteran);
            var rotating = day.Missions.First(m => !m.Fixed);
            var fixedOne = day.Missions.First(m => m.Fixed);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, _) = await MissionEngine.RerollAsync(db, playerId, fixedOne.Slot, Facts(Veteran), Noon);
                Assert.Equal(MissionEngine.RerollOutcome.NotRerollable, outcome);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, view) = await MissionEngine.RerollAsync(db, playerId, rotating.Slot, Facts(Veteran), Noon);
                Assert.Equal(MissionEngine.RerollOutcome.Ok, outcome);
                var swapped = view!.Missions[rotating.Slot];
                Assert.NotEqual(rotating.Action, swapped.Action);
                Assert.Equal(MissionRegistry.MissionsPerDay, view.Missions.Select(m => m.Action).Distinct().Count());
                Assert.Equal(0, view.RerollsLeft);
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (outcome, _) = await MissionEngine.RerollAsync(db, playerId, rotating.Slot, Facts(Veteran), Noon);
                Assert.Equal(MissionEngine.RerollOutcome.NoRerollsLeft, outcome);
            }

            // The swap is stored, not recomputed.
            var reread = await ViewAsync(playerId, Veteran);
            Assert.NotEqual(rotating.Action, reread.Missions[rotating.Slot].Action);
        }

        [Fact]
        public async Task TheWorkerDrainsTheQueueIntoTheCounters()
        {
            long playerId = await SeedPlayerAsync();
            var worker = new MissionProgressWorker(_fixture.ServiceProvider);

            MissionEvents.Record(playerId, MissionAction.ChatMessage);
            MissionEvents.Record(playerId, MissionAction.ChatMessage);
            MissionEvents.Record(playerId, MissionAction.LootChestOpened, 3);
            for (int cycle = 0; cycle < 5 && await worker.DrainOneCycleAsync() > 0; cycle++) { }

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await db.PlayerMissionCounters.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync();
            Assert.Equal(2, rows.Single(r => r.Action == (short)MissionAction.ChatMessage).Count);
            Assert.Equal(3, rows.Single(r => r.Action == (short)MissionAction.LootChestOpened).Count);
        }
    }
}

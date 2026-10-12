using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Chernobog's Sunday payout (spec 2026-10-13, section 3): the week's top
    /// three by damage get a title that moves every week and gold scaled to
    /// their income; if the registered players brought the boss down, each of
    /// them gets the kill reward - and a sandbagger a quarter of its gold, no
    /// chest, a penalty row and a line from Chernobog. A developer's window pays
    /// nothing.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossPayoutTests
    {
        private readonly PostgresTestFixture _fixture;

        public WorldBossPayoutTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<long[]> PlayersAsync(int count)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var players = Enumerable.Range(0, count).Select(i => new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"boss_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 20,
            }).ToList();
            db.PlayerRecords.AddRange(players);
            await db.SaveChangesAsync();
            return players.Select(p => p.Id).ToArray();
        }

        /// <summary>Gives each player a strike row with the given damage, as landed strikes would.</summary>
        private async Task DealAsync(long[] players, long[] damage, bool registered = false,
            (double RegSum, int RegPlayed, double LaterSum, int LaterPlayed)? skill = null)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long today = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            for (int i = 0; i < players.Length; i++)
            {
                db.PlayerWorldBossAttempts.Add(new PlayerWorldBossAttempt
                {
                    PlayerId = players[i],
                    BossInstanceId = WorldBossEngine.ActiveBossInstanceId,
                    AttemptCount = 1,
                    TotalInflictedDamage = damage[i],
                    AttemptDateKey = today,
                    IsRegistered = registered,
                    RegistrationDamage = registered ? damage[i] : 0,
                    RegistrationStrikes = registered ? 1 : 0,
                    RegistrationSkillSum = skill?.RegSum ?? 0,
                    RegistrationPlayed = skill?.RegPlayed ?? 0,
                    LaterSkillSum = skill?.LaterSum ?? 0,
                    LaterPlayed = skill?.LaterPlayed ?? 0,
                });
            }
            await db.SaveChangesAsync();
        }

        private async Task<MailboxInstance[]> MailAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.MailboxInstances.AsNoTracking()
                .Where(m => m.PlayerId == playerId && m.SenderName == WorldBossRewards.SenderName)
                .ToArrayAsync();
        }

        private async Task<string[]> TitlesAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerTitles.AsNoTracking().Where(t => t.PlayerId == playerId).Select(t => t.TitleSlug).ToArrayAsync();
        }

        private async Task<int> ChestsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CosmeticItems.AsNoTracking()
                .CountAsync(c => c.PlayerId == playerId && c.Kind == (byte)CosmeticKind.Chest && c.Source == (byte)CosmeticSource.WorldBoss);
        }

        private async Task<WorldBossEngine> OpenAsync()
        {
            var engine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
            return engine;
        }

        private async Task MarkDefeatedAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"WorldBossSnapshots\" SET \"DefeatedAtEpoch\" = {0}, \"HpRevealed\" = 1 WHERE \"BossInstanceId\" = {1}",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), (long)WorldBossEngine.ActiveBossInstanceId);
        }

        [Fact]
        public async Task TheTopThreeArePaidByRankAndWearTheirPlace_Once()
        {
            var engine = await OpenAsync();
            var players = await PlayersAsync(4);
            await DealAsync(players, new long[] { 9_000, 1_000, 4_000, 2_000 });

            await engine.FinalizeEventAsFailedAsync();

            // Ranked 0, 2, 3, 1. No income on record, so every scaled reward is the floor.
            Assert.Contains(WorldBossRewards.FirstPlaceSlug, await TitlesAsync(players[0]));
            Assert.Contains(WorldBossRewards.SecondPlaceSlug, await TitlesAsync(players[2]));
            Assert.Contains(WorldBossRewards.ThirdPlaceSlug, await TitlesAsync(players[3]));
            Assert.Empty(await TitlesAsync(players[1]));
            Assert.Equal(GoldRewardScale.FloorGold, Assert.Single(await MailAsync(players[0])).GoldAttachment);
            Assert.Empty(await MailAsync(players[1]));

            // Worn at once by a player who wore nothing.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var worn = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == players[0]).Select(p => p.ActiveTitleSlug).SingleAsync();
                Assert.Equal(WorldBossRewards.FirstPlaceSlug, worn);
            }

            // No kill this week: nobody gets a chest.
            Assert.Equal(0, await ChestsAsync(players[0]));

            // Once only: a second close finds the encounter already concluded.
            await engine.FinalizeEventAsFailedAsync();
            Assert.Single(await MailAsync(players[0]));
        }

        [Fact]
        public async Task LastWeeksPlaceTitlesMoveToThisWeeksTopThree()
        {
            var engine = await OpenAsync();
            var players = await PlayersAsync(4);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await TitleEngine.GrantAsync(db, players[3], WorldBossRewards.FirstPlaceSlug, DateTime.UtcNow);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"PlayerRecords\" SET \"ActiveTitleSlug\" = {0} WHERE \"Id\" = {1}", WorldBossRewards.FirstPlaceSlug, players[3]);
            }
            await DealAsync(players.Take(3).ToArray(), new long[] { 5_000, 4_000, 3_000 });

            await engine.FinalizeEventAsFailedAsync();

            Assert.Empty(await TitlesAsync(players[3]));
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Null(await verify.PlayerRecords.AsNoTracking().Where(p => p.Id == players[3]).Select(p => p.ActiveTitleSlug).SingleAsync());
            Assert.Equal(1, await verify.PlayerTitles.CountAsync(t => t.TitleSlug == WorldBossRewards.FirstPlaceSlug));
        }

        [Fact]
        public async Task AFallenBossPaysEveryRegisteredPlayerGoldAndALegendaryChest_AndNoLatecomer()
        {
            var engine = await OpenAsync();
            var registered = await PlayersAsync(2);
            var latecomer = await PlayersAsync(1);
            await DealAsync(registered, new long[] { 5_000, 4_000 }, registered: true);
            await DealAsync(latecomer, new long[] { 50_000 });
            await MarkDefeatedAsync();

            await engine.FinalizeEventAsFailedAsync();

            foreach (var id in registered)
            {
                Assert.Equal(1, await ChestsAsync(id));
                Assert.Contains(await MailAsync(id), m => m.MessageText!.Contains("back into Svarog's chest"));
            }
            // The latecomer topped the board, so they are paid for their place -
            // but never the kill reward.
            Assert.Equal(0, await ChestsAsync(latecomer[0]));
            Assert.DoesNotContain(await MailAsync(latecomer[0]), m => m.MessageText!.Contains("back into Svarog's chest"));
            Assert.Contains(WorldBossRewards.FirstPlaceSlug, await TitlesAsync(latecomer[0]));
        }

        [Fact]
        public async Task ASandbaggerWithAProvenPastGetsAQuarterNoChestAPenaltyAndATaunt()
        {
            await OpenAsync();
            var players = await PlayersAsync(2);
            long cheat = players[0], learner = players[1];

            // Both threw everything away on Monday and Tuesday and played well
            // from Wednesday. Only the cheat had shown the skill in an earlier week.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.WorldBossProfiles.Add(new WorldBossProfile { PlayerId = cheat, ProvenSkillAtUtc = DateTime.UtcNow.AddDays(-7) });
                await db.SaveChangesAsync();
            }
            await DealAsync(players, new long[] { 9_000, 9_000 }, registered: true, skill: (0.0, 2, 4.0, 4));
            await MarkDefeatedAsync();

            // The payout itself, so its taunts can be read off the result: the
            // chat queue is static, and in the full suite another test's dispatch
            // worker drains it into ITS OWN database (the static-queue trap).
            WeekPayout payout;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var snapshot = await db.WorldBossSnapshots.SingleAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
                payout = await WorldBossLedger.PayWeekAsync(db, snapshot, DateTime.UtcNow, new Random(1));
            }

            long full = GoldRewardScale.ForMinutes(0, WorldBossRewards.KillGoldHours * 60);
            var cheatKill = Assert.Single(await MailAsync(cheat), m => m.MessageText!.Contains("held back"));
            Assert.Equal(full * WorldBossRewards.SandbaggerKillSharePct / 100, cheatKill.GoldAttachment);
            Assert.Equal(0, await ChestsAsync(cheat));

            // The learner had no skilled week before this one: never flagged.
            Assert.Equal(1, await ChestsAsync(learner));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var penalty = await db.AccountPenalties.AsNoTracking().SingleAsync(p => p.PlayerId == cheat);
                Assert.Equal(PenaltySource.WorldBossSandbag, penalty.Source);
                Assert.False(await db.AccountPenalties.AnyAsync(p => p.PlayerId == learner));

                // And this week, the learner showed the skill - remembered for next week.
                Assert.NotNull((await db.WorldBossProfiles.AsNoTracking().SingleAsync(p => p.PlayerId == learner)).ProvenSkillAtUtc);
            }

            // Chernobog names the cheat - and only the cheat - in World chat.
            string cheatName;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                cheatName = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == cheat).Select(p => p.Username!).SingleAsync();
            }
            Assert.Contains(cheatName, Assert.Single(payout.Taunts));
        }

        [Fact]
        public async Task ADevelopersWindowPaysNothingWhenItCloses()
        {
            var engine = await OpenAsync();
            var players = await PlayersAsync(2);
            await DealAsync(players, new long[] { 5_000, 3_000 });

            await engine.CloseManualWindowAsync();

            Assert.Empty(await MailAsync(players[0]));
            Assert.Empty(await TitlesAsync(players[0]));
        }

        [Fact]
        public async Task TheBoardRanksFromTheSameRowsAndShowsThePhaseAndTheRewards()
        {
            await OpenAsync();
            var players = await PlayersAsync(3);
            await DealAsync(players, new long[] { 2_000, 7_000, 1_000 });

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var view = await WorldBossBoard.ViewAsync(db, players[2]);

            Assert.Equal(10_000, view.TotalDamage);
            Assert.Equal(3, view.Participants);
            Assert.Equal(new[] { players[1], players[0], players[2] }, view.Top.Select(r => r.PlayerId));
            Assert.Equal(new[] { 1, 2, 3 }, view.Top.Select(r => r.Rank));
            Assert.All(view.Top, r => Assert.StartsWith("boss_", r.Name));
            Assert.Equal(3, view.Me!.Rank);
            Assert.Equal("Chernobog", view.BossName);
            Assert.Equal("Registration", view.Phase);
            Assert.Equal(0, view.BossMaxHp);
            Assert.Equal(WorldBossRewards.Milestones.Count, view.Milestones.Count);
            Assert.Equal(3, view.Places.Count);
            Assert.Equal(GoldRewardScale.FloorGold, view.Milestones[0].Gold);

            var stranger = await WorldBossBoard.ViewAsync(db, 1);
            Assert.Null(stranger.Me);
        }
    }
}

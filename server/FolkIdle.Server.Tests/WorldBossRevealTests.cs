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
    /// Chernobog's week through the real engine (spec 2026-10-13, section 2 and
    /// 3.2): registration hides the health and registers players at the ranked
    /// level, the reveal sets it from their average daily damage, a latecomer
    /// never moves it, a fall does not stop the strikes, and a strike that
    /// crosses a milestone pays it at once - cosmetics only once per account.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossRevealTests
    {
        private readonly PostgresTestFixture _fixture;

        public WorldBossRevealTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<long> PlayerAsync(int level)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var p = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"wb_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = level,
            };
            db.PlayerRecords.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        }

        private async Task<WorldBossEngine> OpenAsync()
        {
            var engine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry, BossMinigameMode.Wheel);
            await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
            return engine;
        }

        /// <summary>An auto-strike on a plate that is not weak, so it is worth exactly <paramref name="damage"/>.</summary>
        private static WorldBossStrikeOrder Auto(long player) => new()
        {
            PlayerId = player,
            Landings = new[] { new SpearLanding(0, 0, SpearClass.Plate, false) },
            WeakPlate = 1,
            Multiplier = WorldBossStrikeRules.Floor,
            LandedAs = WorldBossStrikeResult.Landed,
        };

        private async Task<WorldBossSnapshot> SnapshotAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.WorldBossSnapshots.AsNoTracking().SingleAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
        }

        private async Task<PlayerWorldBossAttempt> AttemptAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerWorldBossAttempts.AsNoTracking()
                .SingleAsync(a => a.PlayerId == playerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
        }

        /// <summary>Lets <paramref name="playerId"/> strike again today, as tomorrow would.</summary>
        private async Task NextDayAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE player_world_boss_attempts SET \"AttemptDateKey\" = \"AttemptDateKey\" - 1 WHERE \"PlayerId\" = {0}", playerId);
        }

        [Fact]
        public async Task RegistrationHidesTheHealthAndRegistersOnlyRankedLevels()
        {
            var engine = await OpenAsync();
            long veteran = await PlayerAsync(20);
            long fresh = await PlayerAsync(1);

            Assert.Equal(WorldBossStrikeResult.Landed, (await engine.ExecuteStrikeAsync(Auto(veteran), 10_000)).Result);
            Assert.Equal(WorldBossStrikeResult.Landed, (await engine.ExecuteStrikeAsync(Auto(fresh), 10_000)).Result);

            var snapshot = await SnapshotAsync();
            Assert.Equal(0, snapshot.MaxHp);
            Assert.Equal(0, snapshot.HpRevealed);
            Assert.Equal(10_000, snapshot.TotalDamageContributed);

            var v = await AttemptAsync(veteran);
            Assert.True(v.IsRegistered);
            Assert.Equal((10_000L, 1), (v.RegistrationDamage, v.RegistrationStrikes));
            var f = await AttemptAsync(fresh);
            Assert.False(f.IsRegistered);
            Assert.Equal(10_000, f.TotalInflictedDamage);
        }

        [Fact]
        public async Task TheRevealIsTheAverageDailyDamageTimesAHiddenN_LessWhatRegistrationDealt()
        {
            var engine = await OpenAsync();
            long both = await PlayerAsync(20);
            long once = await PlayerAsync(20);

            await engine.ExecuteStrikeAsync(Auto(both), 10_000);
            await NextDayAsync(both);
            await engine.ExecuteStrikeAsync(Auto(both), 20_000);
            await engine.ExecuteStrikeAsync(Auto(once), 6_000);

            await engine.RevealNowAsync();

            var s = await SnapshotAsync();
            Assert.Equal(1, s.HpRevealed);
            Assert.InRange(s.HpFactor, WorldBossHpRules.MinFactor, WorldBossHpRules.MaxFactor);
            // (10k + 20k) / 2 + 6k = 21k a day, times N.
            Assert.Equal((long)Math.Round(21_000 * s.HpFactor), s.MaxHp);
            Assert.Equal(s.MaxHp - 36_000, s.CurrentHp);
            Assert.Equal(s.MaxHp, engine.BossMaxHp);
        }

        [Fact]
        public async Task ALatecomerNeverMovesTheHealth_ARegisteredPlayerDoes_AndAFallDoesNotStopTheWeek()
        {
            var engine = await OpenAsync();
            long registered = await PlayerAsync(20);
            long latecomer = await PlayerAsync(20);
            long third = await PlayerAsync(20);

            await engine.ExecuteStrikeAsync(Auto(registered), 10_000);
            await engine.RevealNowAsync();
            long hp = (await SnapshotAsync()).CurrentHp;

            await engine.ExecuteStrikeAsync(Auto(latecomer), 50_000);
            Assert.Equal(hp, (await SnapshotAsync()).CurrentHp);
            Assert.Equal(50_000, (await AttemptAsync(latecomer)).TotalInflictedDamage);
            Assert.False((await AttemptAsync(latecomer)).IsRegistered);

            // The registered player finishes it: the boss falls, the overkill is
            // still the player's, and the week goes on.
            await NextDayAsync(registered);
            var kill = await engine.ExecuteStrikeAsync(Auto(registered), hp + 5_000);
            Assert.Equal(WorldBossStrikeResult.Landed, kill.Result);
            Assert.Equal(hp + 5_000, kill.Damage);
            var s = await SnapshotAsync();
            Assert.Equal(0, s.CurrentHp);
            Assert.True(s.DefeatedAtEpoch > 0);
            Assert.True(engine.IsBossDead());

            Assert.Equal(WorldBossStrikeResult.Landed, (await engine.ExecuteStrikeAsync(Auto(third), 1_000)).Result);
        }

        [Fact]
        public async Task CrossingMilestonesPaysThemAtOnce_AndCosmeticsOnlyOncePerAccount()
        {
            var engine = await OpenAsync();
            long player = await PlayerAsync(20);

            // One strike over a million crosses every milestone.
            await engine.ExecuteStrikeAsync(Auto(player), 1_200_000);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var mail = await db.MailboxInstances.AsNoTracking()
                    .Where(m => m.PlayerId == player && m.SenderName == WorldBossRewards.SenderName).ToListAsync();
                Assert.Equal(WorldBossRewards.Milestones.Sum(m => m.Diamonds), mail.Sum(m => m.DiamondAttachment));
                Assert.Equal(3, await db.CosmeticItems.CountAsync(c => c.PlayerId == player && c.Source == (byte)CosmeticSource.WorldBoss));
                Assert.True(await db.PlayerTitles.AnyAsync(t => t.PlayerId == player && t.TitleSlug == WorldBossRewards.ChainerSlug));
                Assert.True(await db.PlayerPets.AnyAsync(p => p.PlayerId == player && p.PetId == PetRegistry.BlackSerpentId));
            }
            Assert.Equal((1 << WorldBossRewards.Milestones.Count) - 1, (await AttemptAsync(player)).MilestonesPaid);

            // A new week: the currency again, the cosmetics never twice.
            engine = await OpenAsync();
            await engine.ExecuteStrikeAsync(Auto(player), 1_200_000);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var mail = await db.MailboxInstances.AsNoTracking()
                    .Where(m => m.PlayerId == player && m.SenderName == WorldBossRewards.SenderName).ToListAsync();
                Assert.Equal(2 * WorldBossRewards.Milestones.Sum(m => m.Diamonds), mail.Sum(m => m.DiamondAttachment));
                Assert.Equal(3, await db.CosmeticItems.CountAsync(c => c.PlayerId == player && c.Source == (byte)CosmeticSource.WorldBoss));
                Assert.Equal(1, await db.PlayerPets.CountAsync(p => p.PlayerId == player && p.PetId == PetRegistry.BlackSerpentId));
            }
        }

        [Fact]
        public async Task AnEncounterOpenedBeforeChernobogIsUpgradedAndKeepsItsStrikers()
        {
            var engine = await OpenAsync();
            long striker = await PlayerAsync(20);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // What production holds on the deploy: the old 75M health, no reveal
                // time, and a strike row from Monday with no registration fields.
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"WorldBossSnapshots\" SET \"RevealEpoch\" = 0, \"MaxHp\" = 75000000, \"CurrentHp\" = 74990000 WHERE \"BossInstanceId\" = {0}",
                    (long)WorldBossEngine.ActiveBossInstanceId);
                db.PlayerWorldBossAttempts.Add(new PlayerWorldBossAttempt
                {
                    PlayerId = striker, BossInstanceId = WorldBossEngine.ActiveBossInstanceId,
                    AttemptCount = 1, TotalInflictedDamage = 17_280,
                    AttemptDateKey = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 1,
                });
                await db.SaveChangesAsync();
            }

            await engine.MaintainActiveBossAsync();

            var s = await SnapshotAsync();
            Assert.True(s.RevealEpoch > 0);
            Assert.Equal(0, s.MaxHp);
            var a = await AttemptAsync(striker);
            Assert.True(a.IsRegistered);
            Assert.Equal(17_280, a.RegistrationDamage);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 55: the rules of the boss challenges, that each pays once, and that
    /// the tick records what a fight was.
    /// </summary>
    [Collection("Postgres collection")]
    public class BossChallengeTests
    {
        private readonly PostgresTestFixture _fixture;

        public BossChallengeTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void TheRulesAreWhatTheySay()
        {
            // Region 1: level cap 15, time limit 80 s = 800 tenths.
            Assert.Equal(new[] { BossChallenge.Starved, BossChallenge.Young, BossChallenge.Swift },
                BossChallengeRegistry.Met(region: 1, level: 15, ateDuringFight: false, fightTenths: 800));
            Assert.Empty(BossChallengeRegistry.Met(region: 1, level: 16, ateDuringFight: true, fightTenths: 801));
            Assert.Equal(new[] { BossChallenge.Swift }, BossChallengeRegistry.Met(5, 99, true, 30));
            // A fight with no recorded length is not a fast one.
            Assert.Empty(BossChallengeRegistry.Met(5, 99, true, 0));

            Assert.Equal(CosmeticRegistry.Rare, BossChallengeRegistry.RewardChestRarityFor(1));
            Assert.Equal(CosmeticRegistry.Rare, BossChallengeRegistry.RewardChestRarityFor(2));
            Assert.Equal(CosmeticRegistry.Epic, BossChallengeRegistry.RewardChestRarityFor(3));
            Assert.Equal(CosmeticRegistry.Epic, BossChallengeRegistry.RewardChestRarityFor(4));
            Assert.Equal(CosmeticRegistry.Legendary, BossChallengeRegistry.RewardChestRarityFor(5));
        }

        [Fact]
        public async Task AChallengePaysOnce_AndAWornWeaponIsJudgedByItsRow()
        {
            const long playerId = 956000001L;
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                seed.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await seed.SaveChangesAsync();
            }

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            // Fed, too old and slow: nothing. (Region 3: cap 45, limit 120 s.)
            Assert.Empty(await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 90, true, 2000, DateTime.UtcNow));

            // Starved and young, still slow: two chests, Epic for region 3.
            var first = await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 40, false, 2000, DateTime.UtcNow);
            Assert.Equal(new[] { BossChallenge.Starved, BossChallenge.Young }, first.Select(g => g.Challenge));
            Assert.All(first, g => Assert.Equal(CosmeticRegistry.Epic, g.ChestRarity));

            // The same kill again pays nothing; a fast one adds Swift only.
            var second = await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 40, false, 900, DateTime.UtcNow);
            Assert.Equal(new[] { BossChallenge.Swift }, second.Select(g => g.Challenge));
            Assert.Empty(await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 40, false, 900, DateTime.UtcNow));

            Assert.Equal(3, await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.Source == (byte)CosmeticSource.Challenge));

            var view = await BossChallengeEngine.ViewAsync(db, playerId);
            Assert.Equal(5, view.Count);
            Assert.All(view.Single(r => r.Region == 3).Challenges, c => Assert.True(c.Completed));
            Assert.All(view.Single(r => r.Region == 1).Challenges, c => Assert.False(c.Completed));
        }

        /// <summary>
        /// The tick's half: the level is taken BEFORE the kill's own XP, the
        /// note carries the fight's own clock, and the "ate" flag is set at the
        /// bite and cleared at both places a fight starts.
        /// </summary>
        [Fact]
        public void TheTickRecordsWhatTheFightWas()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server"))) dir = dir.Parent;
            string sim = File.ReadAllText(Path.Combine(dir!.FullName, "server", "FolkIdle.Server", "Domain", "Combat", "SimulationEngine.cs"))
                .Replace("\r\n", "\n");

            int levelAt = sim.IndexOf("int levelAtKill = payload.CurrentLevel;", StringComparison.Ordinal);
            int death = sim.IndexOf("ProgressionEngine.ProcessMonsterDeath(ref payload", StringComparison.Ordinal);
            Assert.True(levelAt > 0 && levelAt < death, "the challenge level must be read before the kill's XP lands");
            Assert.Matches(@"CosmeticGrantEngine\.NoteBossKill\(payload\.PlayerId, clearedBossRegion, activeMonster\.Id,\s+levelAtKill, payload\.AteThisFight, \(int\)Math\.Min\(int\.MaxValue, \(long\)payload\.CombatTargetTickAccumulator\)", sim);

            Assert.Single(Regex.Matches(sim, @"payload\.AteThisFight = true;"));
            Assert.Equal(2, Regex.Matches(sim, @"payload\.AteThisFight = false;").Count);
        }
    }
}

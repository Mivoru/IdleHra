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
            Assert.Equal(new[] { BossChallenge.Starved, BossChallenge.Young, BossChallenge.Humble },
                BossChallengeRegistry.Met(region: 1, level: 15, ateDuringFight: false, weaponQualityTier: 2));
            Assert.Empty(BossChallengeRegistry.Met(region: 1, level: 16, ateDuringFight: true, weaponQualityTier: 3));
            Assert.Equal(new[] { BossChallenge.Humble }, BossChallengeRegistry.Met(5, 99, true, 0));

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
            var legendaryBlade = new EquipmentInstance { PlayerId = playerId, BaseItemId = "test_blade", QualityTier = 7 };
            db.EquipmentInstances.Add(legendaryBlade);
            await db.SaveChangesAsync();

            // Fed, too old, and a Legendary weapon: nothing.
            Assert.Empty(await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 90, true, legendaryBlade.Id, DateTime.UtcNow));

            // Starved and young, still the Legendary weapon: two chests, Epic for region 3.
            var first = await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 40, false, legendaryBlade.Id, DateTime.UtcNow);
            Assert.Equal(new[] { BossChallenge.Starved, BossChallenge.Young }, first.Select(g => g.Challenge));
            Assert.All(first, g => Assert.Equal(CosmeticRegistry.Epic, g.ChestRarity));

            // The same kill again pays nothing; bare-handed adds Humble only.
            var second = await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 40, false, 0, DateTime.UtcNow);
            Assert.Equal(new[] { BossChallenge.Humble }, second.Select(g => g.Challenge));
            Assert.Empty(await BossChallengeEngine.JudgeKillAsync(db, playerId, 3, 40, false, 0, DateTime.UtcNow));

            // A weapon id the player does not own is not "no weapon".
            Assert.DoesNotContain(
                (await BossChallengeEngine.JudgeKillAsync(db, playerId, 4, 200, true, 999_999_999, DateTime.UtcNow)).Select(g => g.Challenge),
                c => c == BossChallenge.Humble);

            Assert.Equal(3, await db.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.Source == (byte)CosmeticSource.Challenge));

            var view = await BossChallengeEngine.ViewAsync(db, playerId);
            Assert.Equal(5, view.Count);
            Assert.All(view.Single(r => r.Region == 3).Challenges, c => Assert.True(c.Completed));
            Assert.All(view.Single(r => r.Region == 1).Challenges, c => Assert.False(c.Completed));
        }

        /// <summary>
        /// The tick's half: the level is taken BEFORE the kill's own XP, the
        /// note is sent at every boss kill, and the "ate" flag is set at the
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
            Assert.Matches(@"CosmeticGrantEngine\.NoteBossKill\(payload\.PlayerId, clearedBossRegion, activeMonster\.Id,\s+levelAtKill, payload\.AteThisFight", sim);

            Assert.Single(Regex.Matches(sim, @"payload\.AteThisFight = true;"));
            Assert.Equal(2, Regex.Matches(sim, @"payload\.AteThisFight = false;").Count);
        }
    }
}

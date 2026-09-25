using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: OFFLINE PAYS WHAT ONLINE PAYS - an owner decision, 2026-09-25.
    ///
    /// The offline projection had its own copy of the per-kill gold formula,
    /// and that copy had lost the legacy gold perk, the guild Gold buff and
    /// Trophy Hunter. Both paths call CombatGoldReward.PerKill now. These tests
    /// pin that from both sides: the source can hold only one formula, and a
    /// real offline window pays PerKill for every kill it counts.
    /// </summary>
    [Collection("Postgres collection")]
    public class CombatGoldParityTests
    {
        private readonly PostgresTestFixture _fixture;

        public CombatGoldParityTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private static string ServerSource(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir!.FullName, "FolkIdle.Server", relativePath));
        }

        [Fact]
        public void BothKillPathsCallTheOneFormulaAndNothingElseRebuildsIt()
        {
            string live = ServerSource(Path.Combine("Domain", "Combat", "SimulationEngine.cs"));
            string offline = ServerSource(Path.Combine("Engine", "OfflineSimulationEngine.cs"));

            Assert.Contains("CombatGoldReward.PerKill(", live);
            Assert.Contains("CombatGoldReward.PerKill(", offline);

            // A second copy starts with the base: if either file computes it
            // itself again, the two formulas can drift again.
            Assert.DoesNotContain("EconomyDecisions.BaseCombatGold(", live);
            Assert.DoesNotContain("EconomyDecisions.BaseCombatGold(", offline);
            Assert.DoesNotMatch(new Regex(@"Inherit_GoldGain\)\s*/\s*100"), offline);
        }

        [Fact]
        public void PerKillAppliesEveryMultiplier()
        {
            ContentRegistry.Initialize();
            var monster = ContentRegistry.Monsters.ToArray().Single(m => m.Id == ContentRegistry.FirstCanonicalMonsterId);
            const long guildId = 950_041_001L;

            var plain = new TickStatePayload();
            long baseGold = CombatGoldReward.PerKill(in plain, in monster, 0f);
            Assert.Equal(EconomyDecisions.BaseCombatGold(monster.BaseGoldReward), baseGold);

            try
            {
                GuildBonusesCache.Apply(guildId, "Gold", 5, DateTime.UtcNow.AddHours(1));
                var buffed = new TickStatePayload
                {
                    GuildId = guildId,
                    CachedLegacyPerks = LegacyPerkResolver.SetPerkRank(0L, LegacyPerkResolver.GoldDropRateBitOffset, 20),
                };
                long buffedGold = CombatGoldReward.PerKill(in buffed, in monster, 0f);

                // +20% legacy, then +10% guild (tier 5 x 2%), each truncated.
                long expected = (long)(baseGold * 1.2f);
                expected = (long)(expected * 1.1f);
                Assert.Equal(expected, buffedGold);
            }
            finally
            {
                GuildBonusesCache.Clear(guildId);
            }
        }

        [Fact]
        public async Task AnOfflineWindowPaysPerKillForEveryKill()
        {
            const long elapsedOfflineSeconds = 3600L;
            const int monsterId = 31;
            const long guildId = 950_041_002L;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            TickStatePayload Build(long playerId) => new TickStatePayload
            {
                PlayerId = playerId,
                LastLogoutTimestamp = now - elapsedOfflineSeconds,
                ActiveActivityId = monsterId,
                CurrentLevel = 1,
                InventorySpaceRemaining = 1000,
                Food1_ItemId = ContentRegistry.RawFishItemIds.First(),
                Food1_Count = 100000,
            };

            var plain = Build(950_041_101L);
            var buffed = Build(950_041_102L);
            buffed.GuildId = guildId;
            buffed.CachedLegacyPerks = LegacyPerkResolver.SetPerkRank(0L, LegacyPerkResolver.GoldDropRateBitOffset, 20);

            try
            {
                GuildBonusesCache.Apply(guildId, "Gold", 5, DateTime.UtcNow.AddHours(1));

                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    plain = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, plain, now);
                }
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    buffed = await OfflineSimulationEngine.ExtrapolateOfflineProgressAsync(db, buffed, now);
                }

                // Gold multipliers do not change how fast anything dies, so
                // both windows count the same kills. Recover the count from
                // the plain run, then the buffed run must pay PerKill for each.
                var monster = ContentRegistry.Monsters.ToArray().Single(m => m.Id == monsterId);
                long plainPerKill = CombatGoldReward.PerKill(in plain, in monster, 0f);
                long buffedPerKill = CombatGoldReward.PerKill(in buffed, in monster, 0f);
                Assert.True(plainPerKill > 0);
                Assert.True(buffedPerKill > plainPerKill, "the legacy perk and guild buff raised nothing");

                Assert.True(plain.OfflineGoldEarned > 0, "the offline window killed nothing");
                Assert.Equal(0L, plain.OfflineGoldEarned % plainPerKill);
                long kills = plain.OfflineGoldEarned / plainPerKill;

                Assert.Equal(kills * buffedPerKill, buffed.OfflineGoldEarned);
            }
            finally
            {
                GuildBonusesCache.Clear(guildId);
            }
        }
    }
}

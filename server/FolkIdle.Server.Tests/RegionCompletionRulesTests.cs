using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 26, H5: area-completion luck could not be earned, because the rule
    /// counted legacy monsters (ids 1-90) nobody can fight. These pin the rule
    /// the owner chose: the five canonical regions, 1,000 kills of each
    /// regular, 100 of the boss.
    /// </summary>
    public class RegionCompletionRulesTests
    {
        public RegionCompletionRulesTests()
        {
            ContentRegistry.Initialize();
        }

        private static Dictionary<int, int> RegionAtThreshold(int location)
        {
            var kills = new Dictionary<int, int>();
            int first = RegionCompletionRules.FirstMonsterOf(location);
            for (int id = first; id < first + ContentRegistry.MonstersPerRegion; id++)
            {
                kills[id] = ContentRegistry.IsRegionalBoss(id) ? 100 : 1000;
            }
            return kills;
        }

        [Fact]
        public void ARegionCompletesOnItsFiveCanonicalMonstersAlone()
        {
            // Not one legacy monster has a kill, which is every real player.
            var kills = RegionAtThreshold(1);
            Assert.True(RegionCompletionRules.IsComplete(1, id => kills.GetValueOrDefault(id)));
            Assert.Equal(1 << 1, RegionCompletionRules.CompletedFlags(id => kills.GetValueOrDefault(id)));
        }

        [Fact]
        public void TheBossNeedsAHundred_TheRegularsAThousand()
        {
            var kills = RegionAtThreshold(3);
            Assert.Equal(100, RegionCompletionRules.RequiredKills(105));
            Assert.Equal(1000, RegionCompletionRules.RequiredKills(101));
            Assert.Equal(0, RegionCompletionRules.RequiredKills(12)); // legacy: in no region

            kills[105] = 99;
            Assert.False(RegionCompletionRules.IsComplete(3, id => kills.GetValueOrDefault(id)));
            kills[105] = 100;
            kills[102] = 999;
            Assert.False(RegionCompletionRules.IsComplete(3, id => kills.GetValueOrDefault(id)));
        }

        [Fact]
        public void TheReportingAccount_RegularsDoneBossesShort_IsNotComplete()
        {
            // Production, player 8, 2026-09-24: region 2 regulars at >= 1,118,
            // boss at 8. The owner's threshold is 100, so not yet.
            var kills = RegionAtThreshold(2);
            kills[100] = 8;
            Assert.Equal(0, RegionCompletionRules.CompletedFlags(id => kills.GetValueOrDefault(id)));
        }

        [Fact]
        public void AllFiveRegions_AreWorthFiveLootLuck_AndSetBitsOneToFive()
        {
            var kills = new Dictionary<int, int>();
            for (int location = 1; location <= ContentRegistry.LocationCount; location++)
            {
                foreach (var kv in RegionAtThreshold(location)) kills[kv.Key] = kv.Value;
            }

            int flags = RegionCompletionRules.CompletedFlags(id => kills.GetValueOrDefault(id));
            Assert.Equal(0b111110, flags);

            float bare = StatsCalculator.Calculate(50, 50, 50, 25).LootLuckPct;
            float done = StatsCalculator.Calculate(50, 50, 50, 25, completedAreaFlags: flags).LootLuckPct;
            Assert.Equal(5f, done - bare, 3);
        }

        [Fact]
        public void NoCopyOfTheOldRuleSurvives()
        {
            // The rule was written three times, grouped by GetMonsterRegionTier.
            // Each copy must now ask RegionCompletionRules.
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolkIdle.Server"))) dir = dir.Parent;
            Assert.NotNull(dir);

            foreach (var file in new[]
            {
                Path.Combine("Domain", "Shared", "StateCheckpointManager.cs"),
                Path.Combine("Engine", "CodexEngine.cs"),
                Path.Combine("Network", "NetworkBroadcastSystem.cs"),
            })
            {
                string source = File.ReadAllText(Path.Combine(dir!.FullName, "FolkIdle.Server", file));
                Assert.Contains("RegionCompletionRules.", source);
                Assert.False(Regex.IsMatch(source, @"KillCount\s*(>=|<)\s*1000"),
                    $"{file} still hard-codes the 1000-kill completion rule");
            }
        }
    }
}

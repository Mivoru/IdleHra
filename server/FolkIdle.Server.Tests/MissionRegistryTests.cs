using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FolkIdle.Server.Domain.Progression;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Daily missions without a database: the draw, the unlock filter, the
    /// reroll pick, the XP arithmetic the pass is built on, and - mechanically -
    /// that every mission act has a writer.
    /// </summary>
    public class MissionRegistryTests
    {
        private readonly ITestOutputHelper _output;
        public MissionRegistryTests(ITestOutputHelper output) => _output = output;

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

        /// <summary>A brand-new account: level 1, nothing built, nothing beaten.</summary>
        private static readonly MissionFacts Newcomer = new() { Level = 1, Gold = 500 };

        [Fact]
        public void AVeteranGetsSixMissions_TheThreeFixedOnesFirst()
        {
            var day = MissionRegistry.Draw(42, 20_000, Veteran);
            Assert.Equal(MissionRegistry.MissionsPerDay, day.Length);
            Assert.Equal(new[] { MissionAction.DungeonFight, MissionAction.ExpeditionSent, MissionAction.WorldBossStrike }, day.Take(3));
            Assert.Equal(day.Length, day.Distinct().Count());
        }

        [Fact]
        public void TheDrawIsTheSameForTheSameDay_AndMovesBetweenDays()
        {
            var a = MissionRegistry.Draw(42, 20_000, Veteran);
            var b = MissionRegistry.Draw(42, 20_000, Veteran);
            Assert.Equal(a, b);

            // Over a month of days the rotating half must not be one fixed set.
            var distinctDays = Enumerable.Range(20_000, 30)
                .Select(d => string.Join(",", MissionRegistry.Draw(42, d, Veteran).Skip(3)))
                .Distinct().Count();
            Assert.True(distinctDays > 10, $"only {distinctDays} different rotating sets in 30 days");
        }

        [Fact]
        public void OutsideTheWorldBossWeek_ARotatingMissionTakesItsSlot()
        {
            var day = MissionRegistry.Draw(42, 20_000, Veteran with { WorldBossActive = false });
            Assert.Equal(MissionRegistry.MissionsPerDay, day.Length);
            Assert.DoesNotContain(MissionAction.WorldBossStrike, day);
        }

        /// <summary>
        /// The new-player trap: every mission a level-1 account is offered must
        /// be one it can do. The fixture can never show this - it has
        /// everything open.
        /// </summary>
        [Fact]
        public void ANewAccountIsOnlyOfferedWhatItCanDo()
        {
            for (int d = 20_000; d < 20_060; d++)
            {
                var day = MissionRegistry.Draw(7, d, Newcomer);
                Assert.NotEmpty(day);
                foreach (var action in day)
                {
                    Assert.True(MissionRegistry.TryGet(action, out var def));
                    Assert.True(def.Eligible(Newcomer), $"{action} offered to a level-1 account on day {d}");
                }
                Assert.DoesNotContain(MissionAction.DungeonFight, day);
                Assert.DoesNotContain(MissionAction.GuildDonation, day);
                Assert.DoesNotContain(MissionAction.ChildBorn, day);
                Assert.DoesNotContain(MissionAction.MarketListed, day);
            }
        }

        [Fact]
        public void TheRerollNeverRepeatsAMissionOfTheDay_NorOffersAFixedOne()
        {
            var day = MissionRegistry.Draw(42, 20_000, Veteran);
            var pick = MissionRegistry.RerollPick(42, 20_000, 1, day, Veteran);
            Assert.NotNull(pick);
            Assert.DoesNotContain(pick!.Value, day);
            Assert.True(MissionRegistry.TryGet(pick.Value, out var def));
            Assert.False(def.IsFixed);
        }

        [Fact]
        public void TheRerollAnswersNullWhenNothingElseIsOpen()
        {
            // A newcomer's only rotating mission is chat.
            var day = MissionRegistry.Draw(7, 20_000, Newcomer);
            Assert.Null(MissionRegistry.RerollPick(7, 20_000, 1, day, Newcomer));
        }

        /// <summary>
        /// The pass is built on this arithmetic (docs/design/CHRONICLE_PASS.md):
        /// a full day is 200 XP, so every mission on 25 days of 30 is 5,000 XP,
        /// which is 50 levels of 100.
        /// </summary>
        [Fact]
        public void AFullDayIs200Xp_And25FullDaysAre50Levels()
        {
            int fullDay = MissionRegistry.MissionsPerDay * MissionRegistry.XpPerMission + MissionRegistry.XpAllDoneBonus;
            Assert.Equal(200, fullDay);
            Assert.Equal(50 * 100, 25 * fullDay);
        }

        [Fact]
        public void EveryMissionHasAPositiveTarget_AndActionsAreUnique()
        {
            Assert.Equal(MissionRegistry.All.Count, MissionRegistry.All.Select(d => d.Action).Distinct().Count());
            Assert.All(MissionRegistry.All, d => Assert.True(d.Target > 0, d.Action.ToString()));
            Assert.Equal(Enum.GetValues<MissionAction>().Length, MissionRegistry.All.Count);
        }

        [Fact]
        public void EncodeAndDecodeRoundTrip()
        {
            var day = MissionRegistry.Draw(42, 20_000, Veteran);
            Assert.Equal(day, MissionEngine.Decode(MissionEngine.Encode(day)));
            Assert.Empty(MissionEngine.Decode(string.Empty));
        }

        /// <summary>
        /// Every MissionAction needs a MissionEvents.Record call somewhere
        /// outside the mission files themselves. A mission nobody writes is a
        /// mission nobody can finish, and it would render perfectly - the
        /// "read side with no writer" defect CLAUDE.md names.
        /// </summary>
        [Fact]
        public void EveryMissionActionHasAWriter()
        {
            string root = ServerSourceRoot();
            var writers = new Dictionary<MissionAction, List<string>>();
            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("Mission", StringComparison.Ordinal)) continue;
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                string source = File.ReadAllText(path);
                if (!source.Contains("MissionEvents.Record(", StringComparison.Ordinal)) continue;
                foreach (Match m in Regex.Matches(source, @"MissionEvents\.Record\([^;]*;"))
                {
                    foreach (Match a in Regex.Matches(m.Value, @"MissionAction\.(\w+)"))
                    {
                        if (!Enum.TryParse<MissionAction>(a.Groups[1].Value, out var action)) continue;
                        if (!writers.TryGetValue(action, out var list)) writers[action] = list = new List<string>();
                        list.Add(name);
                    }
                }
            }

            foreach (var action in Enum.GetValues<MissionAction>())
            {
                _output.WriteLine($"{action,-22} {(writers.TryGetValue(action, out var w) ? string.Join(", ", w.Distinct()) : "** NO WRITER **")}");
            }

            var missing = Enum.GetValues<MissionAction>().Where(a => !writers.ContainsKey(a)).ToList();
            Assert.True(missing.Count == 0, "Mission acts nothing records: " + string.Join(", ", missing));
        }

        private static string ServerSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "could not locate server/FolkIdle.Server from the test binary");
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server");
        }
    }
}

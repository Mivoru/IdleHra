using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// DO A CHARACTER'S ATTRIBUTES ACTUALLY GROW? - 2026-09-06.
    ///
    /// Asked because the live database says no. The only account past level 1
    /// is level 86 and its four attributes read 50 / 50 / 50 / 25 - exactly the
    /// values a brand-new registration gets. A level-86 Human should be at
    /// 50 + 2 * 85 = 220 STR, DEX and CON.
    ///
    /// It matters more than it used to. DEX is `AccuracyRating`, and until
    /// 2026-09-06 accuracy bought nothing at all, because every canonical
    /// monster had DodgeRating 0. Monsters evade now, so a character stuck at
    /// its starting DEX lands a far smaller share of its swings than the curve
    /// assumes - MonsterDefenceCurve prices dodge against `40 * (region - 1)`
    /// accuracy, which is what levelling is supposed to provide.
    /// </summary>
    public class AttributeGrowthTests
    {
        private readonly ITestOutputHelper _o;

        public AttributeGrowthTests(ITestOutputHelper o) => _o = o;

        private static TickStatePayload FreshLevelOne() => new()
        {
            CurrentLevel = 1,
            CurrentXp = 0,
            STR = 50,
            DEX = 50,
            CON = 50,
            LCK = 25,
            SelectedLineageId = 1,
        };

        [Fact]
        public void KillingEnoughToLevelPaysAttributePoints()
        {
            var payload = FreshLevelOne();
            int startingCon = payload.CON;
            int startingDex = payload.DEX;

            // Enough XP for several levels at the real curve, granted through
            // the path an ordinary kill takes.
            long xpForTen = 0;
            for (int level = 1; level <= 10; level++)
            {
                xpForTen += ProgressionEngine.GetRequiredXpForLevel(level);
            }

            ProgressionEngine.ProcessMonsterDeath(
                ref payload,
                baseExpReward: (int)System.Math.Min(int.MaxValue, xpForTen),
                xpMultiplier: 100,
                activeGlobalEventId: 0,
                activeRaceId: RaceIds.Human);

            _o.WriteLine($"level {payload.CurrentLevel}, {payload.UnspentAttributePoints} points, "
                + $"STR {payload.STR}, DEX {payload.DEX}, CON {payload.CON}, LCK {payload.LCK}");

            Assert.True(payload.CurrentLevel > 1, "the character did not level at all.");

            // Modul: a level pays a POOL now - the player places it. The four
            // attributes must NOT move on their own, or the choice would be
            // made twice.
            int levelsGained = payload.CurrentLevel - 1;
            Assert.Equal(RaceAttributeGrowth.AttributePointsPerLevel * levelsGained, payload.UnspentAttributePoints);
            Assert.Equal(startingCon, payload.CON);
            Assert.Equal(startingDex, payload.DEX);
        }

        [Fact]
        public void PointsAreGrantedWhateverTheRaceIs()
        {
            // Modul: the OLD trap, kept as a test of the new behaviour.
            //
            // GetGrowthPerLevel's `default` arm returns 0/0/0/0, and
            // activeRaceId is the low byte of the character's genetic vector -
            // so a character with a missing lineage row levelled forever without
            // gaining a point, and it would have looked exactly like the live
            // account did. Points are race-independent now, which removes that
            // failure mode entirely rather than guarding it.
            var payload = FreshLevelOne();

            long xpForFive = 0;
            for (int level = 1; level <= 5; level++)
            {
                xpForFive += ProgressionEngine.GetRequiredXpForLevel(level);
            }

            ProgressionEngine.ProcessMonsterDeath(
                ref payload,
                baseExpReward: (int)System.Math.Min(int.MaxValue, xpForFive),
                xpMultiplier: 100,
                activeGlobalEventId: 0,
                activeRaceId: 0);

            _o.WriteLine($"race 0: level {payload.CurrentLevel}, {payload.UnspentAttributePoints} points");

            Assert.True(payload.CurrentLevel > 1);
            Assert.True(payload.UnspentAttributePoints > 0,
                "a character with no resolvable race gained no attribute points - that is the old race-gated defect, back.");
        }

        [Fact]
        public void EveryLevellingPathGrantsAttributes()
        {
            // Modul: THIS IS THE ONE THAT WAS BROKEN, and it is the path an
            // idle game uses most.
            //
            // The game grows levels in THREE places - ProgressionEngine (an
            // ordinary kill), SimulationEngine's bulk/warp catch-up, and
            // OfflineSimulationEngine's projection. The first two called
            // RaceAttributeGrowth; the offline one raised the level, paid the
            // skill point and granted no attributes at all. The live account
            // sat at level 86 with a fresh registration's 50 / 50 / 50 / 25.
            //
            // Asserted by reading the source, because the offline projection
            // needs a whole simulated window to drive end to end and this is the
            // property that actually matters: no path may level a character
            // without asking RaceAttributeGrowth for the points.
            foreach (string relativePath in new[]
                     {
                         "Engine/ProgressionEngine.cs",
                         "Engine/OfflineSimulationEngine.cs",
                         "Domain/Combat/SimulationEngine.cs",
                     })
            {
                string source = SourceOf(relativePath);
                Assert.Contains("ApplyLevelUpGrowth", source);
            }

            // And the offline path specifically must count the levels it grants
            // rather than calling growth with a stale or zero figure.
            string offline = SourceOf("Engine/OfflineSimulationEngine.cs");
            // The DEFINITION, not the first call site.
            int applyAt = offline.IndexOf("void ApplyCombatXp", System.StringComparison.Ordinal);
            Assert.True(applyAt > 0);
            string body = offline.Substring(applyAt, System.Math.Min(4000, offline.Length - applyAt));
            Assert.Contains("levelsGained++", body);
            Assert.Contains("ApplyLevelUpGrowth", body);
        }

        private static string SourceOf(string relativePath)
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "server", "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            string full = System.IO.Path.Combine(
                dir!.FullName, "server", "FolkIdle.Server",
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Assert.True(System.IO.File.Exists(full), $"{full} not found");
            return System.IO.File.ReadAllText(full);
        }

    }
}

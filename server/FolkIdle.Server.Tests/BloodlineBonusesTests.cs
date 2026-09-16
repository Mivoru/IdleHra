using System.IO;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: ONE FORMULA PER STAT, used by the live tick AND the offline
    /// projection. Offline combat never applied the Strength aptitude at all
    /// (found 2026-09-13) - SimulationEngine added it after
    /// ComputeEffectiveMilliAttack and OfflineSimulationEngine stopped there, so
    /// a bred line killed more slowly away than online. The third instance of
    /// "three paths grow a level" (CLAUDE.md).
    /// </summary>
    public class BloodlineBonusesTests
    {
        [Fact]
        public void AttackAddsTheAptitudeThenTheTrait()
        {
            Assert.Equal(100_000L, BloodlineBonuses.ApplyAttack(100_000L, 0, default));
            // Strength 20 is +30% (BreedingAptitudes band one)
            Assert.Equal(130_000L, BloodlineBonuses.ApplyAttack(100_000L, 20, default));
            var blood = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.BloodOfKings));
            Assert.Equal(143_000L, BloodlineBonuses.ApplyAttack(100_000L, 20, blood));
            var faint = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.FaintHeart));
            Assert.Equal(95_000L, BloodlineBonuses.ApplyAttack(100_000L, 0, faint));
        }

        [Fact]
        public void HealthAddsTheAptitudeThenTheTrait()
        {
            var iron = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.IronBlood));
            Assert.Equal(140_400L, BloodlineBonuses.ApplyMaxHp(100_000L, 20, iron));
            var thin = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.ThinBlood));
            Assert.Equal(94_000L, BloodlineBonuses.ApplyMaxHp(100_000L, 0, thin));
        }

        [Fact]
        public void GatheringSumsAptitudeAndTraits()
        {
            var quick = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.QuickHands, TraitRegistry.GreenThumb));
            Assert.Equal(35, BloodlineBonuses.GatherSpeedBonusPct(20, quick));
            Assert.Equal(5, BloodlineBonuses.GatherYieldBonusPct(quick));
            Assert.Equal(0, BloodlineBonuses.GatherYieldBonusPct(default));
        }

        [Fact]
        public void LiveAndOfflineBothUseTheSharedFormulas()
        {
            foreach (string path in new[] { "Domain/Combat/SimulationEngine.cs", "Engine/OfflineSimulationEngine.cs" })
            {
                string source = SourceOf(path);
                Assert.Contains("BloodlineBonuses.ApplyAttack(", source);
                Assert.Contains("BloodlineBonuses.ApplyMaxHp(", source);
                Assert.Contains("BloodlineBonuses.GatherSpeedBonusPct(", source);
                Assert.Contains("BloodlineBonuses.GatherYieldBonusPct(", source);
                Assert.DoesNotContain("BonusPercentFor(payload.Aptitude_", source);
                Assert.DoesNotContain("LocusYield", source);
            }
        }

        [Fact]
        public void GrowthNoLongerReadsGenesOrTheHiddenInbreedingPenalty()
        {
            string growth = SourceOf("Engine/RaceAttributeGrowth.cs");
            Assert.DoesNotContain("LocusSpeed", growth);
            Assert.DoesNotContain("IsInbred", growth);

            string payload = SourceOf("Engine/TickStatePayload.cs");
            Assert.DoesNotContain("public int LocusSpeed", payload);
            Assert.DoesNotContain("public bool IsInbred", payload);
        }

        private static string SourceOf(string relativePath)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            string full = Path.Combine(dir!.FullName, "server", "FolkIdle.Server", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"{full} not found");
            return File.ReadAllText(full);
        }
    }
}

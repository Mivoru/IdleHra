using System.IO;
using System.Reflection;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
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
                // Offline fights HuntingProjection's fight now, which reads the
                // live attack (SimulationEngine.EffectiveMilliAttackFor, which
                // calls ApplyAttack) rather than a copy that also calls it.
                Assert.True(
                    source.Contains("BloodlineBonuses.ApplyAttack(") || source.Contains("HuntingProjection.FightSetup.For("),
                    $"{path} neither applies the bloodline attack formula nor fights the shared fight");
                // Offline reads the live bar itself now (EffectiveMaxMilliHpFor,
                // which calls ApplyMaxHp) rather than a copy that also calls it.
                Assert.True(
                    source.Contains("BloodlineBonuses.ApplyMaxHp(") || source.Contains("SimulationEngine.EffectiveMaxMilliHpFor(")
                        || source.Contains("HuntingProjection.FightSetup.For("),
                    $"{path} neither applies the bloodline health formula nor reads the live bar");
                // Offline asks the live tick's own gathering functions now
                // (offline parity, 2026-09-30) rather than holding a copy that
                // also calls the bloodline terms.
                Assert.True(
                    source.Contains("BloodlineBonuses.GatherSpeedBonusPct(") || source.Contains("SimulationEngine.RequiredGatherTicks("),
                    $"{path} neither applies the bloodline gather speed nor asks the live speed");
                Assert.True(
                    source.Contains("BloodlineBonuses.GatherYieldBonusPct(") || source.Contains("SimulationEngine.GatheringYieldFor("),
                    $"{path} neither applies the bloodline gather yield nor asks the live yield");
                Assert.DoesNotContain("BonusPercentFor(payload.Aptitude_", source);
                Assert.DoesNotContain("LocusYield", source);
            }
        }

        /// <summary>
        /// Modul: offline had its OWN EffectiveMilliAttackFor, extracted so this
        /// test could hold it to the live one by value - and it still drifted:
        /// the live figure grew the guild Damage buff and the legacy speed perk,
        /// and the copy never did. Since 2026-09-30 the offline fight is
        /// HuntingProjection's, which calls the live method, so the copy is
        /// deleted and this pins that it stays deleted - and that the live
        /// figure reads the Strength aptitude (the case that first broke).
        /// </summary>
        [Fact]
        public void OfflineHasNoAttackCopy_AndTheLiveAttackReadsTheAptitude()
        {
            Assert.Null(typeof(OfflineSimulationEngine).GetMethod(
                "EffectiveMilliAttackFor", BindingFlags.NonPublic | BindingFlags.Static));
            Assert.Contains("SimulationEngine.EffectiveMilliAttackFor(", SourceOf("Domain/Combat/HuntingProjection.cs"));

            var traits = TraitTotals.From(TraitRegistry.MaskOf(TraitRegistry.KeenEdge, TraitRegistry.HawkEye));
            var combatStats = StatsCalculator.Calculate(
                str: 40, dex: 25, con: 30, lck: 15,
                activeOffensivePotionId: 0, activeDefensivePotionId: 0,
                activeAgePhase: 1, completedAreaFlags: 0, activeRaceId: 0,
                humanMastery: 0, vilaMastery: 0, draugrMastery: 0,
                equippedAffixTotals: default, isEpicMutation: false,
                traits: traits, equippedSetIds: default);

            var payload = new TickStatePayload
            {
                CurrentLevel = 30,
                Aptitude_Strength = 22, // a mid-band bred value, not the default 4
                TraitMask = TraitRegistry.MaskOf(TraitRegistry.KeenEdge, TraitRegistry.HawkEye),
                Inherit_Damage = 5,
            };
            const int damageScalePerLevelPct = 12;

            long withAptitude = SimulationEngine.EffectiveMilliAttackFor(ref payload, in combatStats, damageScalePerLevelPct);
            var noAptitude = payload;
            noAptitude.Aptitude_Strength = BreedingAptitudes.StartingValue;
            long withoutAptitude = SimulationEngine.EffectiveMilliAttackFor(ref noAptitude, in combatStats, damageScalePerLevelPct);

            Assert.True(withAptitude > 0);
            Assert.True(withoutAptitude < withAptitude);
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

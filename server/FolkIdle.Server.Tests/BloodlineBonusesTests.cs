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
                Assert.Contains("BloodlineBonuses.ApplyAttack(", source);
                Assert.Contains("BloodlineBonuses.ApplyMaxHp(", source);
                Assert.Contains("BloodlineBonuses.GatherSpeedBonusPct(", source);
                Assert.Contains("BloodlineBonuses.GatherYieldBonusPct(", source);
                Assert.DoesNotContain("BonusPercentFor(payload.Aptitude_", source);
                Assert.DoesNotContain("LocusYield", source);
            }
        }

        /// <summary>
        /// Modul: a COMPUTED-VALUE parity test, not a grep. The grep test above
        /// (`LiveAndOfflineBothUseTheSharedFormulas`) would pass even if a
        /// future edit called `ApplyAttack(effective, 0, default)` or fed it
        /// the wrong aptitude - it only proves the function NAME appears in
        /// both files. This one drives the actual private methods each engine
        /// runs on a tick - `SimulationEngine.EffectiveMilliAttackFor` and
        /// `OfflineSimulationEngine.EffectiveMilliAttackFor`, both extracted
        /// specifically so a test can reach them without standing up a socket,
        /// a database or the 10Hz tick thread (see the reflection precedent in
        /// HardenedEngineIntegrationTests, e.g. ProcessAllSlotSubTicks) - with
        /// the SAME CombatStats, the SAME Strength aptitude and the SAME trait
        /// mask, and asserts they return the same milli-attack figure.
        ///
        /// This is exactly the case that broke: offline never applied
        /// Aptitude_Strength at all, so a bred line with real Strength killed
        /// noticeably slower away than online. Reverting the fix in either
        /// engine, or introducing a NEW divergence (wrong argument, wrong
        /// order, a different trait mask), fails this test on the computed
        /// number - not on whether a string appears in the source.
        /// </summary>
        [Fact]
        public void LiveAndOfflineComputeTheIdenticalEffectiveAttack()
        {
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
                GuildId = 0, // no guild buff to feed live's extra term - see EffectiveMilliAttackFor
            };
            const int damageScalePerLevelPct = 12;

            long liveAttack = InvokeEffectiveMilliAttackFor(
                typeof(SimulationEngine), payload, combatStats, damageScalePerLevelPct);
            long offlineAttack = InvokeEffectiveMilliAttackFor(
                typeof(OfflineSimulationEngine), payload, combatStats, damageScalePerLevelPct);

            Assert.True(liveAttack > 0);
            Assert.Equal(liveAttack, offlineAttack);

            // And the aptitude is not a no-op: strip it back to the starting
            // value and the figure must drop, proving this payload's Strength
            // of 22 was actually read by both paths rather than both engines
            // coincidentally agreeing on an unused default.
            var noAptitude = payload;
            noAptitude.Aptitude_Strength = BreedingAptitudes.StartingValue;
            long liveWithoutAptitude = InvokeEffectiveMilliAttackFor(
                typeof(SimulationEngine), noAptitude, combatStats, damageScalePerLevelPct);
            long offlineWithoutAptitude = InvokeEffectiveMilliAttackFor(
                typeof(OfflineSimulationEngine), noAptitude, combatStats, damageScalePerLevelPct);

            Assert.True(liveWithoutAptitude < liveAttack);
            Assert.Equal(liveWithoutAptitude, offlineWithoutAptitude);
        }

        private static long InvokeEffectiveMilliAttackFor(
            System.Type engineType, TickStatePayload payload, CombatStats combatStats, int damageScalePerLevelPct)
        {
            var method = engineType.GetMethod(
                "EffectiveMilliAttackFor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(method != null, $"{engineType.Name}.EffectiveMilliAttackFor not found - was it renamed?");

            object[] args = { payload, combatStats, damageScalePerLevelPct };
            var result = method!.Invoke(null, args);
            Assert.NotNull(result);
            return (long)result!;
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

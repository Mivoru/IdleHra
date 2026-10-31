using System.Linq;
using System.Text;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: A POTION MUST NOT LOWER A WALL (herbs and potions, 2026-10-31).
    /// The rare-fish heal bonus was dropped because x1.2 healing broke four
    /// boss walls; a combat potion is the same kind of term, bought with an
    /// hour of herbs, so each one is run through the wall projection the
    /// ladder is calibrated with: the set two quality tiers under a boss's
    /// requirement must still lose with the strong potion running. The report
    /// prints what each potion does to the fight at the requirement, and the
    /// permanent sources the reward potions are sized against.
    /// </summary>
    public class PotionMeasurementTests
    {
        private readonly ITestOutputHelper _output;

        public PotionMeasurementTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
            PotionRegistry.ResetForTests();
        }

        private static ReferenceLoadout Gear(int region, int qualityTier, AffixRarity rarity, int potionId)
            => new ReferenceLoadout(BossGearBenchmark.ReferenceLevelForRegion(region), region, qualityTier, rarity, default, potionId);

        [Fact]
        public void NoCombatPotionLetsTheSetTwoTiersUnderTheRequirementWin()
        {
            var table = new StringBuilder();
            var failures = new System.Collections.Generic.List<string>();
            table.AppendLine("region potion                      at req: kill s / die s      req-1        req-2");
            var combat = PotionRegistry.All.Where(p => p.Slot == PotionSlot.Combat).OrderBy(p => p.Kind).ThenBy(p => p.Strong).ToList();
            Assert.Equal(6, combat.Count);

            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
                int required = BossFirstClearRules.RequiredQualityTierFor(region);
                AffixRarity rarity = BossFirstClearRules.RequiredAffixRarityFor(region);

                foreach (int potionId in new[] { 0 }.Concat(combat.Select(p => p.ItemId)))
                {
                    var at = BossGearBenchmark.ProjectFirstClear(bossId, Gear(region, required, rarity, potionId));
                    var one = BossGearBenchmark.ProjectFirstClear(bossId, Gear(region, required - 1, rarity, potionId));
                    var two = BossGearBenchmark.ProjectFirstClear(bossId, Gear(region, required - 2, rarity, potionId));
                    string name = potionId == 0 ? "(none)" : ContentRegistry.GetItemBaseId(potionId).Replace("_defensive_potion_consumable", "");
                    table.AppendLine($"r{region}     {name,-27} {Fmt(at)}   {(one.PlayerWins ? "WIN " : "lose")}         {(two.PlayerWins ? "WIN " : "lose")}");

                    if (two.PlayerWins)
                        failures.Add($"region {region}: {name} lets quality {required - 2} beat the boss in {two.SecondsToKillBoss:F0}s - the potion lowers the wall.");
                }
            }

            _output.WriteLine(table.ToString());
            Assert.True(failures.Count == 0, string.Join(System.Environment.NewLine, failures) + System.Environment.NewLine + table);
        }

        [Fact]
        public void TheRewardPotions_AgainstThePermanentSources_Report()
        {
            var report = new StringBuilder();
            report.AppendLine("Permanent sources (always on):");
            report.AppendLine($"  shop pet {PetRegistry.ShopPetPct}%, monster/rare pet {PetRegistry.MonsterPetPct}%, boss pet {PetRegistry.BossPetPct}%, Black Serpent {PetRegistry.BlackSerpentPct}%");
            report.AppendLine("Potions (one hour of effect for ~10 min / ~20 min of picking):");
            foreach (var p in PotionRegistry.All.OrderBy(p => p.Slot).ThenBy(p => p.Kind).ThenBy(p => p.Strong))
            {
                report.AppendLine($"  {ContentRegistry.GetItemBaseId(p.ItemId),-50} {p.Magnitude,4}  {p.DurationMs / 60000} min");
            }
            _output.WriteLine(report.ToString());
        }

        private static string Fmt(BossFightProjection p)
            => $"{(double.IsInfinity(p.SecondsToKillBoss) ? "inf" : p.SecondsToKillBoss.ToString("F0")),7} / {(double.IsInfinity(p.SecondsToPlayerDeath) ? "inf" : p.SecondsToPlayerDeath.ToString("F0")),-7} {(p.PlayerWins ? "WIN " : "lose")}";
    }
}

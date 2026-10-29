using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The race page the Wiki and the mastery panel show (GET /api/v1/races).
    /// It is built from the constants the fight reads, so these tests hold the
    /// TEXT to the resolver the simulation calls: a sentence that names a
    /// number the resolver does not return is the drift this registry exists
    /// to prevent.
    /// </summary>
    public class RaceBonusRegistryTests
    {
        [Fact]
        public void All_six_races_are_described_in_id_order()
        {
            var races = RaceBonusRegistry.Describe();
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, races.Select(r => r.RaceId).ToArray());
            Assert.All(races, r =>
            {
                Assert.False(string.IsNullOrWhiteSpace(r.Name));
                Assert.NotEmpty(r.Innate);
                Assert.NotEmpty(r.Mastery);
            });
        }

        [Fact]
        public void Every_mastery_line_matches_what_the_resolver_pays_at_its_level()
        {
            var byId = RaceBonusRegistry.Describe().ToDictionary(r => r.RaceId);

            void Says(int raceId, int level, string number)
            {
                var line = byId[raceId].Mastery.Single(m => m.Level == level);
                Assert.Contains(number, line.Text);
            }

            Says(RaceIds.Human, RaceBonusRegistry.HumanXpFirstLevel, $"{RaceMasteryResolver.GetHumanXpBonusPct(RaceBonusRegistry.HumanXpFirstLevel)}%");
            Says(RaceIds.Human, RaceBonusRegistry.HumanXpSecondLevel, $"{RaceMasteryResolver.GetHumanXpBonusPct(RaceBonusRegistry.HumanXpSecondLevel)}%");
            Says(RaceIds.Human, RaceBonusRegistry.HumanVaultLevel, $"+{RaceMasteryResolver.GetHumanVaultBonusSlots(RaceBonusRegistry.HumanVaultLevel)} backpack");
            Says(RaceIds.Vila, RaceBonusRegistry.VilaCritLevel, $"+{RaceMasteryResolver.GetVilaCritBonusPct(RaceBonusRegistry.VilaCritLevel):0.##} crit");
            Says(RaceIds.Draugr, RaceBonusRegistry.DraugrLifestealFirstLevel, $"{RaceMasteryResolver.GetDraugrLifestealBonusPct(RaceBonusRegistry.DraugrLifestealFirstLevel):0.##}% lifesteal");
            Says(RaceIds.Draugr, RaceBonusRegistry.DraugrLifestealSecondLevel, $"{RaceMasteryResolver.GetDraugrLifestealBonusPct(RaceBonusRegistry.DraugrLifestealSecondLevel):0.##}% lifesteal");
            Says(RaceIds.Kobold, RaceBonusRegistry.KoboldMiningLevel, $"+{RaceMasteryResolver.GetKoboldOreDuplicationBonusPct(RaceBonusRegistry.KoboldMiningLevel):0.##}%");
            Says(RaceIds.Moosleute, RaceBonusRegistry.MoosleuteHarvestLevel, $"+{RaceMasteryResolver.GetMoosleuteDoubleHarvestBonusPct(RaceBonusRegistry.MoosleuteHarvestLevel):0.##}%");
            Says(RaceIds.Vodnik, RaceBonusRegistry.VodnikOfflineLevel,
                $"{RaceMasteryResolver.GetVodnikExtendedOfflineSeconds(RaceBonusRegistry.VodnikOfflineLevel, 0) / 3600} hours");

            // Below a milestone the resolver pays nothing - the line is a
            // threshold, not a ramp, and the page says "Level N".
            Assert.Equal(0f, RaceMasteryResolver.GetDraugrLifestealBonusPct(RaceBonusRegistry.DraugrLifestealFirstLevel - 1));
        }

        [Fact]
        public void The_page_never_promises_the_ranged_damage_combat_does_not_read()
        {
            var fairy = RaceBonusRegistry.Describe().Single(r => r.RaceId == RaceIds.Vila);
            Assert.DoesNotContain(fairy.Innate, line => line.Contains("ranged", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Only_fairy_and_draugr_mastery_is_tied_to_the_race_fighting()
        {
            // StatsCalculator applies these two for the ACTIVE race only; the
            // other four are read off the account's mastery level.
            var scopes = RaceBonusRegistry.Describe().ToDictionary(r => r.RaceId, r => r.MasteryAccountWide);
            Assert.False(scopes[RaceIds.Vila]);
            Assert.False(scopes[RaceIds.Draugr]);
            Assert.True(scopes[RaceIds.Human]);
            Assert.True(scopes[RaceIds.Kobold]);
            Assert.True(scopes[RaceIds.Vodnik]);
            Assert.True(scopes[RaceIds.Moosleute]);
        }
    }
}

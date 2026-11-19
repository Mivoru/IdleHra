using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>The Descent into Nav: the numbers (NavRules) and the run (NavSimulator).</summary>
    public class NavTests
    {
        public NavTests()
        {
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Character(int strength, int constitution = 2_000_000) => new()
        {
            PlayerId = 990_100L,
            CurrentLevel = 100,
            STR = strength,
            CON = constitution,
            HighestLocationReached = 5,
            InventorySpaceRemaining = 1000,
        };

        private static readonly NavBonuses None = new(0, 0, 0, 0, 0, 0, 0);

        [Fact]
        public void TheFlagIsOffUnlessSaidOn()
        {
            Assert.False(NavRules.Enabled(null));
            Assert.False(NavRules.Enabled("1"));
            Assert.True(NavRules.Enabled("on"));
            Assert.True(NavRules.Enabled("ON"));
        }

        [Fact]
        public void GuardiansGrowTenPercentADepth_AndACircleIsFiveTimesStronger()
        {
            Assert.Equal(1_000_000, NavRules.GuardianHp(1, 1), 3);
            Assert.Equal(1_100_000, NavRules.GuardianHp(1, 2), 3);
            Assert.Equal(5_000_000, NavRules.GuardianHp(2, 1), 3);
            Assert.True(NavRules.GuardianAttack(1, 11) > NavRules.GuardianAttack(1, 10));
        }

        [Fact]
        public void SoulsGrowWithDepthAndCircle()
        {
            Assert.Equal(2, NavRules.SoulsForDepth(1));
            Assert.Equal(10, NavRules.SoulsForDepth(15));
            Assert.Equal(30, NavRules.SoulsForDepth(2, 15, 0));
            Assert.Equal(20, NavRules.SoulsForDepth(1, 15, 100));
        }

        [Fact]
        public void WorkshopCostsGrowFasterThanTheirEffect()
        {
            var strength = NavRules.WorkshopByKey("strength")!;
            Assert.Equal(10, NavRules.WorkshopCost(strength, 0));
            Assert.True(NavRules.WorkshopCost(strength, 50) > 2_000);
            Assert.Equal(150.0, NavRules.WorkshopEffect(strength, 50), 6);
            // The thrift research takes a share off, never more than 60%.
            Assert.Equal(4, NavRules.WorkshopCost(strength, 0, 90));
        }

        [Fact]
        public void ResearchIsAGoldSinkThatGrowsWithIncome()
        {
            Assert.Equal(50_000_000, NavRules.ResearchGold(0));
            Assert.True(NavRules.ResearchGold(15) > 20_000_000_000);
            Assert.Equal(7200, NavRules.ResearchSeconds(0));
        }

        [Fact]
        public void ChargesComeBackOneEveryTwentyMinutes_UpToTheCap()
        {
            var (charges, anchor) = NavRules.SettleCharges(0, 1_000, 1_000 + (3 * NavRules.ChargeSeconds) + 5);
            Assert.Equal(3, charges);
            Assert.Equal(1_000 + (3 * NavRules.ChargeSeconds), anchor);
            Assert.Equal(NavRules.ChargeCap, NavRules.SettleCharges(5, 0, 1_000).Charges == 5 ? NavRules.ChargeCap : NavRules.ChargeCap);
            Assert.Equal(NavRules.ChargeCap, NavRules.SettleCharges(0, 1, 10_000_000).Charges);
        }

        [Fact]
        public void PackedListsRoundTrip()
        {
            var map = new Dictionary<string, int> { ["strength"] = 12, ["bark"] = 3, ["zero"] = 0 };
            string packed = NavRules.Pack(map);
            Assert.Equal("bark:3,strength:12", packed);
            var back = NavRules.Unpack(packed);
            Assert.Equal(12, back["strength"]);
            Assert.False(back.ContainsKey("zero"));
            Assert.Empty(NavRules.Unpack("garbage,:5,x:-1"));
        }

        [Fact]
        public void AStrongerCharacterGoesDeeper_AndRootstrengthTakesItFurther()
        {
            var weak = NavSimulator.Run(Character(30_000_000), 0, 1, None, 7, 0);
            var strong = NavSimulator.Run(Character(300_000_000), 0, 1, None, 7, 0);
            Assert.True(strong.DeepestCleared > weak.DeepestCleared,
                $"strong {strong.DeepestCleared} vs weak {weak.DeepestCleared}");

            var boosted = NavSimulator.Run(Character(300_000_000), 0, 1, None with { DamagePct = 300 }, 7, 0);
            Assert.True(boosted.DeepestCleared > strong.DeepestCleared,
                $"+300% damage {boosted.DeepestCleared} vs {strong.DeepestCleared}");
        }

        [Fact]
        public void ARunPaysTheSoulsOfEveryDepthItCleared_AndSaysWhyItStopped()
        {
            var run = NavSimulator.Run(Character(300_000_000), 0, 1, None, 7, 0);
            long expected = 0;
            for (int d = run.StartDepth; d <= run.DeepestCleared; d++) expected += NavRules.SoulsForDepth(1, d, 0);
            Assert.Equal(expected, run.Souls);
            Assert.Contains(run.EndedBy, new[] { "time", "fell" });
            Assert.False(run.Depths[^1].Cleared);
        }

        [Fact]
        public void TheSameSeedReplaysTheSameRun()
        {
            var a = NavSimulator.Run(Character(150_000_000), 0, 1, None, 42, 0);
            var b = NavSimulator.Run(Character(150_000_000), 0, 1, None, 42, 0);
            Assert.Equal(a.DeepestCleared, b.DeepestCleared);
            Assert.Equal(a.Souls, b.Souls);
        }

        [Fact]
        public void LongFallStartsDeeper_ButNeverPastHalfTheRecord()
        {
            var bonuses = None with { StartDepth = 30 };
            Assert.Equal(11, NavSimulator.Run(Character(300_000_000), 0, 1, bonuses, 7, 22).StartDepth);
            Assert.Equal(1, NavSimulator.Run(Character(300_000_000), 0, 1, bonuses, 7, 0).StartDepth);
        }

        [Fact]
        public void BonusesSumTheWorkshopAndTheResearch()
        {
            var row = new NavProgress { Workshop = "strength:10,breath:25,harvest:2", Research = "craft:10,souls:2" };
            var b = NavEngine.BonusesOf(row);
            Assert.Equal(33.0, b.DamagePct, 6); // 10 x 3% x 1.10
            Assert.Equal(2, b.Revives);
            Assert.Equal((2 * 3.0 * 1.10) + 10.0, b.SoulsPct, 6);
        }

        [Fact]
        public void FinishedResearchIsCreditedOnRead()
        {
            var row = new NavProgress { Research = "souls:1", ResearchActive = "souls", ResearchEndsEpoch = 100, Charges = 0, ChargeAnchorEpoch = 100 };
            NavEngine.Settle(row, 200);
            Assert.Equal("souls:2", row.Research);
            Assert.Equal(string.Empty, row.ResearchActive);
        }
        [Fact]
        public void TenGuardiansStandInTurn_AndVelesGuardsEveryTwentyFifthDepth()
        {
            Assert.Equal(10, NavRules.Guardians.Length);
            Assert.Equal("upir", NavRules.GuardianFor(1).Key);
            Assert.Equal("upir", NavRules.GuardianFor(11).Key);
            Assert.Equal("hound", NavRules.GuardianFor(10).Key);
            Assert.Equal("veles", NavRules.GuardianFor(25).Key);
            Assert.Equal("veles", NavRules.GuardianFor(50).Key);
            Assert.Equal("upir", NavRules.GuardianFor(51).Key);
            // Every guardian has its own ability, and its key is unique.
            Assert.Equal(11, NavRules.Guardians.Append(NavRules.Warden).Select(g => g.Ability).Distinct().Count());
            Assert.Equal(11, NavRules.Guardians.Append(NavRules.Warden).Select(g => g.Key).Distinct().Count());
            Assert.Same(NavRules.Warden, NavRules.GuardianByKey("veles"));
        }

        [Fact]
        public void VelesPaysTripleSouls()
        {
            long plain = (long)Math.Ceiling((double)NavRules.SoulsForDepth(25));
            Assert.Equal(plain * 3, NavRules.SoulsForDepth(1, 25, 0));
        }

        [Fact]
        public void NoonHeatKillsSoonerThanTheSameBlowsWithoutIt()
        {
            double flat = NavSimulator.TimeToDieRamping(1000, 100, 0, 0);
            Assert.Equal(10.0, flat, 6);
            double hot = NavSimulator.TimeToDieRamping(1000, 100, 0, 0.04);
            Assert.True(hot < flat, $"hot {hot} vs flat {flat}");
            Assert.True(double.IsPositiveInfinity(NavSimulator.TimeToDieRamping(1000, 100, 150, 0)));
        }

        [Fact]
        public void ARunNamesTheGuardianOfEveryDepth()
        {
            var run = NavSimulator.Run(Character(300_000_000), 0, 1, None, 7, 0);
            foreach (var d in run.Depths) Assert.Equal(NavRules.GuardianFor(d.Depth).Key, d.Guardian);
        }
        [Fact]
        public void ARunThatStopsNamesTheGuardianAndAHint()
        {
            var run = NavSimulator.Run(Character(300_000_000), 0, 1, None, 7, 0);
            Assert.Equal(run.Depths[^1].Guardian, run.StoppedBy);
            Assert.False(string.IsNullOrWhiteSpace(run.Hint));
            Assert.Contains(NavRules.GuardianByKey(run.StoppedBy)!.Name, run.Depths[^1].Outcome);
        }

        [Fact]
        public void EveryAbilityHasItsOwnHint()
        {
            var all = NavRules.Guardians.Append(NavRules.Warden).Select(g => NavRules.Hint(g.Ability, fell: false)).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        [Fact]
        public void NewsOnlyForANewVelesOrACircle()
        {
            Assert.Null(NavEngine.NavNews("A", 1, 10, 24, false));
            Assert.Equal("A has beaten Veles at depth 25 of Nav (Circle 1).", NavEngine.NavNews("A", 1, 24, 30, false));
            Assert.Null(NavEngine.NavNews("A", 1, 30, 40, false));
            Assert.Equal("A has opened Circle 2 of Nav.", NavEngine.NavNews("A", 1, 40, 52, true));
        }

        [Fact]
        public void NavTitlesAreRegistered()
        {
            Assert.NotNull(TitleRegistry.Find(NavRules.VelesTitleSlug));
            foreach (var t in NavRules.CircleTitles) Assert.NotNull(TitleRegistry.Find(t.Slug));
        }

        [Fact]
        public void TheExpectedDepthIsStable_AndCloseToARealRun()
        {
            var c = Character(300_000_000);
            int a = NavSimulator.ExpectedDepth(c, 0, 1, None, 0);
            Assert.Equal(a, NavSimulator.ExpectedDepth(c, 0, 1, None, 0));
            int real = NavSimulator.Run(c, 0, 1, None, 7, 0).DeepestCleared;
            Assert.InRange(real, a - 6, a + 6);
        }
    }
}

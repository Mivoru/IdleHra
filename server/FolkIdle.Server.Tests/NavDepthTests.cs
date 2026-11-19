using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Nav's second pass (2026-11-19): banes, the nine research lines, the
    /// spirits of the bench, the weekly omen and board, the Souls frames and
    /// the outward research the tick reads. Pure rules here; the database
    /// paths are in <see cref="NavDepthDbTests"/>.
    /// </summary>
    public class NavDepthTests
    {
        public NavDepthTests()
        {
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Character(int strength, int constitution = 2_000_000) => new()
        {
            PlayerId = 990_200L,
            CurrentLevel = 100,
            STR = strength,
            CON = constitution,
            HighestLocationReached = 5,
            InventorySpaceRemaining = 1000,
        };

        private static readonly NavBonuses None = new(0, 0, 0, 0, 0, 0, 0);

        [Fact]
        public void EveryGuardianButVelesHasOneBane_InTheWorkshop()
        {
            foreach (var g in NavRules.Guardians)
            {
                var bane = NavRules.BaneFor(g.Key);
                Assert.NotNull(bane);
                Assert.Equal(NavRules.BaneMaxLevel, bane!.MaxLevel);
                Assert.Same(bane, NavRules.WorkshopByKey(bane.Key));
            }
            Assert.Null(NavRules.BaneFor("veles"));
            Assert.Equal(10, NavRules.Workshop.Count(w => w.Guardian.Length > 0));
        }

        [Fact]
        public void AGuardianIsMetByItsBit()
        {
            int met = 0;
            Assert.False(NavRules.HasMet(met, "navka"));
            met |= 1 << NavRules.GuardianBit("navka");
            Assert.True(NavRules.HasMet(met, "navka"));
            Assert.False(NavRules.HasMet(met, "upir"));
            Assert.Equal(-1, NavRules.GuardianBit("veles"));
        }

        [Fact]
        public void TheOmenStrengthensOneRule_ABaneWearsItDown_AndThreeQuartersIsTheMost()
        {
            Assert.Equal(1.0, NavRules.AbilityStrength(0, false), 6);
            Assert.Equal(1.5, NavRules.AbilityStrength(0, true), 6);
            Assert.Equal(0.4, NavRules.AbilityStrength(60, false), 6);
            Assert.Equal(0.25, NavRules.AbilityStrength(500, false), 6);
            var b = None with { BaneReductionPct = new Dictionary<string, double> { ["upir"] = 40 }, Restless = "upir" };
            Assert.Equal(0.9, b.StrengthOf("upir"), 6);
            Assert.Equal(1.0, b.StrengthOf("mora"), 6);
        }

        [Fact]
        public void BanesOnEveryGuardianTakeARunDeeper()
        {
            var c = Character(300_000_000);
            int plain = NavSimulator.Run(c, 0, 1, None, 7, 0).DeepestCleared;
            var all = NavRules.Guardians.ToDictionary(g => g.Key, _ => 60.0);
            int baned = NavSimulator.Run(c, 0, 1, None with { BaneReductionPct = all }, 7, 0).DeepestCleared;
            Assert.True(baned > plain, $"baned {baned} vs plain {plain}");
        }

        [Fact]
        public void TheRestlessGuardianPaysDoubleSouls()
        {
            Assert.Equal(NavRules.SoulsForDepth(1, 13, 0) * 2, NavRules.SoulsForDepth(1, 13, 0, restless: true));
            var c = Character(300_000_000);
            var run = NavSimulator.Run(c, 0, 1, None with { Restless = "upir" }, 7, 0);
            var depth1 = run.Depths.First(d => d.Depth == 1);
            Assert.Equal(NavRules.SoulsForDepth(1, 1, 0, restless: true), depth1.Souls);
        }

        [Fact]
        public void TheRestlessGuardianTurnsEveryWeek_AndTheWeekEndsOnMonday()
        {
            long sunday = new DateTimeOffset(2026, 11, 22, 23, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            long monday = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            int w = NavRules.WeekOf(sunday);
            Assert.Equal(w + 1, NavRules.WeekOf(monday));
            Assert.Equal(monday, NavRules.WeekEndsEpoch(w));
            Assert.NotEqual(NavRules.RestlessGuardian(w).Key, NavRules.RestlessGuardian(w + 1).Key);
            Assert.Equal(NavRules.RestlessGuardian(w).Key, NavRules.RestlessGuardian(w + 10).Key);
        }

        [Fact]
        public void SpiritsAreTheBestBenchedAdults_EachAptitudeItsOwnBonus()
        {
            var bench = new List<NavRules.Spirit>
            {
                new("A", 50, 50, 50, 50),
                new("B", 20, 20, 20, 20),
                new("C", 35, 24, 34, 34),
                new("D", 50, 49, 50, 50),
                new("E", 4, 4, 4, 4),
            };
            var three = NavRules.ChooseSpirits(bench, 3);
            Assert.Equal(new[] { "A", "D", "C" }, three.Select(s => s.Name));
            var (d, h, sp, so) = NavRules.SpiritBonus(new[] { bench[0] });
            Assert.Equal(12.0, d, 6);
            Assert.Equal(12.0, h, 6);
            Assert.Equal(3.0, sp, 6);
            Assert.Equal(12.0, so, 6);
            Assert.Equal((0.0, 0.0, 0.0, 0.0), NavRules.SpiritBonus(new[] { bench[1], bench[4] }));
            // Spirit bond scales them.
            Assert.Equal(24.0, NavRules.SpiritBonus(new[] { bench[0] }, 100).Damage, 6);
        }

        [Fact]
        public void BonusesCarryTheSpirits_TheBanes_AndTheOmen()
        {
            var row = new NavProgress { Workshop = "strength:10,bane_upir:5,bond:0", Research = "call:1" };
            var bench = Enumerable.Range(0, 5).Select(i => new NavRules.Spirit($"S{i}", 50, 20, 20, 20)).ToList();
            long now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var b = NavEngine.BonusesOf(row, bench, now);
            // 30 + four spirits (three plus Ancestors' call) x 12.
            Assert.Equal(30 + 48, b.DamagePct, 6);
            Assert.Equal(20.0, b.BaneReductionPct!["upir"], 6);
            Assert.Equal(NavRules.RestlessGuardian(NavRules.WeekOf(now)).Key, b.Restless);
            Assert.Equal(4, NavEngine.SpiritCount(row));
        }

        [Fact]
        public void ResearchHasNineLines_TheOutwardOnesOpenWithTheCircles()
        {
            Assert.Equal(9, NavRules.Research.Length);
            var bounty = NavRules.ResearchByKey("bounty")!;
            var luck = NavRules.ResearchByKey("luck")!;
            Assert.True(bounty.Outward && luck.Outward);
            Assert.Equal(-1, NavRules.ResearchCap(bounty, 1));
            Assert.Equal(10, NavRules.ResearchCap(bounty, 2));
            Assert.Equal(20, NavRules.ResearchCap(bounty, 3));
            Assert.Equal(25, NavRules.ResearchCap(bounty, 9));
            Assert.Equal(-1, NavRules.ResearchCap(luck, 2));
            Assert.Equal(10, NavRules.ResearchCap(luck, 3));
            Assert.Equal(0, NavRules.ResearchCap(NavRules.ResearchByKey("souls")!, 1));
            Assert.Equal(30, NavRules.ResearchCap(NavRules.ResearchByKey("thrift")!, 1));
            Assert.Equal(2 * NavRules.ResearchGold(3), NavRules.ResearchGold(bounty, 3));
            Assert.Equal(NavRules.ResearchGold(3), NavRules.ResearchGold(NavRules.ResearchByKey("souls")!, 3));
        }

        [Fact]
        public void DeepWellAndSwiftReturnChangeTheCharges()
        {
            Assert.Equal((12, 1200), NavRules.ChargeRules(0, 0));
            Assert.Equal((18, 720), NavRules.ChargeRules(6, 8));
            Assert.Equal((18, 720), NavRules.ChargeRules(60, 80));
            var (charges, _) = NavRules.SettleCharges(0, 1000, 1000 + (720 * 20), 18, 720);
            Assert.Equal(18, charges);
            var row = new NavProgress { Research = "well:2,swift:4", Charges = 0, ChargeAnchorEpoch = 1000 };
            NavEngine.Settle(row, 1000 + (960 * 3));
            Assert.Equal(3, row.Charges);
        }

        [Fact]
        public void AWeekRollsItsBestIntoTheLastWeek()
        {
            var row = new NavProgress { WeekId = 100, WeekCircle = 1, WeekDepth = 30 };
            NavEngine.RollWeek(row, 101);
            Assert.Equal((101, 0, 0), (row.WeekId, row.WeekCircle, row.WeekDepth));
            Assert.Equal((1, 30), NavEngine.WeekResult(row, 100));
            Assert.Equal((0, 0), NavEngine.WeekResult(row, 99));
            // An empty week does not wipe the week before it.
            NavEngine.RollWeek(row, 102);
            Assert.Equal((1, 30), NavEngine.WeekResult(row, 100));
        }

        [Fact]
        public void NavFramesAreBoundCosmetics_BehindAPlaceInNav()
        {
            foreach (var f in NavRules.Frames)
            {
                var def = CosmeticRegistry.Find(f.Id);
                Assert.NotNull(def);
                Assert.True(def!.Bound);
                Assert.Equal(CosmeticKind.Frame, def.Kind);
            }
            var roots = NavRules.FrameById("frame_nav_roots")!;
            var crown = NavRules.FrameById("frame_nav_crown")!;
            Assert.False(NavEngine.FrameReached(new NavProgress { BestDepths = "1:9" }, roots));
            Assert.True(NavEngine.FrameReached(new NavProgress { BestDepths = "1:10" }, roots));
            Assert.False(NavEngine.FrameReached(new NavProgress { BestDepths = "1:49", HighestCircle = 1 }, crown));
            Assert.True(NavEngine.FrameReached(new NavProgress { BestDepths = "1:50,2:25", HighestCircle = 2 }, crown));
            Assert.True(NavEngine.FrameReached(new NavProgress { BestDepths = "1:50,2:50,3:1", HighestCircle = 3 }, crown));
        }

        [Fact]
        public void TheWeeklyTitleIsRegistered()
        {
            Assert.NotNull(TitleRegistry.Find(NavRules.WeeklyTitleSlug));
        }

        [Fact]
        public void TheOutwardResearchReachesTheTick_AndTheFlagClosesIt()
        {
            const long id = 990_211L;
            string? before = Environment.GetEnvironmentVariable("FOLKIDLE_NAV");
            try
            {
                var row = new NavProgress { PlayerId = id, Research = "bounty:5,memory:3", ResearchActive = "luck", ResearchEndsEpoch = 1 };
                NavBlessingsCache.Update(row);
                Environment.SetEnvironmentVariable("FOLKIDLE_NAV", "on");
                Assert.Equal(5.0, NavBlessingsCache.GoldPct(id), 6);
                Assert.Equal(3, NavBlessingsCache.XpPct(id));
                // The running line's timer is long over: it counts before Nav is opened again.
                Assert.Equal(0.5f, NavBlessingsCache.LootLuckPct(id));
                var payload = new TickStatePayload { PlayerId = id };
                Assert.Equal(1.05f, CombatGoldReward.FactorsFor(in payload, 0f).Nav, 4);
                Environment.SetEnvironmentVariable("FOLKIDLE_NAV", null);
                Assert.Equal(0.0, NavBlessingsCache.GoldPct(id), 6);
                Assert.Equal(1f, CombatGoldReward.FactorsFor(in payload, 0f).Nav);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FOLKIDLE_NAV", before);
                NavBlessingsCache.Update(new NavProgress { PlayerId = id });
            }
        }
    }

    /// <summary>Nav's database paths: the bane lock, the frame shop, the bench and the weekly payout.</summary>
    [Collection("Postgres collection")]
    public class NavDepthDbTests
    {
        private readonly PostgresTestFixture _fixture;

        public NavDepthDbTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task SeedAsync(long playerId, Action<NavProgress> shape)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            if (!await db.PlayerRecords.AnyAsync(p => p.Id == playerId))
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = $"nav{playerId}" });
            var row = await db.NavProgress.FirstOrDefaultAsync(n => n.PlayerId == playerId);
            if (row == null) { row = new NavProgress { PlayerId = playerId }; db.NavProgress.Add(row); }
            shape(row);
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task ABaneStaysLockedUntilItsGuardianHasStoppedARun()
        {
            const long id = 960_440_001L;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await SeedAsync(id, r => { r.Souls = 10_000; r.GuardiansMet = 0; r.Workshop = string.Empty; });
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (result, _) = await NavEngine.BuyAsync(db, id, "bane_navka", 1, now);
                Assert.Equal(NavBuyResult.Locked, result);
            }
            await SeedAsync(id, r => r.GuardiansMet = 1 << NavRules.GuardianBit("navka"));
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (result, level) = await NavEngine.BuyAsync(db, id, "bane_navka", 3, now);
                Assert.Equal(NavBuyResult.Bought, result);
                Assert.Equal(3, level);
            }
        }

        [Fact]
        public async Task AFrameCostsSouls_Once_AndOnlyWhereYouHaveBeen()
        {
            const long id = 960_440_002L;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await SeedAsync(id, r => { r.Souls = 7_000; r.BestDepths = "1:9"; });
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                Assert.Equal(NavFrameResult.NotReached, await NavEngine.BuyFrameAsync(db, id, "frame_nav_roots", now, DateTime.UtcNow));
            await SeedAsync(id, r => r.BestDepths = "1:12");
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                Assert.Equal(NavFrameResult.Bought, await NavEngine.BuyFrameAsync(db, id, "frame_nav_roots", now, DateTime.UtcNow));
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(NavFrameResult.Owned, await NavEngine.BuyFrameAsync(db, id, "frame_nav_roots", now, DateTime.UtcNow));
                Assert.Equal(2_000, (await db.NavProgress.AsNoTracking().SingleAsync(n => n.PlayerId == id)).Souls);
                Assert.True(await db.CosmeticItems.AnyAsync(c => c.PlayerId == id && c.DefinitionId == "frame_nav_roots"));
            }
        }

        [Fact]
        public async Task OutwardResearchWaitsForItsCircle_AndCostsDouble()
        {
            const long id = 960_440_003L;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await SeedAsync(id, r => { r.HighestCircle = 1; r.ResearchActive = string.Empty; r.Research = string.Empty; });
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = id, ItemId = "gold", Quantity = 1_000_000_000 });
                await db.SaveChangesAsync();
                Assert.Equal(NavResearchResult.NotOpen, (await NavEngine.ResearchAsync(db, id, "bounty", now)).Result);
            }
            await SeedAsync(id, r => r.HighestCircle = 2);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var (result, gold) = await NavEngine.ResearchAsync(db, id, "bounty", now);
                Assert.Equal(NavResearchResult.Started, result);
                Assert.Equal(100_000_000, gold);
            }
        }

        [Fact]
        public async Task TheBenchIsAdultsOutsideTheSlots()
        {
            const long id = 960_440_004L;
            await SeedAsync(id, _ => { });
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                void Add(int slot, int age, int apt)
                {
                    var c = new CharacterRecord { Id = Guid.NewGuid(), PlayerId = id, Name = $"c{slot}", SlotIndex = slot, AgePhase = age };
                    db.CharacterRecords.Add(c);
                    db.CharacterLineages.Add(new CharacterLineageRegistry { CharacterId = c.Id, AptitudeStrength = apt, AptitudeSkill = apt, AptitudeEndurance = apt, AptitudeFortune = apt });
                }
                Add(0, 1, 50);   // in a slot: not a spirit
                Add(5, 1, 40);   // benched adult
                Add(6, 0, 50);   // benched child: not yet
                Add(7, 2, 30);   // benched senior
                await db.SaveChangesAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var bench = await NavEngine.LoadBenchAsync(db, id);
                Assert.Equal(new[] { 30, 40 }, bench.Select(b => b.Strength).OrderBy(x => x));
            }
        }

        [Fact]
        public async Task LastWeeksTopThreeArePaidOnce_WhenTwoOrMoreDescended()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int week = NavRules.WeekOf(now);
            int last = week - 1;
            const long a = 960_440_011L, b = 960_440_012L, lone = 960_440_013L;
            int lonely = week - 3;
            await SeedAsync(a, r => { r.WeekId = last; r.WeekCircle = 1; r.WeekDepth = 30; r.WeekRewardSettledId = 0; });
            await SeedAsync(b, r => { r.WeekId = last; r.WeekCircle = 1; r.WeekDepth = 20; r.WeekRewardSettledId = 0; });
            await SeedAsync(lone, r => { r.WeekId = lonely; r.WeekCircle = 1; r.WeekDepth = 40; r.WeekRewardSettledId = 0; });

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var lines = await NavEngine.SettleWeekAsync(db, a, now, DateTime.UtcNow);
                Assert.Contains(lines, l => l.StartsWith("#1 deepest", StringComparison.Ordinal));
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Empty(await NavEngine.SettleWeekAsync(db, a, now, DateTime.UtcNow));
                var lines = await NavEngine.SettleWeekAsync(db, b, now, DateTime.UtcNow);
                Assert.Contains(lines, l => l.StartsWith("#2 deepest", StringComparison.Ordinal));
                // Alone in its week: placed first, paid nothing.
                Assert.Empty(await NavEngine.SettleWeekAsync(db, lone, now, DateTime.UtcNow));
                var row = await db.NavProgress.AsNoTracking().SingleAsync(n => n.PlayerId == lone);
                Assert.Equal(lonely, row.WeekRewardSettledId);
            }
        }
    }
}

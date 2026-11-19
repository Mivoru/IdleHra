using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The equipment dungeon's run as the REST route performs it: a ticket
    /// leaves in the same transaction as the reward lands, a locked stage or an
    /// empty ticket shelf spends nothing, and a first clear pays once.
    /// </summary>
    [Collection("Postgres collection")]
    public class DungeonRunTests
    {
        private readonly PostgresTestFixture _fixture;

        public DungeonRunTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private static TickStatePayload Strong(long playerId)
        {
            byte mask = 0;
            for (int region = 1; region <= 2; region++)
                mask = BossFirstClearRules.MarkDefeated(mask, RaceUnlockRegistry.GetRegionBossMonsterId(region));
            return new TickStatePayload
            {
                PlayerId = playerId,
                CurrentLevel = 60,
                STR = 2000, DEX = 2000, CON = 2000, LCK = 200,
                AutoEatThreshold = 50,
                DefeatedRegionBossMask = mask,
            };
        }

        [Fact]
        public async Task StageFifteenPaysEachGatesTitleOnceAndOnlyOnAWin()
        {
            const long playerId = 950_420_015L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }
            var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            // Stage 15 pays the boss's title, the last stage (20) its second one.
            var titled = new (int Stage, Func<DungeonBoss, string> Slug, Func<DungeonBoss, string> Name)[]
            {
                (DungeonSimulator.TitleStage, b => b.TitleSlug, b => b.TitleName),
                (DungeonSimulator.StageCount, b => b.LegendTitleSlug, b => b.LegendTitleName),
            };
            foreach (var kind in DungeonBoss.All)
            foreach (var (stage, slugOf, nameOf) in titled)
            {
                var boss = DungeonBoss.For(kind);
                Assert.Equal(nameOf(boss), TitleRegistry.DisplayNameFor(slugOf(boss)));
                Assert.Contains($"\"{nameOf(boss)}\"", DungeonEngine.RewardLine(kind, stage));
                Assert.DoesNotContain($"\"{nameOf(boss)}\"", DungeonEngine.RewardLine(kind, stage - 1));

                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    var lost = await DungeonEngine.FinishLiveAsync(db, playerId, kind, stage, false, default, now, new Random(1));
                    Assert.Equal(string.Empty, lost.Reward?.Title ?? string.Empty);
                }
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    var won = await DungeonEngine.FinishLiveAsync(db, playerId, kind, stage, true, default, now, new Random(1));
                    Assert.Equal(nameOf(boss), won.Reward!.Title);
                }
                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    var again = await DungeonEngine.FinishLiveAsync(db, playerId, kind, stage, true, default, now, new Random(2));
                    Assert.Equal(string.Empty, again.Reward!.Title);
                }
            }
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var slugs = await verify.PlayerTitles.AsNoTracking().Where(t => t.PlayerId == playerId).Select(t => t.TitleSlug).ToListAsync();
            var expected = DungeonBoss.All.SelectMany(k => new[] { DungeonBoss.For(k).TitleSlug, DungeonBoss.For(k).LegendTitleSlug });
            Assert.Equal(expected.OrderBy(x => x), slugs.OrderBy(x => x));
        }

        /// <summary>
        /// Stages 16-20 (2026-11-19) grow only in body: every ability holds its
        /// stage-15 strength, and stages 1-15 are exactly what they were when
        /// fifteen was the last - the ramps are anchored to AbilityTopStage.
        /// </summary>
        [Fact]
        public void TheLegendBandKeepsTheAbilitiesOfStageFifteenAndOnlyGrowsTheBoss()
        {
            Assert.Equal(25, BabaYaga.MortarDodgePct(15), 3);
            Assert.Equal(25, BabaYaga.MortarDodgePct(20), 3);
            Assert.Equal(10, BabaYaga.HexEverySeconds(15), 3);
            Assert.Equal(10, BabaYaga.HexEverySeconds(20), 3);
            Assert.Equal(40, Zmey.HidePct(20), 3);
            for (int stage = 16; stage <= DungeonSimulator.StageCount; stage++)
            {
                var (hpBefore, atkBefore, _) = DungeonSimulator.BossStrength(stage - 1);
                var (hp, atk, _) = DungeonSimulator.BossStrength(stage);
                Assert.InRange(atk / (double)atkBefore, 1.34, 1.36);
                Assert.True(hp > hpBefore);
            }
            // The dungeon's body is the pre-retune Malakor A10 (x152), not today's
            // endgame A10 (x1,500): the retune must not move a single stage.
            var anchor = BossAscensionRegistry.DungeonAnchor();
            Assert.InRange(1 + anchor.AttackPct / 100.0, 151, 153);
            Assert.True(BossAscensionRegistry.ModifiersFor(5, 10).AttackPct > 5 * anchor.AttackPct);
            Assert.Equal("Legend", DungeonEngine.BandOf(16));
            Assert.Equal("Beyond", DungeonEngine.BandOf(15));
            Assert.Equal(new[] { 20, 40, 60, 80, 100, 120 }, Enumerable.Range(15, 6).Select(DungeonEngine.FirstClearDiamonds));
            Assert.Equal(RarityTier.Mythic, DungeonEngine.RarityFloorFor(20));
        }

        [Fact]
        public async Task AWonRunTakesOneTicketAndPaysAPieceAChestAndTheFirstClearOnce()
        {
            const long playerId = 950_420_001L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            int fish = FoodRegistry.FirstRawFishOfTier(1);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var first = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 1, Strong(playerId), fish, 100, default, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.Ok, first.Outcome);
                Assert.True(first.Result!.Won, "a 2000-attribute character should beat stage 1");
                Assert.Equal(DungeonEngine.TicketCap - 1, first.TicketsLeft);
                Assert.True(first.Reward!.PieceId > 0);
                Assert.True(first.Reward.ChestId > 0);
                Assert.Equal(DungeonEngine.FirstClearDiamonds(1), first.Reward.Diamonds);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var second = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 1, Strong(playerId), fish, 100, default, now, new Random(2));
                Assert.Equal(0, second.Reward!.Diamonds);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
            var row = await verify.DungeonProgress.AsNoTracking().SingleAsync(d => d.PlayerId == playerId && d.Kind == (int)DungeonKind.Equipment);
            Assert.Equal(DungeonEngine.TicketCap - 2, row.Tickets);
            Assert.Equal(1, row.BestStage);
            // The other dungeons keep their own tickets.
            Assert.Equal(DungeonEngine.TicketCap, (await verify.DungeonProgress.AsNoTracking().SingleAsync(d => d.PlayerId == playerId && d.Kind == (int)DungeonKind.Gold)).Tickets);
            Assert.Equal(DungeonEngine.FirstClearDiamonds(1), player.PremiumDiamonds);
            Assert.Equal(2, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
            Assert.Equal(2, await verify.LootChests.CountAsync(c => c.PlayerId == playerId));
        }

        [Fact]
        public async Task ALockedStageOrNoTicketSpendsNothing()
        {
            const long playerId = 950_420_002L;
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.DungeonProgress.Add(new DungeonProgress { PlayerId = playerId, Kind = (int)DungeonKind.Equipment, Tickets = 0, TicketDay = DungeonEngine.UtcDay(now) });
                await db.SaveChangesAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var locked = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 3, Strong(playerId), 0, 0, default, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.Locked, locked.Outcome);
                var empty = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Equipment, 1, Strong(playerId), 0, 0, default, now, new Random(1));
                Assert.Equal(DungeonEngine.RunOutcome.NoTickets, empty.Outcome);
            }
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(0, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
            Assert.Equal(0, (await verify.DungeonProgress.AsNoTracking().SingleAsync(d => d.PlayerId == playerId && d.Kind == (int)DungeonKind.Equipment)).Tickets);
        }

        [Fact]
        public async Task TheXpDungeonReturnsItsXpAndTheGoldDungeonBanksItsGold()
        {
            const long playerId = 950_420_003L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            int fish = FoodRegistry.FirstRawFishOfTier(1);
            var income = new DungeonEngine.Income(HourlyXp: 120_000, HourlyGold: 3_000_000);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var xp = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Xp, 1, Strong(playerId), fish, 100, income, now, new Random(1));
                Assert.True(xp.Result!.Won);
                Assert.Equal(DungeonEngine.XpFor(1, 120_000, won: true), xp.Reward!.Xp);
                Assert.Equal(0, xp.Reward.Gold);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var gold = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Gold, 1, Strong(playerId), fish, 100, income, now, new Random(1));
                Assert.True(gold.Result!.Won);
                Assert.Equal(DungeonEngine.GoldFor(1, 3_000_000, won: true), gold.Reward!.Gold);
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var banked = await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            Assert.Equal(DungeonEngine.GoldFor(1, 3_000_000, won: true), banked.Quantity);
            // Two first clears, one in each dungeon.
            Assert.Equal(2 * DungeonEngine.FirstClearDiamonds(1), (await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId)).PremiumDiamonds);
        }

        [Fact]
        public async Task ALiveRunTakesItsTicketAtTheEndAndASweepNeedsABeatenStage()
        {
            const long playerId = 950_420_005L;
            var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
            }
            byte mask = Strong(playerId).DefeatedRegionBossMask;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var sweep = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Gold, 1, Strong(playerId), 0, 0, default, now, new Random(1), sweep: true);
                Assert.Equal(DungeonEngine.RunOutcome.Locked, sweep.Outcome);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var start = await DungeonEngine.StartLiveAsync(db, playerId, DungeonKind.Gold, 1, mask, now);
                Assert.Equal(DungeonEngine.RunOutcome.Ok, start.Outcome);
                Assert.Equal(DungeonEngine.TicketCap, start.TicketsLeft); // a run lost to a restart costs nothing
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var finish = await DungeonEngine.FinishLiveAsync(db, playerId, DungeonKind.Gold, 1, won: true, new DungeonEngine.Income(0, 3_000_000), now, new Random(1));
                Assert.Equal(DungeonEngine.TicketCap - 1, finish.TicketsLeft);
                Assert.Equal(DungeonEngine.GoldFor(1, 3_000_000, won: true), finish.Reward!.Gold);
                Assert.Equal(DungeonEngine.FirstClearDiamonds(1), finish.Reward.Diamonds);
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var sweep = await DungeonEngine.RunAsync(db, playerId, DungeonKind.Gold, 1, Strong(playerId), 0, 0, new DungeonEngine.Income(0, 3_000_000), now, new Random(1), sweep: true);
                Assert.Equal(DungeonEngine.RunOutcome.Ok, sweep.Outcome);
                Assert.True(sweep.Result!.Won);
                Assert.Equal(0, sweep.Reward!.Diamonds);
                Assert.Equal(DungeonEngine.TicketCap - 2, sweep.TicketsLeft);
            }
        }
    }
}

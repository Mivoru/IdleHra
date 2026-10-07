using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The quest line (owner, 2026-10-07): the registry's order, each step's
    /// predicates against seeded facts, the reward's scaling by region, and the
    /// claim's idempotency against a real Postgres.
    /// </summary>
    public class QuestLineRulesTests
    {
        private readonly ITestOutputHelper _o;
        public QuestLineRulesTests(ITestOutputHelper o) => _o = o;

        [Fact]
        public void TheStepsAreTheOwnersTwelveInTheOwnersOrder()
        {
            var ids = QuestLineRegistry.Steps.OrderBy(s => s.Order).Select(s => s.Id).ToArray();
            Assert.Equal(new[]
            {
                QuestLineRegistry.ClearChest, QuestLineRegistry.AutoSell,
                QuestLineRegistry.Fuse, QuestLineRegistry.Reroll, QuestLineRegistry.Village, QuestLineRegistry.Market,
                QuestLineRegistry.Breed, QuestLineRegistry.Inheritance, QuestLineRegistry.Delve,
                QuestLineRegistry.WorldBoss, QuestLineRegistry.Ascension, QuestLineRegistry.Rebirth,
            }, ids);
            Assert.Equal(Enumerable.Range(1, 12), QuestLineRegistry.Steps.Select(s => s.Order).OrderBy(o => o));
            Assert.Equal(ids.Length, ids.Distinct().Count());
            // The StepId column is varchar(32).
            Assert.All(ids, id => Assert.InRange(id.Length, 1, 32));
        }

        [Fact]
        public void EveryStepNamesAScreenATargetAnExplanationAndWhatUnlocksIt()
        {
            foreach (var step in QuestLineRegistry.Steps)
            {
                Assert.False(string.IsNullOrWhiteSpace(step.Title), step.Id);
                Assert.False(string.IsNullOrWhiteSpace(step.Explanation), step.Id);
                Assert.False(string.IsNullOrWhiteSpace(step.Screen), step.Id);
                Assert.False(string.IsNullOrWhiteSpace(step.UnlockHint), step.Id);
                Assert.NotEmpty(step.GuideTargets);
            }
        }

        [Fact]
        public void ANewPlayerHasNothingDoneAndOnlyTheEarlySteps_Unlocked()
        {
            var fresh = new QuestFacts { Level = 1 };
            foreach (var step in QuestLineRegistry.Steps) Assert.False(step.Done(fresh), $"{step.Id} done for a level-1 player");
            Assert.All(QuestLineRegistry.Steps, s => Assert.False(s.Unlocked(fresh), $"{s.Id} unlocked at level 1 with nothing"));
        }

        [Fact]
        public void EachStepUnlocksOnTheFactThatMakesTheActPossible()
        {
            bool Unlocked(string id, QuestFacts f) => QuestLineRegistry.Find(id)!.Unlocked(f);

            Assert.False(Unlocked(QuestLineRegistry.Fuse, new QuestFacts { Level = 4 }));
            Assert.True(Unlocked(QuestLineRegistry.Fuse, new QuestFacts { Level = 5 }));
            Assert.True(Unlocked(QuestLineRegistry.Reroll, new QuestFacts { Level = 1, ForgeLevel = 1 }));
            Assert.True(Unlocked(QuestLineRegistry.Village, new QuestFacts { Level = 5 }));

            Assert.False(Unlocked(QuestLineRegistry.Market, new QuestFacts { Level = 9, InGuild = true }));
            // The market's trade licence is a guild: level alone is not enough.
            Assert.False(Unlocked(QuestLineRegistry.Market, new QuestFacts { Level = 10 }));
            Assert.True(Unlocked(QuestLineRegistry.Market, new QuestFacts { Level = 10, InGuild = true }));

            Assert.False(Unlocked(QuestLineRegistry.Breed, new QuestFacts { Level = 40 }));
            Assert.True(Unlocked(QuestLineRegistry.Breed, new QuestFacts { BreedingGroundsLevel = 1 }));

            long cheapest = QuestLineRegistry.CheapestInheritanceLevel;
            Assert.False(Unlocked(QuestLineRegistry.Inheritance, new QuestFacts { Diamonds = (int)cheapest - 1 }));
            Assert.True(Unlocked(QuestLineRegistry.Inheritance, new QuestFacts { Diamonds = (int)cheapest }));

            long fee = DelveRegistry.EntryFeeForRegion(1);
            Assert.False(Unlocked(QuestLineRegistry.Delve, new QuestFacts { Gold = fee - 1, HighestUnlockedRegion = 1 }));
            Assert.True(Unlocked(QuestLineRegistry.Delve, new QuestFacts { Gold = fee, HighestUnlockedRegion = 1 }));
            // The fee is the player's own region's: region 3's is dearer.
            Assert.False(Unlocked(QuestLineRegistry.Delve, new QuestFacts { Gold = fee, HighestUnlockedRegion = 3 }));

            Assert.False(Unlocked(QuestLineRegistry.WorldBoss, new QuestFacts { Level = 9 }));
            Assert.True(Unlocked(QuestLineRegistry.WorldBoss, new QuestFacts { Level = 10 }));

            Assert.False(Unlocked(QuestLineRegistry.Ascension, new QuestFacts { Level = 60, HighestUnlockedRegion = 1 }));
            Assert.True(Unlocked(QuestLineRegistry.Ascension, new QuestFacts { HighestUnlockedRegion = 2 }));

            // Rebirth resets the whole life, so it is never pointed at a newcomer.
            Assert.False(Unlocked(QuestLineRegistry.Rebirth, new QuestFacts { Level = RebirthRules.RenownLevel - 1 }));
            Assert.True(Unlocked(QuestLineRegistry.Rebirth, new QuestFacts { Level = RebirthRules.RenownLevel }));
        }

        [Fact]
        public void EachStepIsDoneOnItsOwnDurableEvidenceAndNothingElse()
        {
            bool Done(string id, QuestFacts f) => QuestLineRegistry.Find(id)!.Done(f);

            Assert.True(Done(QuestLineRegistry.Fuse, new QuestFacts { FusionsCompleted = 1 }));
            Assert.True(Done(QuestLineRegistry.Fuse, new QuestFacts { GoldSpentOnFusion = 10_000 }));
            Assert.False(Done(QuestLineRegistry.Fuse, new QuestFacts { RerollsPerformed = 5, GoldSpentOnReroll = 500 }));

            Assert.True(Done(QuestLineRegistry.Reroll, new QuestFacts { RerollsPerformed = 1 }));
            Assert.True(Done(QuestLineRegistry.Reroll, new QuestFacts { GoldSpentOnReroll = 500 }));
            Assert.False(Done(QuestLineRegistry.Reroll, new QuestFacts { FusionsCompleted = 5 }));

            Assert.True(Done(QuestLineRegistry.Village, new QuestFacts { AnyVillageBuildingStarted = true }));
            Assert.True(Done(QuestLineRegistry.Village, new QuestFacts { GoldSpentOnVillage = 500 }));

            Assert.True(Done(QuestLineRegistry.Market, new QuestFacts { HasOpenOrSoldListing = true }));
            Assert.True(Done(QuestLineRegistry.Breed, new QuestFacts { GoldSpentOnBreeding = 1 }));
            Assert.True(Done(QuestLineRegistry.Inheritance, new QuestFacts { InheritanceLevelsBought = 1 }));
            Assert.True(Done(QuestLineRegistry.Delve, new QuestFacts { GoldSpentOnDelve = 7_000 }));
            Assert.True(Done(QuestLineRegistry.Delve, new QuestFacts { DelveDeepestFloor = 1 }));
            Assert.True(Done(QuestLineRegistry.WorldBoss, new QuestFacts { HasWorldBossAttemptRow = true }));
            Assert.True(Done(QuestLineRegistry.Ascension, new QuestFacts { HighestAscensionStep = 1 }));
            Assert.True(Done(QuestLineRegistry.Rebirth, new QuestFacts { RebirthCount = 1 }));

            // Being rich, high level, or standing next to the button is not doing it.
            var richAndStrong = new QuestFacts { Level = 99, Gold = 1_000_000_000, Diamonds = 9_999, HighestUnlockedRegion = 5, ForgeLevel = 5, BreedingGroundsLevel = 5 };
            Assert.All(QuestLineRegistry.Steps, s => Assert.False(s.Done(richAndStrong), $"{s.Id} done by wealth alone"));
        }

        [Fact]
        public void ADoneStepIsNeverLocked_AndClaimedBeatsDone()
        {
            var step = QuestLineRegistry.Find(QuestLineRegistry.Rebirth)!;
            var lowLevelButDone = new QuestFacts { Level = 1, RebirthCount = 1 };
            Assert.Equal(QuestStepState.Done, QuestLineEngine.StateOf(step, lowLevelButDone, latched: false, claimed: false));
            Assert.Equal(QuestStepState.Claimed, QuestLineEngine.StateOf(step, lowLevelButDone, latched: true, claimed: true));
            Assert.Equal(QuestStepState.Locked, QuestLineEngine.StateOf(step, new QuestFacts { Level = 1 }, latched: false, claimed: false));
            Assert.Equal(QuestStepState.Available, QuestLineEngine.StateOf(step, new QuestFacts { Level = 60 }, latched: false, claimed: false));
            // The latch alone proves it: a rebirth wiped the evidence.
            Assert.Equal(QuestStepState.Done, QuestLineEngine.StateOf(step, new QuestFacts { Level = 1 }, latched: true, claimed: false));
        }

        [Fact]
        public void TheRewardScalesWithTheRegionFromTheDelveFeeAndTheVillageLadder()
        {
            _o.WriteLine("region        gold  materials");
            long previousGold = 0;
            for (int region = 1; region <= 5; region++)
            {
                var reward = QuestLineRegistry.RewardFor(region);
                var mats = VillageManagementEngine.GetTierMaterials((region - 1) * 5);
                _o.WriteLine($"{region,6} {reward.Gold,11:N0}  {reward.MaterialQuantity} x {reward.MaterialLog} + {reward.MaterialQuantity} x {reward.MaterialOre}");

                Assert.Equal(DelveRegistry.EntryFeeForRegion(region) / 4, reward.Gold);
                Assert.Equal(mats.Log, reward.MaterialLog);
                Assert.Equal(mats.Ore, reward.MaterialOre);
                Assert.True(reward.MaterialQuantity >= 1);
                Assert.True(reward.Gold > previousGold, "the reward must rise with the region");
                previousGold = reward.Gold;

                // Modest: a quarter of a Delve entry, and below one fusion's fee at region 1.
                Assert.True(reward.Gold < DelveRegistry.EntryFeeForRegion(region));
            }
            // Out-of-range regions clamp instead of throwing.
            Assert.Equal(QuestLineRegistry.RewardFor(1), QuestLineRegistry.RewardFor(0));
            Assert.Equal(QuestLineRegistry.RewardFor(5), QuestLineRegistry.RewardFor(9));
        }

        /// <summary>
        /// Grep for a WRITER as well as a reader (CLAUDE.md): the two steps with
        /// no durable evidence of their own are answered by a latch, and a latch
        /// nothing writes can never be read as true.
        /// </summary>
        [Fact]
        public void TheTwoLatchOnlyStepsHaveAWriterAtTheActItself()
        {
            string root = ServerRoot();
            string Source(string rel) => File.ReadAllText(Path.Combine(root, rel));

            Assert.Contains("QuestLineRegistry.Market", Source("Domain/Economy/MarketEscrowEngine.cs"));
            Assert.Contains("QuestLineRegistry.Market", Source("Engine/MarketOrderBookEngine.cs"));
            // Both strike paths (the legacy attack and the wheel strike).
            int worldBossWriters = System.Text.RegularExpressions.Regex.Matches(
                Source("Engine/WorldBossEngine.cs"), @"NoteFactAsync\(db, playerId, QuestLineRegistry\.WorldBoss\)").Count;
            Assert.Equal(2, worldBossWriters);
        }

        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server", "Engine"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server");
        }
    }

    [Collection("Postgres collection")]
    public class QuestLineEngineTests
    {
        private readonly PostgresTestFixture _fixture;
        public QuestLineEngineTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task<long> CreatePlayerAsync(int level = 20, long gold = 0, Action<PlayerRecord>? configure = null)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"quest_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = level,
            };
            configure?.Invoke(player);
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            if (gold > 0) await CommodityLedger.AddAsync(db, player.Id, "gold", gold);
            return player.Id;
        }

        private async Task<long> HeldAsync(long playerId, string itemId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == itemId)
                .Select(c => (long?)c.Quantity).SingleOrDefaultAsync() ?? 0L;
        }

        private async Task<QuestLineView> ViewAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return (await QuestLineEngine.ViewAsync(db, playerId))!;
        }

        private async Task<QuestClaimResult> ClaimAsync(long playerId, string step)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await QuestLineEngine.ClaimAsync(db, playerId, step);
        }

        private static string StateOf(QuestLineView view, string step) => view.Steps.Single(s => s.Id == step).State;

        [Fact]
        public async Task TheViewListsTwelveStepsInOrderWithTheirState()
        {
            long id = await CreatePlayerAsync(level: 1);
            var view = await ViewAsync(id);

            Assert.Equal(12, view.Total);
            Assert.Equal(Enumerable.Range(1, 12), view.Steps.Select(s => s.Order));
            Assert.All(view.Steps, s => Assert.Equal("locked", s.State));
            Assert.All(view.Steps, s => Assert.False(string.IsNullOrEmpty(s.UnlockHint)));
            Assert.All(view.Steps, s => Assert.False(s.Claimable));
            Assert.Equal(0, view.Done);
        }

        [Fact]
        public async Task EvidenceInTheDatabaseTicksStepsOff()
        {
            long id = await CreatePlayerAsync(level: 20, configure: p =>
            {
                p.ForgeFusionsCompleted = 3;
                p.AffixRerollsPerformed = 2;
                p.DelveDeepestFloor = 2;
                p.RebirthCount = 1;
                p.PremiumDiamonds = 100;
                p.AutoSalvageBelowTier = 3;
            });
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = id, BuildingId = VillageManagementEngine.LumberjackBuildingId, CurrentLevel = 1 });
                db.PlayerInheritanceStats.Add(new PlayerInheritanceStat { PlayerId = id, StatId = 0, Level = 1 });
                db.BossAscensionProgress.Add(new BossAscensionProgress { PlayerId = id, Region = 1, HighestStep = 2, UpdatedAtUtc = DateTime.UtcNow });
                db.MarketOrderRecords.Add(new MarketOrderRecord { SellerId = id, OrderType = "SELL", BaseItemId = "x", Price = 5, CreatedAtEpoch = 1 });
                db.PlayerWorldBossAttempts.Add(new PlayerWorldBossAttempt { PlayerId = id, BossInstanceId = 987654, AttemptCount = 1, TotalInflictedDamage = 10 });
                await db.SaveChangesAsync();
                await GoldLedger.RecordSpendAsync(db, id, GoldSpendCategory.Breeding, 500);
                await GoldLedger.RecordIncomeAsync(db, id, GoldIncomeSource.ChestSale, 120);
                await db.SaveChangesAsync();
            }

            var view = await ViewAsync(id);
            foreach (var step in new[]
            {
                QuestLineRegistry.ClearChest, QuestLineRegistry.AutoSell,
                QuestLineRegistry.Fuse, QuestLineRegistry.Reroll, QuestLineRegistry.Village, QuestLineRegistry.Market,
                QuestLineRegistry.Breed, QuestLineRegistry.Inheritance, QuestLineRegistry.Delve,
                QuestLineRegistry.WorldBoss, QuestLineRegistry.Ascension, QuestLineRegistry.Rebirth,
            })
            {
                Assert.Equal("done", StateOf(view, step));
            }
            Assert.Equal(12, view.Done);
            Assert.All(view.Steps, s => Assert.True(s.Claimable));
        }

        [Fact]
        public async Task ADoneStepStaysDoneAfterTheEvidenceIsWiped_BecauseTheViewLatchedIt()
        {
            long id = await CreatePlayerAsync(level: 20);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure { PlayerId = id, BuildingId = VillageManagementEngine.LumberjackBuildingId, CurrentLevel = 1 });
                await db.SaveChangesAsync();
            }
            Assert.Equal("done", StateOf(await ViewAsync(id), QuestLineRegistry.Village));

            // A rebirth deletes the village.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"VillageInfrastructures\" WHERE \"PlayerId\" = {id}");
            }
            Assert.Equal("done", StateOf(await ViewAsync(id), QuestLineRegistry.Village));
        }

        [Fact]
        public async Task TheMarketStepNeedsTheGuildTradeLicence_BecauseListingDoes()
        {
            long loner = await CreatePlayerAsync(level: 20);
            long guilded = await CreatePlayerAsync(level: 20, configure: p => p.GuildId = 1);

            var lonerView = await ViewAsync(loner);
            Assert.Equal("locked", StateOf(lonerView, QuestLineRegistry.Market));
            Assert.Contains("guild", lonerView.Steps.Single(s => s.Id == QuestLineRegistry.Market).UnlockHint);
            Assert.Equal("available", StateOf(await ViewAsync(guilded), QuestLineRegistry.Market));
        }

        [Fact]
        public async Task ALatchWrittenAtTheActMakesTheStepDoneWithNoOtherEvidence()
        {
            long id = await CreatePlayerAsync(level: 20, configure: p => p.GuildId = 1);
            Assert.Equal("available", StateOf(await ViewAsync(id), QuestLineRegistry.Market));

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await QuestLineEngine.NoteFactAsync(db, id, QuestLineRegistry.Market);
                await QuestLineEngine.NoteFactAsync(db, id, QuestLineRegistry.Market); // idempotent
            }
            Assert.Equal("done", StateOf(await ViewAsync(id), QuestLineRegistry.Market));
            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(1, await check.QuestLineClaims.CountAsync(q => q.PlayerId == id && q.StepId == QuestLineRegistry.Market));
        }

        [Fact]
        public async Task ALockedStepCannotBeClaimed_AndPaysNothing()
        {
            long id = await CreatePlayerAsync(level: 1);
            long goldBefore = await HeldAsync(id, "gold");

            Assert.Equal(QuestClaimResult.Locked, await ClaimAsync(id, QuestLineRegistry.Rebirth));
            Assert.Equal(goldBefore, await HeldAsync(id, "gold"));
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await db.QuestLineClaims.AnyAsync(q => q.PlayerId == id && q.ClaimedAtUtc != null));
        }

        [Fact]
        public async Task AnAvailableButUndoneStepCannotBeClaimed()
        {
            long id = await CreatePlayerAsync(level: 20);
            Assert.Equal(QuestClaimResult.NotDone, await ClaimAsync(id, QuestLineRegistry.Fuse));
            Assert.Equal(0, await HeldAsync(id, "gold"));
        }

        [Fact]
        public async Task AnUnknownStepAndAMissingPlayerAreRefused()
        {
            long id = await CreatePlayerAsync();
            Assert.Equal(QuestClaimResult.UnknownStep, await ClaimAsync(id, "does_not_exist"));
            Assert.Equal(QuestClaimResult.UnknownStep, await ClaimAsync(id, ""));
            Assert.Equal(QuestClaimResult.PlayerNotFound, await ClaimAsync(long.MaxValue - 7, QuestLineRegistry.Fuse));
        }

        [Fact]
        public async Task AQuarantinedAccountCannotClaim()
        {
            long id = await CreatePlayerAsync(configure: p => { p.ForgeFusionsCompleted = 1; p.IsQuarantined = true; });
            Assert.Equal(QuestClaimResult.Restricted, await ClaimAsync(id, QuestLineRegistry.Fuse));
            Assert.Equal(0, await HeldAsync(id, "gold"));
        }

        [Fact]
        public async Task ClaimingADoneStepPaysTheRegionRewardOnce()
        {
            long id = await CreatePlayerAsync(level: 20, configure: p => p.ForgeFusionsCompleted = 1);
            var reward = QuestLineRegistry.RewardFor(1);

            Assert.Equal(QuestClaimResult.Ok, await ClaimAsync(id, QuestLineRegistry.Fuse));
            Assert.Equal(reward.Gold, await HeldAsync(id, "gold"));
            Assert.Equal(reward.MaterialQuantity, await HeldAsync(id, reward.MaterialLog));
            Assert.Equal(reward.MaterialQuantity, await HeldAsync(id, reward.MaterialOre));
            Assert.Equal("claimed", StateOf(await ViewAsync(id), QuestLineRegistry.Fuse));

            // The second press pays nothing.
            Assert.Equal(QuestClaimResult.AlreadyClaimed, await ClaimAsync(id, QuestLineRegistry.Fuse));
            Assert.Equal(reward.Gold, await HeldAsync(id, "gold"));
            Assert.Equal(reward.MaterialQuantity, await HeldAsync(id, reward.MaterialLog));

            // And the ledger counted it as income from the quest line, once.
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long income = await db.GoldIncomeDaily.AsNoTracking()
                .Where(g => g.PlayerId == id && g.Source == (short)GoldIncomeSource.QuestLine).SumAsync(g => g.Amount);
            Assert.Equal(reward.Gold, income);
        }

        [Fact]
        public async Task TwentyConcurrentClaimsPayExactlyOnce()
        {
            long id = await CreatePlayerAsync(level: 20, configure: p => p.RebirthCount = 1);
            var reward = QuestLineRegistry.RewardFor(1);

            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ClaimAsync(id, QuestLineRegistry.Rebirth)));

            Assert.Equal(1, results.Count(r => r == QuestClaimResult.Ok));
            Assert.Equal(19, results.Count(r => r == QuestClaimResult.AlreadyClaimed));
            Assert.Equal(reward.Gold, await HeldAsync(id, "gold"));
            Assert.Equal(reward.MaterialQuantity, await HeldAsync(id, reward.MaterialLog));
        }

        [Fact]
        public async Task EachStepPaysSeparately()
        {
            long id = await CreatePlayerAsync(level: 20, configure: p => { p.ForgeFusionsCompleted = 1; p.AffixRerollsPerformed = 1; });
            var reward = QuestLineRegistry.RewardFor(1);

            Assert.Equal(QuestClaimResult.Ok, await ClaimAsync(id, QuestLineRegistry.Fuse));
            Assert.Equal(QuestClaimResult.Ok, await ClaimAsync(id, QuestLineRegistry.Reroll));
            Assert.Equal(reward.Gold * 2, await HeldAsync(id, "gold"));
        }

        [Fact]
        public async Task TheRewardScalesToTheHighestUnlockedRegion()
        {
            long id = await CreatePlayerAsync(level: 60, configure: p => p.ForgeFusionsCompleted = 1);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // Bosses of regions 1 and 2 defeated -> region 3 is open.
                foreach (int region in new[] { 1, 2 })
                {
                    db.MonsterCodexEntries.Add(new MonsterCodexEntry { PlayerId = id, MonsterId = RaceUnlockRegistry.GetRegionBossMonsterId(region), KillCount = 1 });
                }
                await db.SaveChangesAsync();
            }

            var view = await ViewAsync(id);
            Assert.Equal(3, view.Reward.Region);
            var expected = QuestLineRegistry.RewardFor(3);
            Assert.Equal(expected.Gold, view.Reward.Gold);

            Assert.Equal(QuestClaimResult.Ok, await ClaimAsync(id, QuestLineRegistry.Fuse));
            Assert.Equal(expected.Gold, await HeldAsync(id, "gold"));
            Assert.Equal(expected.MaterialQuantity, await HeldAsync(id, expected.MaterialLog));
            Assert.Equal(expected.MaterialQuantity, await HeldAsync(id, expected.MaterialOre));
        }

        [Fact]
        public async Task TheDevUnclaimTakesBackExactlyWhatWasPaid_AndTheStepStaysDone()
        {
            long id = await CreatePlayerAsync(level: 20, gold: 500, configure: p => p.ForgeFusionsCompleted = 1);
            var reward = QuestLineRegistry.RewardFor(1);

            Assert.Equal(QuestClaimResult.Ok, await ClaimAsync(id, QuestLineRegistry.Fuse));
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.True(await QuestLineEngine.DevUnclaimAsync(db, id, QuestLineRegistry.Fuse));
                Assert.False(await QuestLineEngine.DevUnclaimAsync(db, id, QuestLineRegistry.Fuse));
            }

            Assert.Equal(500, await HeldAsync(id, "gold"));
            Assert.Equal(0, await HeldAsync(id, reward.MaterialLog));
            Assert.Equal("done", StateOf(await ViewAsync(id), QuestLineRegistry.Fuse));
            Assert.Equal(QuestClaimResult.Ok, await ClaimAsync(id, QuestLineRegistry.Fuse));
        }
    }
}

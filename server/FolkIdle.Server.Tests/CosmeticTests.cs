using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: TASK 54 - cosmetic chests, avatars and frames. Pins the owner's
    /// numbers (docs/superpowers/plans/2026-09-28-task-54-cosmetics.md), the
    /// level chest's idempotence and backfill, opening, wearing, and that every
    /// path that grows a level also notes it for the level chest.
    /// </summary>
    [Collection("Postgres collection")]
    public class CosmeticTests
    {
        private readonly PostgresTestFixture _fixture;

        public CosmeticTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void TheCatalogueHasTheAgreedPools()
        {
            Assert.Equal(CosmeticRegistry.All.Count, CosmeticRegistry.All.Select(d => d.Id).Distinct().Count());

            int Avatars(int r) => CosmeticRegistry.All.Count(d => d.Kind == CosmeticKind.Avatar && d.Rarity == r);
            int Frames(int r) => CosmeticRegistry.All.Count(d => d.Kind == CosmeticKind.Frame && d.Rarity == r);

            Assert.Equal(new[] { 8, 8, 6, 3 }, Enumerable.Range(1, 4).Select(Avatars));
            Assert.Equal(new[] { 4, 4, 4, 4 }, Enumerable.Range(1, 4).Select(Frames));
            Assert.Equal(25, CosmeticRegistry.All.Count(d => d.Kind == CosmeticKind.Avatar));

            // Every avatar is a canonical monster's portrait - by name, which is
            // what the client maps to a picture.
            string monstersJson = File.ReadAllText(Path.Combine(ServerRoot(), "..", "GameData", "monsters.json"));
            var monsters = Regex.Matches(monstersJson, @"""Name""\s*:\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
            foreach (var avatar in CosmeticRegistry.All.Where(d => d.Kind == CosmeticKind.Avatar))
            {
                Assert.Contains(avatar.Art!, monsters);
            }

            for (int r = 1; r <= 4; r++)
            {
                Assert.All(CosmeticRegistry.ChestPool(r), d => Assert.Equal(r, d.Rarity));
                Assert.DoesNotContain(CosmeticRegistry.ChestPool(r), d => d.Kind == CosmeticKind.Chest);
            }
        }

        [Fact]
        public void MonsterChestsDropAsRarelyAsTheirTwinItemTier()
        {
            // The owner's words: Common as rare as Mythic, Rare as Relic, Epic
            // as Ancient, Legendary as Divine.
            Assert.Equal(new[] { 0, RarityTier.Mythic, RarityTier.Relic, RarityTier.Ancient, RarityTier.Divine }, CosmeticRegistry.TwinItemTier);

            for (int r = 1; r <= 4; r++)
            {
                double expected = CombatLootEngine.EquipmentDropChance * RarityTier.BaseShare(CosmeticRegistry.TwinItemTier[r]);
                Assert.Equal(expected, CosmeticRegistry.ChestChancePerKill(r), 12);
            }

            // Measured, not decoration: about one Common chest per 2,600 kills,
            // one Legendary per 131,000.
            Assert.InRange(1.0 / CosmeticRegistry.ChestChancePerKill(CosmeticRegistry.Common), 2500, 2700);
            Assert.InRange(1.0 / CosmeticRegistry.ChestChancePerKill(CosmeticRegistry.Legendary), 125_000, 137_000);

            // The ladder: rarest first, then nothing.
            double legendary = CosmeticRegistry.ChestChancePerKill(4);
            Assert.Equal(4, CosmeticRegistry.RollMonsterChest(0.0));
            Assert.Equal(4, CosmeticRegistry.RollMonsterChest(legendary * 0.999));
            Assert.Equal(3, CosmeticRegistry.RollMonsterChest(legendary * 1.001));
            double all = Enumerable.Range(1, 4).Sum(CosmeticRegistry.ChestChancePerKill);
            Assert.Equal(1, CosmeticRegistry.RollMonsterChest(all * 0.999));
            Assert.Equal(0, CosmeticRegistry.RollMonsterChest(all * 1.001));
            Assert.Equal(0, CosmeticRegistry.RollMonsterChest(0.5));
        }

        [Fact]
        public void TheLevelChestRolls50_30_15_5()
        {
            Assert.Equal(1, CosmeticRegistry.RollLevelChest(0.0));
            Assert.Equal(1, CosmeticRegistry.RollLevelChest(0.4999));
            Assert.Equal(2, CosmeticRegistry.RollLevelChest(0.5001));
            Assert.Equal(2, CosmeticRegistry.RollLevelChest(0.7999));
            Assert.Equal(3, CosmeticRegistry.RollLevelChest(0.8001));
            Assert.Equal(3, CosmeticRegistry.RollLevelChest(0.9499));
            Assert.Equal(4, CosmeticRegistry.RollLevelChest(0.9501));
            Assert.Equal(4, CosmeticRegistry.RollLevelChest(0.99999));

            Assert.Equal(0, CosmeticRegistry.LevelChestsOwed(4));
            Assert.Equal(1, CosmeticRegistry.LevelChestsOwed(5));
            Assert.Equal(19, CosmeticRegistry.LevelChestsOwed(96));
        }

        /// <summary>
        /// CLAUDE.md: three paths grow a level, and each one has to be told
        /// separately. Every file that applies level-up growth must also note
        /// the level for the chest.
        /// </summary>
        [Fact]
        public void EveryLevelPathNotesTheLevelChest()
        {
            string root = ServerRoot();
            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToList();

            int callers = 0;
            foreach (string path in files)
            {
                string source = File.ReadAllText(path);
                if (path.EndsWith("RaceAttributeGrowth.cs")) continue;
                int grows = Regex.Matches(source, @"RaceAttributeGrowth\.ApplyLevelUpGrowth\(").Count;
                if (grows == 0) continue;
                callers++;
                int notes = Regex.Matches(source, @"CosmeticGrantEngine\.NoteLevel\(").Count;
                Assert.True(notes >= grows, $"{Path.GetFileName(path)} grows a level {grows} time(s) but notes the level chest {notes} time(s)");
            }
            Assert.Equal(3, callers);

            // And login, which is what pays the backfill.
            string sim = File.ReadAllText(Path.Combine(root, "Domain", "Combat", "SimulationEngine.cs"));
            int login = sim.IndexOf("ExtrapolateOfflineProgressAsync", StringComparison.Ordinal);
            int ready = sim.IndexOf("_readyLogins.Enqueue(payload)", login, StringComparison.Ordinal);
            Assert.True(login > 0 && ready > login);
            Assert.Contains("CosmeticGrantEngine.NoteLevel", sim.Substring(login, ready - login));
        }

        /// <summary>
        /// A real monster chest is thousands of kills away, so the wiring is
        /// pinned in source: rolled inside the per-kill loop (which live and
        /// offline both walk), written in the transaction, reported on the loot
        /// feed, and written again if the batch fails rather than lost.
        /// </summary>
        [Fact]
        public void TheLootWorkerRollsWritesAndReportsAChest()
        {
            string loot = File.ReadAllText(Path.Combine(ServerRoot(), "Engine", "CombatLootEngine.cs"));

            int loop = loot.IndexOf("for (int kill = 0; kill < kills; kill++)", StringComparison.Ordinal);
            int apply = loot.IndexOf("await ApplyCommodityDeltasAsync(dbContext, playerId, resolvedCommodityDeltas);", loop, StringComparison.Ordinal);
            Assert.True(loop > 0 && apply > loop);
            Assert.Contains("CosmeticRegistry.RollMonsterChest(", loot.Substring(loop, apply - loop));

            Assert.Equal(2, Regex.Matches(loot, @"CosmeticEngine\.InsertChestsAsync\(").Count);
            Assert.Contains("DropKindCosmeticChest", loot);

            int commit = loot.IndexOf("await transaction.CommitAsync();", apply, StringComparison.Ordinal);
            int firstInsert = loot.IndexOf("CosmeticEngine.InsertChestsAsync(", apply, StringComparison.Ordinal);
            Assert.True(firstInsert > apply && firstInsert < commit, "the chest must be written inside the loot transaction");
        }

        [Fact]
        public async Task TheLevelChestPaysOncePerFiveLevels_AndBackfills()
        {
            const long playerId = 954000001L;
            await SeedPlayerAsync(playerId, level: 96);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // An existing account at 96: nineteen chests, once.
                var first = await CosmeticEngine.GrantLevelChestsAsync(db, playerId, 96, new Random(1), DateTime.UtcNow);
                Assert.Equal(19, first.Count);
                Assert.All(first, g => Assert.InRange(g.Rarity, 1, 4));

                // A relogin, and a stale request from an older level, pay nothing.
                Assert.Empty(await CosmeticEngine.GrantLevelChestsAsync(db, playerId, 96, new Random(2), DateTime.UtcNow));
                Assert.Empty(await CosmeticEngine.GrantLevelChestsAsync(db, playerId, 90, new Random(3), DateTime.UtcNow));

                // 99 is still inside the same milestone; 100 is the twentieth.
                Assert.Empty(await CosmeticEngine.GrantLevelChestsAsync(db, playerId, 99, new Random(4), DateTime.UtcNow));
                Assert.Single(await CosmeticEngine.GrantLevelChestsAsync(db, playerId, 100, new Random(5), DateTime.UtcNow));
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(20, await verify.CosmeticItems.CountAsync(c => c.PlayerId == playerId && c.Kind == (byte)CosmeticKind.Chest));
            Assert.Equal(20, (await verify.PlayerRecords.SingleAsync(p => p.Id == playerId)).LevelChestsGranted);
        }

        [Fact]
        public async Task TheWorkerPaysANotedLevel_AndANewPlayerGetsOneChestAtFive()
        {
            const long playerId = 954000002L;
            await SeedPlayerAsync(playerId, level: 5);

            var worker = new CosmeticGrantEngine(_fixture.ServiceProvider, playerRegistry: null);
            CosmeticGrantEngine.NoteLevel(playerId, 4);
            CosmeticGrantEngine.NoteLevel(playerId, 5);
            CosmeticGrantEngine.NoteLevel(playerId, 5);

            // Drained directly rather than through StartCron: the queue is
            // static, and a started worker left running would eat later tests'
            // notes (CLAUDE.md). Other tests may have queued notes too, so drain
            // until this player's grant has landed.
            for (int i = 0; i < 20 && !await HasChestsAsync(playerId); i++)
            {
                await worker.DrainOneCycleAsync();
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(1, await verify.CosmeticItems.CountAsync(c => c.PlayerId == playerId));
        }

        [Fact]
        public async Task OpeningAChestTurnsItIntoACosmeticOfItsRarity()
        {
            const long playerId = 954000003L;
            await SeedPlayerAsync(playerId, level: 1);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var (none, _) = await CosmeticEngine.OpenAsync(db, playerId, CosmeticRegistry.Epic, new Random(1), DateTime.UtcNow);
            Assert.Equal(CosmeticResult.NoChest, none);

            await CosmeticEngine.InsertChestsAsync(db, playerId, new[] { CosmeticRegistry.Epic }, CosmeticSource.Dev, DateTime.UtcNow);
            var (ok, opened) = await CosmeticEngine.OpenAsync(db, playerId, CosmeticRegistry.Epic, new Random(1), DateTime.UtcNow);
            Assert.Equal(CosmeticResult.Ok, ok);
            Assert.NotNull(opened);
            Assert.Equal(CosmeticRegistry.Epic, opened!.Rarity);
            Assert.NotEqual((byte)CosmeticKind.Chest, opened.Kind);

            var view = await CosmeticEngine.ViewAsync(db, playerId);
            Assert.Equal(0, view.Chests[CosmeticRegistry.Epic]);
            Assert.Single(view.Owned);
            Assert.Equal(opened.DefinitionId, view.Owned[0].DefinitionId);

            // The chest is gone: a second open finds nothing.
            var (again, _) = await CosmeticEngine.OpenAsync(db, playerId, CosmeticRegistry.Epic, new Random(1), DateTime.UtcNow);
            Assert.Equal(CosmeticResult.NoChest, again);
        }

        [Fact]
        public async Task OnlyAnOwnedUnlistedCosmeticCanBeWorn()
        {
            const long playerId = 954000004L;
            await SeedPlayerAsync(playerId, level: 1);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            Assert.Equal(CosmeticResult.NotOwned, await CosmeticEngine.EquipAsync(db, playerId, CosmeticKind.Avatar, "avatar_malakor"));
            Assert.Equal(CosmeticResult.UnknownCosmetic, await CosmeticEngine.EquipAsync(db, playerId, CosmeticKind.Avatar, "avatar_nobody"));
            Assert.Equal(CosmeticResult.UnknownCosmetic, await CosmeticEngine.EquipAsync(db, playerId, CosmeticKind.Frame, "avatar_malakor"));
            Assert.Equal(CosmeticResult.UnknownCosmetic, await CosmeticEngine.EquipAsync(db, playerId, CosmeticKind.Chest, null));

            db.CosmeticItems.Add(new CosmeticItem
            {
                PlayerId = playerId, Kind = (byte)CosmeticKind.Avatar, DefinitionId = "avatar_malakor",
                Rarity = CosmeticRegistry.Legendary, AcquiredAtUtc = DateTime.UtcNow, IsListed = true,
            });
            await db.SaveChangesAsync();

            // On the market, it is on its way to someone else.
            Assert.Equal(CosmeticResult.NotOwned, await CosmeticEngine.EquipAsync(db, playerId, CosmeticKind.Avatar, "avatar_malakor"));

            var row = await db.CosmeticItems.SingleAsync(c => c.PlayerId == playerId);
            row.IsListed = false;
            await db.SaveChangesAsync();

            Assert.Equal(CosmeticResult.Ok, await CosmeticEngine.EquipAsync(db, playerId, CosmeticKind.Avatar, "avatar_malakor"));
            var worn = await CosmeticEngine.WornAsync(db, new[] { playerId });
            Assert.Equal("avatar_malakor", Assert.Single(worn).AvatarId);

            Assert.Equal(CosmeticResult.Ok, await CosmeticEngine.EquipAsync(db, playerId, CosmeticKind.Avatar, null));
            Assert.Null((await CosmeticEngine.WornAsync(db, new[] { playerId })).Single().AvatarId);
        }

        private async Task SeedPlayerAsync(long playerId, int level)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            if (await db.PlayerRecords.AnyAsync(p => p.Id == playerId)) return;
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = level,
            });
            await db.SaveChangesAsync();
        }

        private async Task<bool> HasChestsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CosmeticItems.AnyAsync(c => c.PlayerId == playerId);
        }

        private static string ServerRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "server", "FolkIdle.Server")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "could not locate server/FolkIdle.Server from the test binary");
            return Path.Combine(dir!.FullName, "server", "FolkIdle.Server");
        }
    }
}

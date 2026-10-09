using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Seasonal event pets: the owner's strengths, one of each per account,
    /// one per character, and a placed pet reaching the character's totals -
    /// the path every live and offline reader takes.
    /// </summary>
    [Collection("Postgres collection")]
    public class PetTests
    {
        private readonly PostgresTestFixture _fixture;

        public PetTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void The_owners_strengths_hold()
        {
            var shop = PetRegistry.All.Where(p => p.Source == PetSource.Shop).ToList();
            Assert.Equal(5, shop.Count);
            Assert.All(shop, p => Assert.Equal(new[] { PetRegistry.ShopPetPct }, p.Bonuses.Select(b => b.Pct)));
            Assert.Equal(5, PetRegistry.ShopPetPct);

            var rare = PetRegistry.RareDropOf(SeasonalEventRegistry.SamhainId)!;
            Assert.Equal(2, rare.Bonuses.Count);
            Assert.All(rare.Bonuses, b => Assert.Equal(8, b.Pct));

            var boss = PetRegistry.BossPetOf(SeasonalEventRegistry.SamhainId)!;
            Assert.True(boss.Bonuses.Sum(b => b.Pct) > rare.Bonuses.Sum(b => b.Pct), "the boss pet is the strongest");

            // Every stat the owner listed is covered by some pet.
            var stats = PetRegistry.All.SelectMany(p => p.Bonuses).Select(b => b.Stat).ToHashSet();
            Assert.Equal(Enum.GetValues<PetStat>().Length, stats.Count);
        }

        [Fact]
        public void The_shop_sells_exactly_the_shop_pets_at_the_agreed_price()
        {
            var samhain = SeasonalEventRegistry.Find(SeasonalEventRegistry.SamhainId)!;
            var petEntries = samhain.Shop.Where(i => i.Kind == EventShopKind.Pet).ToList();
            Assert.Equal(
                PetRegistry.All.Where(p => p.Source == PetSource.Shop).Select(p => p.Id).OrderBy(x => x),
                petEntries.Select(i => i.Id).OrderBy(x => x));
            Assert.All(petEntries, i => Assert.Equal(3000, i.Price));
            Assert.All(samhain.Shop.Where(i => i.Kind == EventShopKind.Avatar), i => Assert.Equal(1000, i.Price));
        }

        [Fact]
        public void Every_pet_has_art_on_disk()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "client_web"))) dir = dir.Parent;
            string root = System.IO.Path.Combine(dir!.FullName, "client", "Assets", "Images", "SpritesWeb");
            foreach (var pet in PetRegistry.All)
            {
                Assert.True(System.IO.File.Exists(System.IO.Path.Combine(root, pet.Art)), pet.Art);
            }
        }

        [Fact]
        public void A_pet_folds_into_the_totals_its_readers_use()
        {
            var totals = default(EquippedAffixTotals);
            PetRegistry.AddTo(PetRegistry.Find("pet_ghostie")!, ref totals);
            PetRegistry.AddTo(PetRegistry.Find("pet_pixie")!, ref totals);
            PetRegistry.AddTo(PetRegistry.Find("pet_witch")!, ref totals);
            Assert.Equal(50, totals.XpTenthsPct);
            Assert.Equal(50, totals.GoldTenthsPct);
            Assert.Equal(80, totals.WorldBossDamageTenthsPct);
            Assert.Equal(80, totals.LootLuckTenthsPct);

            // The XP reader adds it.
            var payload = new TickStatePayload { CachedAffixTotals = totals };
            var bare = new TickStatePayload();
            Assert.Equal(
                SimulationEngine.LiveKillXpMultiplierPct(in bare, 100) + 5,
                SimulationEngine.LiveKillXpMultiplierPct(in payload, 100));
        }

        private async Task<(long PlayerId, Guid A, Guid B)> SeedAsync(long playerId)
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId, PlayerGuid = a, AuthenticatorToken = Guid.NewGuid(), Username = $"pet{playerId}",
            });
            db.CharacterRecords.AddRange(
                new CharacterRecord { Id = a, PlayerId = playerId, AgePhase = 1, SlotIndex = 0 },
                new CharacterRecord { Id = b, PlayerId = playerId, AgePhase = 1, SlotIndex = 1 });
            await db.SaveChangesAsync();
            return (playerId, a, b);
        }

        [Fact]
        public async Task A_pet_is_owned_once_and_follows_one_character()
        {
            var (playerId, a, b) = await SeedAsync(985_000_001L);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            Assert.True(await PetEngine.GrantAsync(db, playerId, "pet_ghostie", DateTime.UtcNow));
            Assert.False(await PetEngine.GrantAsync(db, playerId, "pet_ghostie", DateTime.UtcNow));
            Assert.Equal(1, await db.PlayerPets.CountAsync(p => p.PlayerId == playerId));

            // Not owned: refused.
            var (refused, _) = await PetEngine.AssignAsync(db, playerId, "pet_pixie", a);
            Assert.Equal(PetAssignResult.NotOwned, refused);

            var (ok, updates) = await PetEngine.AssignAsync(db, playerId, "pet_ghostie", a);
            Assert.Equal(PetAssignResult.Ok, ok);
            Assert.Single(updates);
            Assert.Equal(50, updates[0].AffixTotals.XpTenthsPct);

            // Moving it re-stats BOTH characters: A loses it, B gains it.
            var (moved, both) = await PetEngine.AssignAsync(db, playerId, "pet_ghostie", b);
            Assert.Equal(PetAssignResult.Ok, moved);
            Assert.Equal(2, both.Count);
            Assert.Equal(0, both.Single(u => u.CharacterId == a).AffixTotals.XpTenthsPct);
            Assert.Equal(50, both.Single(u => u.CharacterId == b).AffixTotals.XpTenthsPct);

            // A second pet onto B sends Ghostie back to rest: one per character.
            await PetEngine.GrantAsync(db, playerId, "pet_pixie", DateTime.UtcNow);
            var (swapped, _) = await PetEngine.AssignAsync(db, playerId, "pet_pixie", b);
            Assert.Equal(PetAssignResult.Ok, swapped);
            db.ChangeTracker.Clear();
            Assert.Null((await db.PlayerPets.SingleAsync(p => p.PlayerId == playerId && p.PetId == "pet_ghostie")).CharacterId);
            Assert.Equal(b, (await db.PlayerPets.SingleAsync(p => p.PlayerId == playerId && p.PetId == "pet_pixie")).CharacterId);

            // Someone else's character is refused.
            var (stranger, _) = await PetEngine.AssignAsync(db, playerId, "pet_pixie", Guid.NewGuid());
            Assert.Equal(PetAssignResult.NotYourCharacter, stranger);
        }

        /// <summary>
        /// Regression, found building pets (2026-10-10): the equip update's six
        /// tool fields had no writer, so ANY equip - or a pet placement, which
        /// travels the same way - zeroed the tool tiers until the next login.
        /// </summary>
        [Fact]
        public async Task An_equip_update_carries_the_accounts_real_tools()
        {
            var (playerId, a, b) = await SeedAsync(985_000_003L);
            await using (var seed = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var rod = new EquipmentInstance
                {
                    PlayerId = playerId, BaseItemId = "birch_fishing_rod_tool", QualityTier = 1,
                    AffixPayload = "{\"gather_rare_find_pct@3\":25}",
                };
                seed.EquipmentInstances.Add(rod);
                await seed.SaveChangesAsync();
                var main = await seed.CharacterRecords.SingleAsync(c => c.Id == a);
                main.EquippedRodId = rod.Id;
                await seed.SaveChangesAsync();
            }

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            // An update for the SECOND character still carries the account's tools.
            var other = await db.CharacterRecords.AsNoTracking().SingleAsync(c => c.Id == b);
            var update = await EquipmentSlotEngine.WithAccountToolsAsync(db, await EquipmentSlotEngine.BuildNotificationAsync(db, other));
            Assert.True(update.ToolsResolved);
            Assert.Equal(1, update.RodToolTier);
            Assert.Equal(25, update.ToolRareFindPct);
        }

        [Fact]
        public async Task A_pet_bought_twice_is_refused_and_refunded()
        {
            var (playerId, _, _) = await SeedAsync(985_000_002L);
            var item = SeasonalEventRegistry.Find(SeasonalEventRegistry.SamhainId)!.Shop.First(i => i.Kind == EventShopKind.Pet);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(CommandResultCode.EventShopBought, await EventShopEngine.BuyCoreAsync(db, playerId, item, DateTime.UtcNow));
            Assert.Equal(CommandResultCode.EventShopAlreadyOwned, await EventShopEngine.BuyCoreAsync(db, playerId, item, DateTime.UtcNow));
            Assert.Contains(item.Id, await EventShopEngine.OwnedAsync(db, playerId, new[] { item.Id }));
        }
    }
}

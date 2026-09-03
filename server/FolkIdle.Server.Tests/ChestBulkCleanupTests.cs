using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The village chest's only drain, and the guard that stops it eating gear
    /// somebody is wearing.
    ///
    /// Modul: THE CHEST GREW FOREVER AND THAT IS WHAT MADE THE GAME LAG.
    ///
    /// Equipment drops on 15% of kills and every drop writes an
    /// EquipmentInstances row. Nothing removed one except a player clicking Sell
    /// on a single item, so the table only ever grew - measured on the live
    /// database at 17,836 rows for one account against 3 for a fresh one. At
    /// that size the inventory snapshot is 3.2 MB and the chest screen renders
    /// ~180,000 DOM nodes, so the ONE screen that could have cleaned it up was
    /// the screen the volume had made unusable.
    ///
    /// A per-item API cannot fix that; seventeen thousand round trips is not a
    /// remedy. These tests pin the bulk path that can, and the rules that keep
    /// it from being a foot-gun.
    /// </summary>
    [Collection("Postgres collection")]
    public class ChestBulkCleanupTests : IAsyncLifetime
    {
        private readonly PostgresTestFixture _fixture;
        private string _databaseName = string.Empty;
        private DbContextOptions<FolkIdleDbContext> _options = null!;

        public ChestBulkCleanupTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        // Its own database, for the reason DevFixtureInvariantTests records:
        // these tests DELETE equipment in bulk, and doing that in the shared
        // database would break whichever test runs next by an amount that
        // depends on ordering. An order-dependent failure is worse than an
        // outright one.
        public async Task InitializeAsync()
        {
            ContentRegistry.Initialize();

            _databaseName = $"chestbulk_{Guid.NewGuid():N}";

            var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString);
            await using (var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            builder.Database = _databaseName;
            _options = new DbContextOptionsBuilder<FolkIdleDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;

            await using var db = new FolkIdleDbContext(_options);
            await db.Database.MigrateAsync();
        }

        public async Task DisposeAsync()
        {
            await using var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// A player, a character, and `count` pieces spread across the tiers
        /// this test cares about. Returns the character so a caller can dress it.
        /// </summary>
        private async Task<(long PlayerId, CharacterRecord Character)> SeedAsync(
            FolkIdleDbContext db, string baseItemId, params int[] tiers)
        {
            long playerId = Random.Shared.NextInt64(900_000_000L, 999_999_999L);

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
            });

            var character = new CharacterRecord
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                SlotIndex = 0,
            };
            db.CharacterRecords.Add(character);

            foreach (int tier in tiers)
            {
                db.EquipmentInstances.Add(new EquipmentInstance
                {
                    PlayerId = playerId,
                    BaseItemId = baseItemId,
                    QualityTier = tier,
                    AffixPayload = "{}",
                });
            }

            await db.SaveChangesAsync();
            return (playerId, character);
        }

        [Fact]
        public async Task ASweepTakesEverythingUpToTheTierAndNothingAbove()
        {
            await using var db = new FolkIdleDbContext(_options);
            var (playerId, _) = await SeedAsync(db, "eq_steel_claymore_melee_weapon_slot_base",
                1, 1, 2, 3, 3, 4, 7, 9);

            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, playerId, maxQualityTier: 3, sell: true);

            Assert.Equal(5, outcome.RemovedCount);

            await using var verify = new FolkIdleDbContext(_options);
            var left = await verify.EquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == playerId)
                .Select(e => e.QualityTier)
                .OrderBy(t => t)
                .ToListAsync();

            Assert.Equal(new[] { 4, 7, 9 }, left);
        }

        [Fact]
        public async Task ASweepPaysExactlyWhatTheSameItemsWouldFetchOneAtATime()
        {
            // Modul: one valuation, shared. Selling from the chest one piece at
            // a time and sweeping the lot must pay the same, or the two become
            // two economies and the cheaper one becomes the only one anybody
            // uses. Both call ValueEquipment; this asserts they still agree.
            await using var db = new FolkIdleDbContext(_options);
            const string baseId = "eq_steel_claymore_melee_weapon_slot_base";
            var (playerId, _) = await SeedAsync(db, baseId, 1, 2, 3);

            long expected =
                VillageChestEngine.ValueEquipment(baseId, 1) +
                VillageChestEngine.ValueEquipment(baseId, 2) +
                VillageChestEngine.ValueEquipment(baseId, 3);

            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, playerId, maxQualityTier: 3, sell: true);

            Assert.Equal(expected, outcome.GoldGained);

            await using var verify = new FolkIdleDbContext(_options);
            long gold = await verify.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                .Select(c => c.Quantity)
                .FirstOrDefaultAsync();

            Assert.Equal(expected, gold);
        }

        [Fact]
        public async Task BinningPaysNothingButStillClearsTheRows()
        {
            await using var db = new FolkIdleDbContext(_options);
            var (playerId, _) = await SeedAsync(db, "eq_steel_claymore_melee_weapon_slot_base", 1, 2);

            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, playerId, maxQualityTier: 2, sell: false);

            Assert.Equal(2, outcome.RemovedCount);
            Assert.Equal(0L, outcome.GoldGained);

            await using var verify = new FolkIdleDbContext(_options);
            Assert.Equal(0, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
            Assert.Equal(0, await verify.CommodityRecords.CountAsync(c => c.PlayerId == playerId && c.ItemId == "gold"));
        }

        /// <summary>
        /// Modul: THE ELEVEN-SLOT TRUNCATION, AGAIN - and this time it could
        /// destroy a worn item.
        ///
        /// RemoveEquipmentAsync's worn-item guard listed the eight COMBAT slots
        /// and stopped at EquippedRingId, which is where every truncated list in
        /// this codebase has stopped. Tools are slots 8, 9 and 10, so a worn axe,
        /// pickaxe or rod could be sold or binned out from under the character
        /// holding it - leaving exactly the dangling equip pointer that
        /// EquipmentSlotEngine.ClearDanglingEquipReferencesAsync exists to heal.
        ///
        /// A Theory over all eleven, so the next list that stops at eight fails
        /// here rather than in a player's chest.
        /// </summary>
        [Theory]
        [InlineData("Weapon")]
        [InlineData("Helmet")]
        [InlineData("Chest")]
        [InlineData("Gloves")]
        [InlineData("Leggings")]
        [InlineData("Boots")]
        [InlineData("Amulet")]
        [InlineData("Ring")]
        [InlineData("Axe")]
        [InlineData("Pickaxe")]
        [InlineData("Rod")]
        public async Task ASweepNeverTakesGearACharacterIsWearing(string slotName)
        {
            await using var db = new FolkIdleDbContext(_options);
            var (playerId, character) = await SeedAsync(db, "eq_steel_claymore_melee_weapon_slot_base", 1, 1);

            long wornId = await db.EquipmentInstances.AsNoTracking()
                .Where(e => e.PlayerId == playerId)
                .Select(e => e.Id)
                .FirstAsync();

            var tracked = await db.CharacterRecords.FirstAsync(c => c.Id == character.Id);
            typeof(CharacterRecord).GetProperty($"Equipped{slotName}Id")!.SetValue(tracked, wornId);
            await db.SaveChangesAsync();

            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, playerId, maxQualityTier: 6, sell: true);

            Assert.Equal(1, outcome.RemovedCount);
            Assert.Equal(1, outcome.SkippedWornCount);

            await using var verify = new FolkIdleDbContext(_options);
            Assert.True(
                await verify.EquipmentInstances.AnyAsync(e => e.Id == wornId),
                $"the sweep destroyed the item worn in the {slotName} slot");
        }

        [Fact]
        public async Task ATierAboveTheCeilingSweepsNothingAtAll()
        {
            // Refused rather than clamped down to the ceiling. Clamping would
            // silently perform a destructive operation the caller did not ask
            // for, and there is no undo.
            await using var db = new FolkIdleDbContext(_options);
            var (playerId, _) = await SeedAsync(db, "eq_steel_claymore_melee_weapon_slot_base", 1, 2, 14);

            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, playerId, VillageChestEngine.MaxSweepableQualityTier + 1, sell: true);

            Assert.Equal(0, outcome.RemovedCount);
            Assert.Equal(0L, outcome.GoldGained);

            await using var verify = new FolkIdleDbContext(_options);
            Assert.Equal(3, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
        }

        [Fact]
        public void TheSweepCeilingKeepsLegendaryAndAboveOutOfReach()
        {
            // Modul: the number itself is the guarantee, so it is pinned here
            // rather than left to be read off a constant nobody revisits.
            // Legendary is tier 7; anything at or above it must never be
            // clearable in bulk or auto-salvaged, because those are the drops
            // the whole loop exists to produce and there is no undo.
            Assert.True(VillageChestEngine.MaxSweepableQualityTier < 7,
                "the bulk sweep must never be able to reach Legendary (tier 7) or above");
        }

        [Fact]
        public async Task ASweepOverAChestTheSizeOfTheLiveOneIsOneStatement()
        {
            // Modul: the shape that made this necessary. The worst live account
            // held 17,836 rows; a per-item loop over that is seventeen thousand
            // Serializable transactions. This asserts the bulk path handles a
            // comparable pile in one call and leaves nothing behind - the
            // measured figure on the dev box was 1,472 items in 131 ms.
            await using var db = new FolkIdleDbContext(_options);

            var tiers = new int[2000];
            for (int i = 0; i < tiers.Length; i++) tiers[i] = (i % 6) + 1;
            var (playerId, _) = await SeedAsync(db, "eq_steel_claymore_melee_weapon_slot_base", tiers);

            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, playerId, VillageChestEngine.MaxSweepableQualityTier, sell: true);

            Assert.Equal(2000, outcome.RemovedCount);
            Assert.True(outcome.GoldGained > 0L);

            await using var verify = new FolkIdleDbContext(_options);
            Assert.Equal(0, await verify.EquipmentInstances.CountAsync(e => e.PlayerId == playerId));
        }
    }
}

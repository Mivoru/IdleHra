using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The Chronicle pass against Postgres. XP is seeded as banked mission
    /// days - the pass's only input - and every reward is read back from its
    /// own table: diamonds, the gold row, loot chests, wardrobe chests, the
    /// bound avatar and frame, and the pet.
    /// </summary>
    [Collection("Postgres collection")]
    public class ChroniclePassTests
    {
        private readonly PostgresTestFixture _fixture;
        public ChroniclePassTests(PostgresTestFixture fixture) => _fixture = fixture;

        // Day 26 of season 1 (2026-11-15 + 25).
        private static readonly DateTime InSeasonOne = new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc);
        private static readonly ChronicleSeason Veles = ChronicleRegistry.Season(1);

        private async Task<long> SeedPlayerAsync()
        {
            string tag = Guid.NewGuid().ToString("N");
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Email = $"chronicle_{tag}@example.com",
                Username = "Chron" + tag[..8],
                DeviceId = "chronicle-device-" + tag,
            };
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        /// <summary>Banks <paramref name="xp"/> spread over season-1 days, as finished missions would.</summary>
        private async Task BankXpAsync(long playerId, int xp, int dayOffset = 0)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerMissionDays.Add(new PlayerMissionDay
            {
                PlayerId = playerId,
                DayKey = ChronicleRegistry.FirstSeasonDayKey + dayOffset,
                Actions = "18",
                XpEarned = xp,
                CreatedAtUtc = InSeasonOne,
            });
            await db.SaveChangesAsync();
        }

        private async Task<ChronicleEngine.ClaimResult> ClaimAsync(long playerId, int? level = null)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await ChronicleEngine.ClaimAsync(db, playerId, InSeasonOne, level);
        }

        [Fact]
        public async Task TheViewReadsTheMissionsXp()
        {
            long playerId = await SeedPlayerAsync();
            await BankXpAsync(playerId, 200, 0);
            await BankXpAsync(playerId, 150, 1);
            // A day BEFORE the season does not count.
            await BankXpAsync(playerId, 999, -1);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var view = await ChronicleEngine.ViewAsync(db, playerId, InSeasonOne);
            Assert.Equal(350, view.Xp);
            Assert.Equal(3, view.Level);
            Assert.Equal(50, view.XpIntoLevel);
            Assert.Equal(3, view.Claimable);
            Assert.Equal("Ready", view.Track[2].State);
            Assert.Equal("Locked", view.Track[3].State);
            Assert.Equal("Veles' Chronicle", view.SeasonName);
        }

        [Fact]
        public async Task ClaimingTheWholeTrackPaysEveryRewardOnce()
        {
            long playerId = await SeedPlayerAsync();
            await BankXpAsync(playerId, 5250);

            var result = await ClaimAsync(playerId);
            Assert.Equal(ChronicleEngine.ClaimOutcome.Ok, result.Outcome);
            Assert.Equal(50, result.LevelsClaimed.Count);
            Assert.Equal(2, result.OverflowChests);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var player = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
                Assert.Equal(300, player.PremiumDiamonds);

                long gold = await db.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold").Select(c => c.Quantity).SingleOrDefaultAsync();
                Assert.True(gold > 0, "gold levels paid nothing");
                Assert.Equal(result.Gold, gold);

                // 3 Gold + 2 Ancient on the track, plus 2 overflow Gold chests.
                int lootChests = await db.LootChests.CountAsync(c => c.PlayerId == playerId);
                Assert.Equal(7, lootChests);

                var cosmetics = await db.CosmeticItems.AsNoTracking().Where(c => c.PlayerId == playerId).ToListAsync();
                Assert.Equal(2, cosmetics.Count(c => c.Kind == (byte)CosmeticKind.Chest && c.Rarity == CosmeticRegistry.Legendary));
                Assert.Contains(cosmetics, c => c.DefinitionId == Veles.AvatarId);
                Assert.Contains(cosmetics, c => c.DefinitionId == Veles.FrameId);

                Assert.True(await db.PlayerPets.AnyAsync(p => p.PlayerId == playerId && p.PetId == Veles.PetId));
            }

            // Nothing is paid twice.
            var again = await ClaimAsync(playerId);
            Assert.Equal(ChronicleEngine.ClaimOutcome.NothingToClaim, again.Outcome);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(300, (await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId)).PremiumDiamonds);
            }

            // One more overflow chest's worth of XP pays exactly one more.
            await BankXpAsync(playerId, 100, 1);
            var overflow = await ClaimAsync(playerId);
            Assert.Equal(1, overflow.OverflowChests);
            Assert.Empty(overflow.LevelsClaimed);
        }

        [Fact]
        public async Task ASingleLevelCanBeClaimedOnItsOwn_AndALockedOneCannot()
        {
            long playerId = await SeedPlayerAsync();
            await BankXpAsync(playerId, 300);

            var three = await ClaimAsync(playerId, 3);
            Assert.Equal(new[] { 3 }, three.LevelsClaimed);
            Assert.Equal(15, three.Diamonds);

            var locked = await ClaimAsync(playerId, 4);
            Assert.Equal(ChronicleEngine.ClaimOutcome.NothingToClaim, locked.Outcome);

            var rest = await ClaimAsync(playerId);
            Assert.Equal(new[] { 1, 2 }, rest.LevelsClaimed.OrderBy(l => l));
        }
    }
}

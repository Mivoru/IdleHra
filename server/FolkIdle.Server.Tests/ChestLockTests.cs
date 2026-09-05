using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// "Locked" has to mean the same thing everywhere, and it did not.
    ///
    /// `EquipmentInstance.IsAffixLocked` was honoured by the affix reroll and
    /// by forge fusion, and ignored by BOTH removal paths - so a locked item
    /// could not be CHANGED, but could still be sold, binned, or swallowed
    /// whole by the bulk sweep. That is not a distinction any player would draw
    /// from the word "locked".
    ///
    /// The sweep is what makes it matter. It deletes every unworn piece at or
    /// below a quality tier in one statement, and its rarity ceiling of 6 was
    /// the only thing standing between a player and a favourite Epic sword.
    ///
    /// NOTHING CAN SET THE FLAG YET - see docs/audit_2026_09_05.md finding A,
    /// where the whole lock feature is read in ten places and written in none.
    /// These tests set it directly, which is the only way to prove the
    /// behaviour is in place BEFORE the feature that needs it is built, rather
    /// than being remembered on the day.
    /// </summary>
    [Collection("Postgres collection")]
    public class ChestLockTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public ChestLockTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const long TestPlayerId = 980_000_202L;

        /// <summary>Two carried pieces at the same tier: one locked, one not.</summary>
        private async Task<(long Locked, long Loose)> SeedPairAsync(int qualityTier)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var stale = db.EquipmentInstances.Where(e => e.PlayerId == TestPlayerId);
            db.EquipmentInstances.RemoveRange(stale);
            await db.SaveChangesAsync();

            var locked = new EquipmentInstance
            {
                PlayerId = TestPlayerId,
                BaseItemId = "eq_steel_claymore_melee_weapon_slot_base",
                QualityTier = qualityTier,
                AffixPayload = "{}",
                IsAffixLocked = true,
            };
            var loose = new EquipmentInstance
            {
                PlayerId = TestPlayerId,
                BaseItemId = "eq_steel_claymore_melee_weapon_slot_base",
                QualityTier = qualityTier,
                AffixPayload = "{}",
                IsAffixLocked = false,
            };

            db.EquipmentInstances.Add(locked);
            db.EquipmentInstances.Add(loose);
            await db.SaveChangesAsync();
            return (locked.Id, loose.Id);
        }

        private async Task<int> CountCarriedAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.EquipmentInstances.CountAsync(e => e.PlayerId == TestPlayerId);
        }

        [Fact]
        public async Task ALockedPieceIsNeverSoldOrBinnedOneAtATime()
        {
            var (lockedId, looseId) = await SeedPairAsync(qualityTier: 3);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            var refused = await VillageChestEngine.RemoveEquipmentAsync(db, TestPlayerId, lockedId, sell: true);
            Assert.Equal(VillageChestEngine.ChestActionResult.Locked, refused.Result);
            Assert.Equal(0L, refused.GoldGained);

            // And the unlocked twin still sells, so this is the lock and not a
            // broken removal path.
            var allowed = await VillageChestEngine.RemoveEquipmentAsync(db, TestPlayerId, looseId, sell: true);
            Assert.Equal(VillageChestEngine.ChestActionResult.Success, allowed.Result);

            Assert.Equal(1, await CountCarriedAsync());
        }

        [Fact]
        public async Task ALockedPieceSurvivesTheBULKSWEEP_whichIsWhatTheLockIsFor()
        {
            const int tier = 3;
            await SeedPairAsync(tier);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, TestPlayerId, maxQualityTier: VillageChestEngine.MaxSweepableQualityTier, sell: true);

            _output.WriteLine($"swept {outcome.RemovedCount} for {outcome.GoldGained} gold");

            Assert.Equal(1, outcome.RemovedCount);
            Assert.True(outcome.GoldGained > 0);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var survivors = await verify.EquipmentInstances
                .AsNoTracking()
                .Where(e => e.PlayerId == TestPlayerId)
                .ToListAsync();

            Assert.Single(survivors);
            Assert.True(survivors[0].IsAffixLocked, "the sweep kept the wrong one");
        }

        [Fact]
        public async Task TheSweepsGoldMatchesWhatItActuallyDeleted()
        {
            // Modul: THE DELETE REPEATS THE SELECT'S PREDICATE rather than
            // listing the ids it just read - which is correct inside one
            // Serializable transaction, and a trap the moment the two
            // predicates differ by a single term.
            //
            // Adding the lock to the SELECT and forgetting the DELETE would
            // have deleted locked pieces while paying for none of them: gold
            // for two items, three items gone. Caught while writing it, pinned
            // here.
            await SeedPairAsync(qualityTier: 2);

            int before = await CountCarriedAsync();

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var outcome = await VillageChestEngine.RemoveEquipmentUpToTierAsync(
                db, TestPlayerId, maxQualityTier: VillageChestEngine.MaxSweepableQualityTier, sell: true);

            int after = await CountCarriedAsync();

            Assert.Equal(outcome.RemovedCount, before - after);
        }
    }
}

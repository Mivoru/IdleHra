using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 26: the drop record - loot_tier_daily_counts for every piece
    /// created, notable_item_events for everything Legendary+ and every
    /// forge/trophy. Each test that starts a loot worker stops it (1ef5318):
    /// the queues are static, and a leftover worker drains other tests' loot.
    /// </summary>
    [Collection("Postgres collection")]
    public class DropRecordTests
    {
        private readonly PostgresTestFixture _fixture;

        public DropRecordTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private async Task<PlayerRecord> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"droprec_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player;
        }

        /// <summary>Runs one request through a real worker and waits for its counts to land.</summary>
        private async Task RunAsync(CombatLootDropRequest request)
        {
            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            try
            {
                CombatLootEngine.DropRequestQueue.Enqueue(request);
                engine.StartCron();

                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                    await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
                    if (await check.LootTierDailyCounts.AsNoTracking().AnyAsync(c => c.PlayerId == request.PlayerId)) return;
                }
                Assert.Fail("the loot worker wrote no drop record within 60 seconds");
            }
            finally
            {
                engine.StopCron();
            }
        }

        [Fact]
        public async Task AnOfflineBatch_CountsEveryPiece_InAtMostOneRowPerTier()
        {
            var player = await CreatePlayerAsync();
            await RunAsync(new CombatLootDropRequest
            {
                PlayerId = player.Id,
                MonsterId = ContentRegistry.FirstCanonicalMonsterId,
                Kills = 5000,
                SkipMaterialRoll = true,
                Source = DropSource.Offline,
            });

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var counts = await db.LootTierDailyCounts.AsNoTracking().Where(c => c.PlayerId == player.Id).ToListAsync();
            int pieces = await db.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == player.Id);

            Assert.All(counts, c => Assert.Equal((short)DropSource.Offline, c.Source));
            Assert.All(counts, c => Assert.Equal(1, c.RegionTier));
            // O(tiers), not O(kills): one row per tier hit, and one upsert.
            Assert.True(counts.Count <= 14, $"{counts.Count} count rows for one region and one source");
            Assert.Equal(pieces, counts.Sum(c => c.Count));
            Assert.True(pieces > 500, $"5,000 kills at 15% produced only {pieces} pieces");

            // Every Legendary+ piece has a row, pointing at the piece.
            int notablePieces = await db.EquipmentInstances.AsNoTracking()
                .CountAsync(e => e.PlayerId == player.Id && e.QualityTier >= RarityTier.Legendary);
            var notables = await db.NotableItemEvents.AsNoTracking().Where(e => e.PlayerId == player.Id).ToListAsync();
            Assert.Equal(notablePieces, notables.Count);
            Assert.All(notables, n => Assert.NotNull(n.EquipmentInstanceId));
        }

        [Fact]
        public async Task AutoSalvagedDrops_AreCounted_AsSalvaged_AndGetNoRow()
        {
            var player = await CreatePlayerAsync();
            await RunAsync(new CombatLootDropRequest
            {
                PlayerId = player.Id,
                MonsterId = ContentRegistry.FirstCanonicalMonsterId,
                Kills = 300,
                SkipMaterialRoll = true,
                AutoSalvageBelowTier = Domain.Economy.CraftingEngine.RarityTierCount,
            });

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var counts = await db.LootTierDailyCounts.AsNoTracking().Where(c => c.PlayerId == player.Id).ToListAsync();
            Assert.NotEmpty(counts);
            Assert.All(counts, c => Assert.Equal(c.Count, c.SalvagedCount));
            Assert.All(counts, c => Assert.Equal((short)DropSource.LiveKill, c.Source)); // Source 0 means a live kill
            Assert.False(await db.NotableItemEvents.AsNoTracking().AnyAsync(e => e.PlayerId == player.Id));
            Assert.False(await db.EquipmentInstances.AsNoTracking().AnyAsync(e => e.PlayerId == player.Id));
        }

        [Fact]
        public async Task ALiftedPiece_ShowsFinalAboveRolled()
        {
            // Thirteen bonus tiers lifts anything the roll produces to 14, so
            // every piece is notable and every row must show the lift.
            var player = await CreatePlayerAsync();
            await RunAsync(new CombatLootDropRequest
            {
                PlayerId = player.Id,
                MonsterId = ContentRegistry.FirstCanonicalMonsterId,
                Kills = 100,
                SkipMaterialRoll = true,
                BonusRarityTiers = 13,
                LootLuckPct = 42f,
            });

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var notables = await db.NotableItemEvents.AsNoTracking().Where(e => e.PlayerId == player.Id).ToListAsync();
            Assert.NotEmpty(notables);
            Assert.All(notables, n =>
            {
                Assert.Equal(14, n.FinalTier);
                Assert.True(n.FinalTier >= n.RolledTier);
                Assert.Equal(42f, n.LootLuckPct);
            });
            Assert.Contains(notables, n => n.FinalTier > n.RolledTier);
        }

        [Fact]
        public async Task AFailedDrop_LeavesNoCount_AndTheOutboxReplayRecordsIt()
        {
            var player = await CreatePlayerAsync();

            // A transaction that throws AFTER the drop record's upsert, so the
            // upsert has to be rolled back rather than simply never reached.
            await using (var setup = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await setup.Database.ExecuteSqlRawAsync(@"
                    CREATE OR REPLACE FUNCTION pg_test_poison_notable_droprec() RETURNS trigger AS $BODY$
                    BEGIN
                      IF NEW.""PlayerId"" = " + player.Id + @" THEN
                        RAISE EXCEPTION 'poisoned for test (DropRecordTests)';
                      END IF;
                      RETURN NEW;
                    END;
                    $BODY$ LANGUAGE plpgsql;
                    DROP TRIGGER IF EXISTS pg_test_poison_notable_droprec_trigger ON notable_item_events;
                    CREATE TRIGGER pg_test_poison_notable_droprec_trigger
                    BEFORE INSERT ON notable_item_events
                    FOR EACH ROW EXECUTE FUNCTION pg_test_poison_notable_droprec();");
            }

            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            try
            {
                CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
                {
                    PlayerId = player.Id,
                    MonsterId = ContentRegistry.FirstCanonicalMonsterId,
                    Kills = 100,
                    SkipMaterialRoll = true,
                    BonusRarityTiers = 13, // every piece notable, so the poisoned insert always runs
                });
                engine.StartCron();

                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                    await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
                    if (await check.PendingGrants.AsNoTracking().AnyAsync(g => g.PlayerId == player.Id)) break;
                }
            }
            finally
            {
                engine.StopCron();
                await using var cleanup = await _fixture.DbContextFactory.CreateDbContextAsync();
                await cleanup.Database.ExecuteSqlRawAsync(
                    "DROP TRIGGER IF EXISTS pg_test_poison_notable_droprec_trigger ON notable_item_events; " +
                    "DROP FUNCTION IF EXISTS pg_test_poison_notable_droprec();");
            }

            int grants;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.False(await db.LootTierDailyCounts.AsNoTracking().AnyAsync(c => c.PlayerId == player.Id),
                    "a rolled-back drop left its count behind");
                var rows = await db.PendingGrants.Where(g => g.PlayerId == player.Id).ToListAsync();
                grants = rows.Count(r => r.PayloadKind == PendingGrantPayloadKind.EquipmentGrant);
                Assert.True(grants > 0);

                await using var transaction = await db.Database.BeginTransactionAsync();
                foreach (var row in rows) Assert.True(await PendingGrantOutbox.TryApplyOneAsync(db, row));
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var counts = await db.LootTierDailyCounts.AsNoTracking().Where(c => c.PlayerId == player.Id).ToListAsync();
                Assert.All(counts, c => Assert.Equal((short)DropSource.OutboxRetry, c.Source));
                Assert.Equal(grants, counts.Sum(c => c.Count));
                Assert.All(counts, c => Assert.Equal(1, c.RegionTier));
                var notables = await db.NotableItemEvents.AsNoTracking().Where(e => e.PlayerId == player.Id).ToListAsync();
                Assert.Equal(grants, notables.Count);
                Assert.All(notables, n => Assert.Equal((short)DropSource.OutboxRetry, n.Source));
            }
        }

        /// <summary>
        /// "Grep for a WRITER as well as a reader": every file that creates an
        /// EquipmentInstances row must call DropRecord, or be one of the four
        /// deliberately excluded (DropRecord.cs says why).
        /// </summary>
        [Fact]
        public void EveryCreationSite_RecordsOrIsExcludedOnPurpose()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "FolkIdle.Server"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string serverRoot = Path.Combine(dir!.FullName, "FolkIdle.Server");

            string[] excluded = { "MarketEscrowEngine.cs", "MailboxAndBankEngine.cs", "StarterEquipmentGrant.cs", "DevFixtureSeeder.cs" };
            string dropRecordSource = File.ReadAllText(Path.Combine(serverRoot, "Engine", "DropRecord.cs"));
            var creation = new Regex(@"EquipmentInstances\.Add\(|new (Models\.)?EquipmentInstance\b");

            foreach (string file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "Migrations" + Path.DirectorySeparatorChar)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                string source = File.ReadAllText(file);
                if (!creation.IsMatch(source)) continue;

                string name = Path.GetFileName(file);
                if (excluded.Contains(name))
                {
                    Assert.Contains(Path.GetFileNameWithoutExtension(name), dropRecordSource);
                    continue;
                }
                Assert.True(source.Contains("DropRecord."),
                    $"{name} creates equipment and records nothing - call DropRecord, or exclude it in DropRecord.cs and here");
            }
        }
    }
}

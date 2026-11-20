using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The diamond ledger (2026-11-20) against a real Postgres: the
    /// PlayerRecords trigger records every change, the writer's label names
    /// it, and a change nobody labelled still lands, as "unlabelled".
    /// </summary>
    [Collection("Postgres collection")]
    public class DiamondLedgerPostgresTests
    {
        private readonly PostgresTestFixture _fixture;

        public DiamondLedgerPostgresTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task<long> CreatePlayerAsync(int diamonds)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"dia_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 10,
                PremiumDiamonds = diamonds,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        private async Task<DiamondDaily[]> RowsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.DiamondDaily.AsNoTracking().Where(d => d.PlayerId == playerId).OrderBy(d => d.Reason).ToArrayAsync();
        }

        [Fact]
        public async Task ALabelledGainAndSpendLandUnderTheirReasons()
        {
            long id = await CreatePlayerAsync(100);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await DiamondLedger.LabelAsync(db, DiamondLedger.Dungeon);
                var p = await db.PlayerRecords.SingleAsync(x => x.Id == id);
                p.PremiumDiamonds += 40;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await DiamondLedger.LabelAsync(db, DiamondLedger.Inheritance);
                var p = await db.PlayerRecords.SingleAsync(x => x.Id == id);
                p.PremiumDiamonds -= 25;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            var rows = await RowsAsync(id);
            Assert.Equal(2, rows.Length);
            var dungeon = rows.Single(r => r.Reason == DiamondLedger.Dungeon);
            Assert.Equal((40L, 0L), (dungeon.Gained, dungeon.Spent));
            var inheritance = rows.Single(r => r.Reason == DiamondLedger.Inheritance);
            Assert.Equal((0L, 25L), (inheritance.Gained, inheritance.Spent));
        }

        [Fact]
        public async Task AnUnlabelledChangeStillLandsAndTheLabelDoesNotOutliveItsTransaction()
        {
            long id = await CreatePlayerAsync(10);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    await DiamondLedger.LabelAsync(db, DiamondLedger.Mail);
                    var p = await db.PlayerRecords.SingleAsync(x => x.Id == id);
                    p.PremiumDiamonds += 5;
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }

                // Same context and connection, no transaction and no label.
                await db.PlayerRecords.Where(x => x.Id == id)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.PremiumDiamonds, x => x.PremiumDiamonds + 3));
            }

            var rows = await RowsAsync(id);
            Assert.Equal(5L, rows.Single(r => r.Reason == DiamondLedger.Mail).Gained);
            Assert.Equal(3L, rows.Single(r => r.Reason == "unlabelled").Gained);
        }

        [Fact]
        public async Task NavPayoutOutsideATransactionIsStillLabelled()
        {
            long id = await CreatePlayerAsync(0);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await NavEngine.ApplyPayoutAsync(db, id, new NavPayout(0, 7));
            }

            var rows = await RowsAsync(id);
            Assert.Equal(7L, Assert.Single(rows, r => r.Reason == DiamondLedger.Nav).Gained);
        }

        [Fact]
        public async Task ARosterUpdateLabelledInItsOwnCommandIsCounted()
        {
            long id = await CreatePlayerAsync(0);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    DiamondLedger.LabelSql(DiamondLedger.GuildWar) +
                    @"UPDATE ""PlayerRecords"" SET ""PremiumDiamonds"" = ""PremiumDiamonds"" + {1} WHERE ""Id"" = {0}",
                    id, 9);
            }

            var rows = await RowsAsync(id);
            Assert.Equal(9L, Assert.Single(rows, r => r.Reason == DiamondLedger.GuildWar).Gained);
        }
            /// <summary>
        /// The defect the ledger found on its first run (2026-11-20): an engine
        /// grant written to the row while the player was online was overwritten
        /// at the next checkpoint by the payload's older absolute balance. The
        /// checkpoint banks the tick's own drops as a delta now, so both survive.
        /// </summary>
        [Fact]
        public async Task AnEngineGrantWhileOnlineSurvivesTheCheckpoint_AndTheTicksDropsBankOnce()
        {
            long id = await CreatePlayerAsync(500);
            await using (var setup = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                setup.CommodityRecords.Add(new CommodityRecord { PlayerId = id, ItemId = "gold", Quantity = 1_000 });
                setup.CharacterRecords.Add(new CharacterRecord { Id = Guid.NewGuid(), PlayerId = id, AgePhase = 1, SlotIndex = 0 });
                await setup.SaveChangesAsync();
            }

            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            try
            {
            var holder = new[] { await manager.LoadPlayerState(id) };
            Assert.Equal(500, holder[0].PremiumCurrency);

            // The tick mints three drops...
            holder[0].SetPremiumCurrency(holder[0].PremiumCurrency + 3);
            holder[0].PendingDiamondDelta += 3;
            holder[0].IsDirty = true;

            // ...while an engine (a Delve bank) adds 20 to the row with no sync.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await DiamondLedger.LabelAsync(db, DiamondLedger.Delve);
                var p = await db.PlayerRecords.SingleAsync(x => x.Id == id);
                p.PremiumDiamonds += 20;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            manager.RequestFlush(ref holder[0], FlushReason.Periodic);
            Assert.Equal(0L, holder[0].PendingDiamondDelta); // it went with the job
            await manager.WhenWriterIdleAsync();
            manager.ApplyPendingAcks(ref holder[0]);

            // A second checkpoint with nothing new adds nothing.
            manager.RequestFlush(ref holder[0], FlushReason.Periodic);
            await manager.WhenWriterIdleAsync();
            manager.ApplyPendingAcks(ref holder[0]);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                Assert.Equal(523, (await db.PlayerRecords.AsNoTracking().SingleAsync(x => x.Id == id)).PremiumDiamonds);
            }
            var rows = await RowsAsync(id);
            Assert.Equal(20L, rows.Single(r => r.Reason == DiamondLedger.Delve).Gained);
            var checkpoint = rows.Single(r => r.Reason == DiamondLedger.Checkpoint);
            Assert.Equal((3L, 0L), (checkpoint.Gained, checkpoint.Spent));
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }
    }
}

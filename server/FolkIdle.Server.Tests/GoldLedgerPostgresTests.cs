using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 79: the ledger's two writes against a real Postgres - the daily
    /// row by category, the lifetime column, and that a rollback takes both.
    /// </summary>
    [Collection("Postgres collection")]
    public class GoldLedgerPostgresTests
    {
        private readonly PostgresTestFixture _fixture;

        public GoldLedgerPostgresTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<long> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"ledger_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        [Fact]
        public async Task SpendsAddUpByCategory_AndIntoTheLifetimeTotal()
        {
            long playerId = await CreatePlayerAsync();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Reroll, 1_000);
                await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Reroll, 2_000);
                await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Village, 50_000);
                await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Fusion, 0); // nothing
                await tx.CommitAsync();
            }

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await check.GoldSpendDaily.Where(g => g.PlayerId == playerId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(3_000, rows.Single(r => r.Category == (short)GoldSpendCategory.Reroll).Amount);
            Assert.Equal(50_000, rows.Single(r => r.Category == (short)GoldSpendCategory.Village).Amount);

            var player = await check.PlayerRecords.SingleAsync(p => p.Id == playerId);
            Assert.Equal(53_000, player.LifetimeGoldSpent);
        }

        [Fact]
        public async Task ARolledBackSpend_LeavesNoTrace()
        {
            long playerId = await CreatePlayerAsync();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Breeding, 9_999);
                await tx.RollbackAsync();
            }

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await check.GoldSpendDaily.AnyAsync(g => g.PlayerId == playerId));
            Assert.Equal(0, (await check.PlayerRecords.SingleAsync(p => p.Id == playerId)).LifetimeGoldSpent);
        }
    }
}

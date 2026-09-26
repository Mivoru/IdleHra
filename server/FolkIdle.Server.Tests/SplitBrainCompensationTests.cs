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
    /// Task 42: the split-brain refusal mails a flat SplitBrainCompensationGold
    /// ONCE per (PlayerId, DbEpoch). It used to mail epochDelta * 500 on every
    /// refused flush - uncapped, and paid again for every race.
    /// </summary>
    [Collection("Postgres collection")]
    public class SplitBrainCompensationTests
    {
        private readonly PostgresTestFixture _fixture;

        public SplitBrainCompensationTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task SeedPlayerAsync(long playerId, long epoch)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM split_brain_incidents WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MailboxInstances\" WHERE \"PlayerId\" = {0}", playerId);
            var existing = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
            if (existing != null) db.PlayerRecords.Remove(existing);
            await db.SaveChangesAsync();

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                LogicEpochCounter = epoch,
                BaseStrength = 50,
                BaseDexterity = 50,
                BaseConstitution = 50,
                BaseLuck = 25
            });
            await db.SaveChangesAsync();
        }

        private async Task SetDbEpochAsync(long playerId, long epoch)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerRecords\" SET \"LogicEpochCounter\" = {0} WHERE \"Id\" = {1}", epoch, playerId);
        }

        private async Task<MailboxInstance[]> CompensationMailAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.MailboxInstances.AsNoTracking()
                .Where(m => m.PlayerId == playerId && m.BaseItemId == "GOLD_COMPENSATION")
                .ToArrayAsync();
        }

        private static TickStatePayload StaleState(long playerId, long epoch) => new TickStatePayload
        {
            PlayerId = playerId,
            LogicEpochCounter = epoch,
            InventorySpaceRemaining = 20,
            STR = 50, DEX = 50, CON = 50, LCK = 25
        };

        [Fact]
        public async Task TheSameStaleStateFlushedTwiceMailsOnce_ANewDbEpochMailsAgain()
        {
            const long playerId = 982000001L;
            await SeedPlayerAsync(playerId, epoch: 50);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);

            // Epoch delta of 40: the old formula would have mailed 20,000.
            Assert.False(await manager.FlushState(StaleState(playerId, 10)));
            await manager.LastSplitBrainCompensation;
            Assert.False(await manager.FlushState(StaleState(playerId, 10)));
            await manager.LastSplitBrainCompensation;

            var mail = await CompensationMailAsync(playerId);
            Assert.Single(mail);
            Assert.Equal(StateCheckpointManager.SplitBrainCompensationGold, mail[0].GoldAttachment);

            // A second incident: the database has moved on to a new epoch.
            await SetDbEpochAsync(playerId, 60);
            Assert.False(await manager.FlushState(StaleState(playerId, 10)));
            await manager.LastSplitBrainCompensation;

            mail = await CompensationMailAsync(playerId);
            Assert.Equal(2, mail.Length);
            Assert.All(mail, m => Assert.Equal(1000, m.GoldAttachment));

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var epochs = await db.SplitBrainIncidents.AsNoTracking()
                .Where(i => i.PlayerId == playerId)
                .OrderBy(i => i.DbEpoch)
                .Select(i => i.DbEpoch)
                .ToArrayAsync();
            Assert.Equal(new long[] { 50, 60 }, epochs);
        }
    }
}

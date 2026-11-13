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
    /// Buying an Ancestral Boon, against a real database (2026-11-13). Nothing
    /// tested LegacyStoreEngine's perk purchase before - it had no client either.
    /// </summary>
    [Collection("Postgres collection")]
    public class AncestralBoonsPurchaseTests
    {
        private readonly PostgresTestFixture _fixture;

        public AncestralBoonsPurchaseTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task SeedAsync(long playerId, int shards, long perks = 0L)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), LegacyPerks = perks });
            int eraId = await db.SeasonalEraRecords.Where(e => e.IsActive).Select(e => e.EraId).FirstOrDefaultAsync();
            db.PlayerLegacyLedgers.Add(new PlayerLegacyLedger { PlayerId = playerId, EraId = eraId, LegacyShardBalance = shards });
            await db.SaveChangesAsync();
        }

        private async Task<(long Perks, long Shards)> ReadAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long perks = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.LegacyPerks).SingleAsync();
            long shards = await db.PlayerLegacyLedgers.AsNoTracking().Where(l => l.PlayerId == playerId).SumAsync(l => (long)l.LegacyShardBalance);
            return (perks, shards);
        }

        private byte? LastResultFor(long playerId)
            => _fixture.PlayerRegistry.CommandResultQueue.Where(r => r.PlayerId == playerId).Select(r => (byte?)r.ResultCode).LastOrDefault();

        [Fact]
        public async Task ABoonRankCostsItsPrice_AndSaysSo()
        {
            const long id = 960_113_001L;
            await SeedAsync(id, 1_000);
            var engine = new LegacyStoreEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await engine.PurchaseLegacyUnlockAsync(id, LegacyStoreEngine.XpMultiplierPerkUnlockId, 0);

            var (perks, shards) = await ReadAsync(id);
            Assert.Equal(1, LegacyPerkResolver.GetXpBonusPct(perks));
            Assert.Equal(1_000 - LegacyPerkResolver.CalculatePerkRankCost(LegacyPerkResolver.XpMultiplierBitOffset, 0), shards);
            Assert.Equal((byte)FolkIdle.Server.Network.CommandResultCode.BoonPurchased, LastResultFor(id));
        }

        [Fact]
        public async Task TooFewShardsChangesNothing_AndSaysSo()
        {
            const long id = 960_113_002L;
            await SeedAsync(id, 100);
            var engine = new LegacyStoreEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await engine.PurchaseLegacyUnlockAsync(id, LegacyStoreEngine.CombatSpeedPerkUnlockId, 0);

            var (perks, shards) = await ReadAsync(id);
            Assert.Equal(0, LegacyPerkResolver.GetCombatSpeedBonusPct(perks));
            Assert.Equal(100, shards);
            Assert.Equal((byte)FolkIdle.Server.Network.CommandResultCode.BoonInsufficientShards, LastResultFor(id));
        }

        [Fact]
        public async Task AMaxedBoonRefusesWithoutSpending()
        {
            const long id = 960_113_003L;
            long maxed = LegacyPerkResolver.SetPerkRank(0L, LegacyPerkResolver.CombatSpeedBitOffset, LegacyPerkResolver.CombatSpeedMaxRank);
            await SeedAsync(id, 1_000_000, maxed);
            var engine = new LegacyStoreEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            await engine.PurchaseLegacyUnlockAsync(id, LegacyStoreEngine.CombatSpeedPerkUnlockId, 0);

            var (perks, shards) = await ReadAsync(id);
            Assert.Equal(LegacyPerkResolver.CombatSpeedMaxRank, LegacyPerkResolver.GetPerkRank(perks, LegacyPerkResolver.CombatSpeedBitOffset));
            Assert.Equal(1_000_000, shards);
            Assert.Equal((byte)FolkIdle.Server.Network.CommandResultCode.BoonMaxed, LastResultFor(id));
        }
    }
}

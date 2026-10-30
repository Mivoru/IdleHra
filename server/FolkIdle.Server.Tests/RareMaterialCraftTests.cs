using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Owner decision R1 (2026-10-30): a recipe that takes a rare material
    /// crafts one quality step higher, with one more affix, than the same
    /// piece from common materials. The Golden Birch Axe used to come out with
    /// exactly the Birch Axe's stats.
    /// </summary>
    [Collection("Postgres collection")]
    public class RareMaterialCraftTests
    {
        private readonly PostgresTestFixture _fixture;
        private static long _nextPlayerId = 971_600_000L;

        public RareMaterialCraftTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void TheRareMaterialsAreTheTablesGoldenLogsRareOresAndRareCatch()
        {
            Assert.True(VillageManagementEngine.IsRareMaterial("golden_birch_log"));
            Assert.True(VillageManagementEngine.IsRareMaterial("malachite_ore"));
            Assert.True(VillageManagementEngine.IsRareMaterial("shimmering_trout"));
            Assert.True(VillageManagementEngine.IsRareMaterial("fern_flower"));
            Assert.False(VillageManagementEngine.IsRareMaterial("yarrow"));
            Assert.False(VillageManagementEngine.IsRareMaterial("birch_log"));
            Assert.False(VillageManagementEngine.IsRareMaterial("copper_ore"));
            Assert.False(VillageManagementEngine.IsRareMaterial(""));
        }

        [Fact]
        public async Task TheGoldenAxeCraftsAStepAboveTheBirchAxe_WithOneMoreAffix()
        {
            long playerId = Interlocked.Increment(ref _nextPlayerId);
            var recipes = ContentRegistry.Recipes.ToArray();
            var birch = recipes.First(r => ContentRegistry.GetItemBaseId(r.ResultItemId) == "birch_axe_tool");
            var golden = recipes.First(r => ContentRegistry.GetItemBaseId(r.ResultItemId) == "golden_birch_axe_tool");

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
                await CraftingEngine.GrantCraftedOutputAsync(db, playerId, birch, 1);
                await CraftingEngine.GrantCraftedOutputAsync(db, playerId, golden, 1);
                await db.SaveChangesAsync();
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var pieces = await verify.EquipmentInstances.AsNoTracking().Where(e => e.PlayerId == playerId).ToListAsync();
            var b = pieces.Single(p => p.BaseItemId == "birch_axe_tool");
            var g = pieces.Single(p => p.BaseItemId == "golden_birch_axe_tool");
            Assert.Equal(RarityTier.Normal, b.QualityTier);
            Assert.Equal(RarityTier.Normal + 1, g.QualityTier);
            int affixes(EquipmentInstance e) => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, int>>(e.AffixPayload)!.Count;
            Assert.Equal(RarityTier.GetAffixCount(RarityTier.Normal), affixes(b));
            Assert.Equal(Math.Min(AffixRegistry.MaxAffixCount, RarityTier.GetAffixCount(RarityTier.Normal + 1) + 1), affixes(g));
        }
    }
}

using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Refine (roadmap 3.4): an affix keeps its type, its rarity and its place,
    /// and only its value moves - within 20% either way and inside its
    /// rarity's legal range - for a quarter of a reroll's price, recorded in
    /// the gold ledger as Refine. Manual only.
    /// </summary>
    [Collection("Postgres collection")]
    public class AffixRefineTests
    {
        private readonly PostgresTestFixture _fixture;
        private static long _nextPlayerId = 971_500_000L;

        public AffixRefineTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void TheValueMovesAtMostTwentyPercent_AndStaysInsideItsRarity()
        {
            Assert.True(AffixRegistry.TryGetDefinition("melee_dmg_pct", out var dmg));
            var rng = new Random(5);
            (int min, int max) = AffixRegistry.CalculateMagnitudeRange(dmg, 3, AffixRarity.Rare);
            for (int i = 0; i < 2000; i++)
            {
                int current = rng.Next(min, max + 1);
                int refined = AffixRerollEngine.RefineMagnitude(dmg, 3, AffixRarity.Rare, current, rng);
                Assert.InRange(refined, min, max);
                Assert.InRange(refined, (int)Math.Floor(current * 0.8), (int)Math.Ceiling(current * 1.2));
            }

            Assert.Equal(250, AffixRerollEngine.RefineCost(1000));
            Assert.Equal(1, AffixRerollEngine.RefineCost(1));
        }

        [Fact]
        public async Task ARefineKeepsTypeRarityAndPlace_ChargesAQuarter_AndIsLedgeredAsRefine()
        {
            long playerId = Interlocked.Increment(ref _nextPlayerId);
            long itemId;
            const string weapon = "eq_steel_claymore_melee_weapon_slot_base";
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = 1_000_000L });
                var item = new EquipmentInstance
                {
                    BaseItemId = weapon,
                    PlayerId = playerId,
                    QualityTier = 6,
                    AffixPayload = "{\"crit_dmg_pct@3\":100,\"melee_dmg_pct@4\":60,\"lifesteal_pct@2\":8}",
                };
                db.EquipmentInstances.Add(item);
                await db.SaveChangesAsync();
                itemId = item.Id;
            }

            var engine = new AffixRerollEngine(_fixture.ServiceProvider);
            await engine.ExecuteRerollAsync(playerId, itemId, affixIndex: 1, operation: RerollOperation.Refine);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var after = await verify.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == itemId);
            var keys = JsonNode.Parse(after.AffixPayload)!.AsObject().Select(kv => kv.Key).ToArray();
            Assert.Equal(new[] { "crit_dmg_pct@3", "melee_dmg_pct@4", "lifesteal_pct@2" }, keys);
            int refined = JsonNode.Parse(after.AffixPayload)!["melee_dmg_pct@4"]!.GetValue<int>();
            Assert.InRange(refined, 48, 72);

            int region = ContentRegistry.ItemDefinitions.ToArray().First(d => ContentRegistry.GetItemBaseId(d.Id) == weapon).RegionTier;
            long expected = AffixRerollEngine.RefineCost(AffixRegistry.CalculateRerollGoldCost(region, 0, rerollStatType: false));
            long gold = (await verify.CommodityRecords.AsNoTracking().SingleAsync(c => c.PlayerId == playerId && c.ItemId == "gold")).Quantity;
            Assert.Equal(1_000_000L - expected, gold);
            Assert.True(await verify.GoldSpendDaily.AnyAsync(g => g.PlayerId == playerId && g.Category == (short)GoldSpendCategory.Refine && g.Amount == expected));
        }

        [Fact]
        public async Task AnAutoRefineIsRefused()
        {
            var engine = new AffixRerollEngine(_fixture.ServiceProvider);
            var result = await engine.ExecuteAutoRerollAsync(1, 1, 0, RerollOperation.Refine,
                new AutoRerollStopCondition(AffixRarity.Legendary, null), 10);
            Assert.Equal(AutoRerollStopReason.RejectedUnreachableCondition, result.Reason);
        }
    }
}

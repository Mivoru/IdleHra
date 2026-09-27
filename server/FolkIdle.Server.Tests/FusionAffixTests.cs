using System;
using System.Linq;
using System.Text.Json.Nodes;
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
    /// Reported 2026-09-27: "the affix a fusion adds cannot be rerolled" - it
    /// showed as "Range Dmg Pct 7bda +23". The forge wrote its own key shape,
    /// "&lt;id&gt;_&lt;4 hex&gt;", which the reroll refused and the stat
    /// totals never matched, so the affix did nothing at all.
    /// </summary>
    [Collection("Postgres collection")]
    public class FusionAffixTests
    {
        private const string Leggings = "eq_linen_trousers_leggings_armor_slot_base";
        private readonly PostgresTestFixture _fixture;

        public FusionAffixTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        private static void AssertCanonicalAndLegal(string key, string baseItemId)
        {
            string id = AffixRegistry.StripStackSuffix(key);
            Assert.True(AffixRegistry.TryGetDefinition(id, out _), $"{key} is not a registry affix");
            Assert.Contains('@', key);
            Assert.False(AffixRegistry.TryStripLegacyFusionSuffix(key, out _), $"{key} is the old forge shape");

            int[] legal = new int[16];
            int count = AffixRegistry.GetLegalAffixIndices(AffixRegistry.ResolveSlot(baseItemId), legal);
            var legalIds = new System.Collections.Generic.List<string>();
            for (int k = 0; k < count; k++) legalIds.Add(AffixRegistry.Definitions[legal[k]].Id);
            Assert.Contains(id, legalIds);
        }

        [Fact]
        public void TheOldForgeKeys_ReadAsTheAffixTheyAre()
        {
            Assert.Equal("range_dmg_pct", AffixRegistry.StripStackSuffix("range_dmg_pct_7bda"));
            Assert.Equal("armor_pen_flat", AffixRegistry.StripStackSuffix("armor_pen_flat_37f6"));
            // Canonical keys are untouched, and so is anything that is not a
            // registered id plus exactly four hex digits.
            Assert.Equal("flat_hp", AffixRegistry.StripStackSuffix("flat_hp#2@4"));
            Assert.Equal("flat_hp_7bdz", AffixRegistry.StripStackSuffix("flat_hp_7bdz"));
            Assert.Equal("nonsense_7bda", AffixRegistry.StripStackSuffix("nonsense_7bda"));
        }

        [Fact]
        public void OneAdditionalAffix_IsCanonical_LegalForTheSlot_AndNeverOverwrites()
        {
            var keys = new System.Collections.Generic.List<string> { "flat_hp@5", "flat_armor@3" };
            for (int i = 0; i < 40; i++)
            {
                Assert.True(AffixRegistry.TryRollOneAdditional(Leggings, 2, 8, keys, out string key, out int magnitude));
                AssertCanonicalAndLegal(key, Leggings);
                Assert.DoesNotContain(key, keys);
                Assert.True(magnitude > 0);
                keys.Add(key);
            }
        }

        [Fact]
        public async Task AFusion_AddsARerollableAffix()
        {
            long targetId, sac1Id, sac2Id;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var forge = await db.VillageInfrastructures.SingleOrDefaultAsync(
                    v => v.PlayerId == DbSeeder.PlayerHighId && v.BuildingId == VillageManagementEngine.ForgeBuildingId);
                if (forge is null)
                {
                    db.VillageInfrastructures.Add(new VillageInfrastructure
                    {
                        PlayerId = DbSeeder.PlayerHighId,
                        BuildingId = VillageManagementEngine.ForgeBuildingId,
                        CurrentLevel = ForgeSplicingEngine.MaxQualityTier,
                    });
                }
                else if (forge.CurrentLevel < ForgeSplicingEngine.MaxQualityTier)
                {
                    forge.CurrentLevel = ForgeSplicingEngine.MaxQualityTier;
                }

                var target = new EquipmentInstance
                {
                    PlayerId = DbSeeder.PlayerHighId, BaseItemId = Leggings, QualityTier = 3,
                    AffixPayload = "{\"flat_hp@3\":100,\"flat_armor@3\":80}",
                };
                var sac1 = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = Leggings, QualityTier = 3 };
                var sac2 = new EquipmentInstance { PlayerId = DbSeeder.PlayerHighId, BaseItemId = Leggings, QualityTier = 3 };
                db.EquipmentInstances.AddRange(target, sac1, sac2);
                await db.SaveChangesAsync();
                (targetId, sac1Id, sac2Id) = (target.Id, sac1.Id, sac2.Id);
            }

            var result = await new ForgeSplicingEngine(_fixture.ServiceProvider)
                .ExecuteFusionAsync(DbSeeder.PlayerHighId, targetId, sac1Id, sac2Id);
            Assert.Equal(ForgeSplicingResult.Success, result);

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var fused = await check.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == targetId);
            var payload = (JsonObject)JsonNode.Parse(fused.AffixPayload!)!;
            Assert.Equal(3, payload.Count);
            Assert.Equal(100, (int)payload["flat_hp@3"]!);
            Assert.Equal(80, (int)payload["flat_armor@3"]!);
            string added = payload.Select(p => p.Key).Single(k => k != "flat_hp@3" && k != "flat_armor@3");
            AssertCanonicalAndLegal(added, Leggings);
        }

        [Fact]
        public async Task AnAffixTheOldForgeWrote_CanBeRerolled_AndComesBackCanonical()
        {
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"fusaffix_{Guid.NewGuid():N}".Substring(0, 20),
            };
            long itemId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(player);
                await db.SaveChangesAsync();
                await CommodityLedger.AddAsync(db, player.Id, "gold", 50_000_000L);
                var item = new EquipmentInstance
                {
                    PlayerId = player.Id, BaseItemId = Leggings, QualityTier = 10,
                    // The owner's own item, as production holds it.
                    AffixPayload = "{\"flat_hp@5\":2577,\"dodge_chance_pct@5\":18,\"range_dmg_pct_7bda\":23}",
                };
                db.EquipmentInstances.Add(item);
                await db.SaveChangesAsync();
                itemId = item.Id;
            }

            var outcome = await new AffixRerollEngine(_fixture.ServiceProvider)
                .ExecuteRerollAsync(player.Id, itemId, affixIndex: 2);
            Assert.True(outcome.DidCommit, $"the reroll was refused ({outcome.FailureCode})");

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rerolled = await check.EquipmentInstances.AsNoTracking().SingleAsync(e => e.Id == itemId);
            var keys = ((JsonObject)JsonNode.Parse(rerolled.AffixPayload!)!).Select(p => p.Key).ToList();
            Assert.Equal(3, keys.Count);
            Assert.Equal("flat_hp@5", keys[0]);
            Assert.Equal("dodge_chance_pct@5", keys[1]);
            AssertCanonicalAndLegal(keys[2], Leggings);
        }
    }
}

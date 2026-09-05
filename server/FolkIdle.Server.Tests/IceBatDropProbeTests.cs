using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Probe: a live account with 25,635 Ice Bat (106) kills has NEVER received
    /// one mat_frozen_wing and stopped receiving equipment entirely, while its
    /// gathering grants keep landing. Region 3 has a passing test already
    /// (Test_CombatLootEngine_IndependentRollsCanGrantMaterialsAndEquipmentTogether,
    /// monster 104), so this asks the same question of EVERY canonical monster.
    /// </summary>
    [Collection("Postgres collection")]
    public class IceBatDropProbeTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _o;

        public IceBatDropProbeTests(PostgresTestFixture fixture, ITestOutputHelper o)
        {
            _fixture = fixture;
            _o = o;
            ContentRegistry.Initialize();
        }

        [Fact]
        public async Task EveryCanonicalMonsterGrantsWhatItsTablesPromise()
        {
            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var process = typeof(CombatLootEngine).GetMethod("ProcessMonsterLootDropAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var report = new StringBuilder();
            report.AppendLine("monster  name                 region  gear  mats");
            var barren = new System.Collections.Generic.List<string>();

            for (int offset = 0; offset < 25; offset++)
            {
                int monsterId = 91 + offset;
                long playerId = 971000000L + monsterId;

                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    db.PlayerRecords.Add(new PlayerRecord
                    {
                        Id = playerId, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid()
                    });
                    await db.SaveChangesAsync();
                }

                for (int i = 0; i < 200; i++)
                {
                    await (Task)process.Invoke(engine,
                        new object[] { playerId, monsterId, 0f, 0f, 0, 1, false, 0 })!;
                }

                await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
                int gear = await verify.EquipmentInstances.AsNoTracking().CountAsync(e => e.PlayerId == playerId);
                long mats = await verify.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .SumAsync(c => (long?)c.Quantity) ?? 0L;

                report.AppendLine(
                    $"{monsterId,7}  {ContentRegistry.GetMonsterName(monsterId),-20} {ContentRegistry.GetMonsterRegionTier(monsterId),6}  {gear,4}  {mats,4}");

                // 200 kills at 15% gear and 35% materials. Zero of either is not
                // bad luck, it is a dead path.
                if (gear == 0) barren.Add($"{monsterId} {ContentRegistry.GetMonsterName(monsterId)}: NO GEAR");
                if (mats == 0) barren.Add($"{monsterId} {ContentRegistry.GetMonsterName(monsterId)}: NO MATERIALS");
            }

            _o.WriteLine(report.ToString());
            Assert.True(barren.Count == 0, string.Join("; ", barren));
        }
    }
}

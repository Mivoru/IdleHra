using System;
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
    /// The loot worker must keep draining its queue whatever one request does,
    /// and every monster must actually grant what its tables promise.
    ///
    /// Written after loot stopped for every player on the live server while
    /// kills, XP, gold, the codex and gathering all kept working. Nothing was
    /// wrong with the roll, the tables or the drop chance - all three test green
    /// here and did then. CombatLootEngine.ExecuteAsync had no exception
    /// handling at all, and the two calls that acquire a database connection sit
    /// outside the try inside ProcessMonsterLootDropAsync, so one
    /// EMAXCONNSESSION from Supabase's 15-client session pooler ended the
    /// worker's task for the life of the process. Silently.
    /// </summary>
    [Collection("Postgres collection")]
    public class LootWorkerResilienceTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _o;

        public LootWorkerResilienceTests(PostgresTestFixture fixture, ITestOutputHelper o)
        {
            _fixture = fixture;
            _o = o;
            ContentRegistry.Initialize();
        }

        [Fact]
        public async Task AFailingRequestDoesNotStopTheQueue()
        {
            // 8_000_000_001 has no PlayerRecords row, so its equipment insert
            // violates the foreign key and the request fails. The one behind it
            // is valid and MUST still be paid: what this pins is not one lost
            // drop, it is a queue that used to stop forever after the first
            // failure of any kind.
            const long poisonPlayerId = 8_000_000_001L;
            const long goodPlayerId = 972_000_001L;
            const int monsterId = 104;

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = goodPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid()
                });
                await db.SaveChangesAsync();
            }

            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);

            CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
            {
                PlayerId = poisonPlayerId,
                MonsterId = monsterId,
                Kills = 200
            });
            CombatLootEngine.DropRequestQueue.Enqueue(new CombatLootDropRequest
            {
                PlayerId = goodPlayerId,
                MonsterId = monsterId,
                Kills = 200
            });

            engine.StartCron();

            var deadline = DateTime.UtcNow.AddSeconds(60);
            int granted = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
                await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
                granted = await check.EquipmentInstances.AsNoTracking()
                    .CountAsync(e => e.PlayerId == goodPlayerId);
                if (granted > 0) break;
            }

            Assert.True(granted > 0,
                "the request behind a failing one was never processed - the worker died on the failure");
        }

        [Fact]
        public async Task EveryCanonicalMonsterGrantsWhatItsTablesPromise()
        {
            // 200 kills at a 15% equipment and 35% material chance. A zero here
            // is not bad luck, it is a dead path - a monster with an empty table
            // can be farmed forever and pay nothing, and the only symptom is a
            // player saying drops are broken.
            var engine = new CombatLootEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var process = typeof(CombatLootEngine).GetMethod(
                "ProcessMonsterLootDropAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var report = new StringBuilder();
            report.AppendLine("monster  name                 region  gear  mats");
            var barren = new System.Collections.Generic.List<string>();

            for (int offset = 0; offset < 5 * ContentRegistry.MonstersPerRegion; offset++)
            {
                int monsterId = ContentRegistry.FirstCanonicalMonsterId + offset;
                long playerId = 973_000_000L + monsterId;

                await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
                {
                    db.PlayerRecords.Add(new PlayerRecord
                    {
                        Id = playerId,
                        PlayerGuid = Guid.NewGuid(),
                        AuthenticatorToken = Guid.NewGuid()
                    });
                    await db.SaveChangesAsync();
                }

                for (int i = 0; i < 200; i++)
                {
                    await (Task)process.Invoke(engine,
                        new object[] { playerId, monsterId, 0f, 0f, 0, 1, false, 0, 0f })!;
                }

                await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
                int gear = await verify.EquipmentInstances.AsNoTracking()
                    .CountAsync(e => e.PlayerId == playerId);
                long mats = await verify.CommodityRecords.AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .SumAsync(c => (long?)c.Quantity) ?? 0L;

                report.AppendLine(
                    $"{monsterId,7}  {ContentRegistry.GetMonsterName(monsterId),-20} "
                    + $"{ContentRegistry.GetMonsterRegionTier(monsterId),6}  {gear,4}  {mats,4}");

                if (gear == 0) barren.Add($"{monsterId} {ContentRegistry.GetMonsterName(monsterId)}: NO GEAR");
                if (mats == 0) barren.Add($"{monsterId} {ContentRegistry.GetMonsterName(monsterId)}: NO MATERIALS");
            }

            _o.WriteLine(report.ToString());
            Assert.True(barren.Count == 0, string.Join("; ", barren));
        }
    }
}
